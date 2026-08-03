using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Transport.InMemory;

/// <summary>
/// 프로세스 안에서 <see cref="Pipe"/> 두 개로 이어진 양방향 커넥션.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 소켓 없이 <b>프레이밍·디스패치·핸들러 전 경로</b>를 돌린다.
/// 그래서 (1) 종단 테스트가 포트 충돌·방화벽·TIME_WAIT 없이 밀리초 단위로 돌고,
/// (2) 무엇보다 <see cref="IConnection"/> 추상화가 <b>정말로 전송 중립인지</b>를 증명한다.
/// 구현이 하나뿐인 추상화는 가설일 뿐이다(CLAUDE.md 3장).
/// </para>
/// <para>
/// <b>구조.</b> 파이프 두 개를 서로 엇갈려 묶는다.
/// </para>
/// <code>
///   클라이언트 Output ──▶ [clientToServer] ──▶ 서버 Input
///   클라이언트 Input  ◀── [serverToClient] ◀── 서버 Output
/// </code>
/// <para>
/// <b>백프레셔가 진짜로 동작한다.</b> <see cref="Pipe"/>의 일시정지·재개 임계값이 그대로
/// 적용되므로, 소비자가 느리면 생산자의 <c>FlushAsync</c>가 실제로 대기한다.
/// 무한 버퍼로 만들면 테스트가 프로덕션과 다른 동작을 하게 되고,
/// 그러면 이 전송으로 검증한 것이 아무 의미가 없다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Input"/>은 읽기 루프 하나가, <see cref="Output"/>은
/// 쓰기 경로 하나가 소유한다. 그 둘은 서로 다른 스레드여도 된다.
/// <see cref="Abort"/>와 <see cref="DisposeAsync"/>는 어느 스레드에서 몇 번을 불러도 안전하다.
/// </para>
/// <para>
/// <b>수명.</b> 짝을 이루는 두 커넥션은 서로를 참조하지 않는다. 한쪽이 닫히면
/// 자기 쪽 파이프 끝을 완료 처리하고, 상대는 그것을 <c>IsCompleted</c> 로 관측한다 —
/// 상호 참조를 두면 순환 참조와 이중 해제가 생긴다.
/// </para>
/// </remarks>
public sealed class InMemoryConnection : IConnection, IConnectionEndPointFeature
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly CancellationTokenSource _closed = new();

    /// <summary>0 = 열림, 1 = 닫힘. <see cref="Interlocked"/>로만 바꾼다.</summary>
    private int _closedFlag;

    internal InMemoryConnection(
        ConnectionId id,
        PipeReader input,
        PipeWriter output,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
    {
        Id = id;
        _input = input;
        _output = output;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;

        FeatureCollection features = new(capacity: 1);
        features.Set<IConnectionEndPointFeature>(this);
        Features = features;
    }

    /// <inheritdoc />
    public ConnectionId Id { get; }

    /// <inheritdoc />
    public PipeReader Input => _input;

    /// <inheritdoc />
    public PipeWriter Output => _output;

    /// <inheritdoc />
    public IFeatureCollection Features { get; }

    /// <inheritdoc />
    public CancellationToken ConnectionClosed => _closed.Token;

    /// <inheritdoc />
    /// <remarks>인메모리 종단이므로 <see cref="InMemoryEndPoint"/>다.</remarks>
    public EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>마지막으로 기록된 종료 사유.</summary>
    /// <remarks>진단용이다. 아직 닫히지 않았으면 기본값.</remarks>
    public ConnectionCloseInfo CloseInfo { get; private set; }

    /// <inheritdoc />
    public void Abort(in ConnectionCloseInfo info)
    {
        // 여러 번 불러도 안전해야 한다(IConnection 계약). 첫 호출만 이유를 기록한다.
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
        {
            return;
        }

        CloseInfo = info;

        // 대기 중인 송신 데이터를 보장하지 않는다 — 그것이 Abort 와 DisposeAsync 의 차이다.
        _input.CancelPendingRead();
        _input.Complete();
        _output.Complete();

        SignalClosed();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 정상 종료다. 남은 송신 데이터를 내보낸 뒤 파이프를 완료한다.
    /// 상대는 <c>ReadResult.IsCompleted</c> 로 이것을 관측한다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
        {
            _closed.Dispose();
            return;
        }

        if (CloseInfo.Reason == CloseReason.None)
        {
            CloseInfo = new ConnectionCloseInfo(CloseReason.ServerClosed);
        }

        try
        {
            await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 종료 경로에서 예외를 던지면 나머지 정리가 실행되지 않는다.
        catch (Exception)
        {
            // 상대가 이미 읽기를 닫았으면 플러시가 실패할 수 있다. 정상 경로다.
        }
#pragma warning restore CA1031

        await _output.CompleteAsync().ConfigureAwait(false);
        await _input.CompleteAsync().ConfigureAwait(false);

        SignalClosed();
        _closed.Dispose();
    }

    /// <summary>취소 토큰을 발화시킨다.</summary>
    /// <remarks>
    /// <see cref="CancellationTokenSource.Cancel()"/>은 등록된 콜백을 <b>동기적으로</b> 실행하고,
    /// 그중 하나가 예외를 던지면 <see cref="AggregateException"/>이 올라온다.
    /// 종료 경로가 그 예외로 중단되면 나머지 자원이 정리되지 않으므로 여기서 막는다.
    /// </remarks>
    private void SignalClosed()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 해제됐다. 닫힌 상태라는 목적은 달성됐다.
        }
        catch (AggregateException)
        {
            // 취소 콜백이 던진 예외. 이 커넥션의 종료를 막을 이유가 없다.
        }
    }
}
