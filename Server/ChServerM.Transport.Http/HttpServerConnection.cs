using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Transport.Http;

/// <summary>
/// HTTP 요청(스트림) 하나를 <see cref="IConnection"/> 으로 비추는 서버 쪽 어댑터.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 이 전송의 핵심 결정 — <b>HTTP/2 스트림 하나 = 커넥션 하나</b>(ADR-0057) —
/// 를 코드로 만드는 지점이다. <see cref="Input"/> 은 요청 본문, <see cref="Output"/> 은 응답
/// 본문이다. 이 대응 덕분에 프레이밍·디스패치·핸들러 전 경로가 <b>TCP 와 완전히 같은
/// 코드로</b> HTTP 위에서 돈다.
/// </para>
/// <para>
/// <b>Abort 는 RST 가 아니라 스트림 종료다.</b> HTTP/2 의 <c>RST_STREAM</c> 은 상대 측에서
/// 프로토콜 예외로 표면화되어, 같은 종료를 TCP 는 조용한 EOF 로 관측하고 HTTP 는 예외로
/// 관측하는 전송별 차이를 만든다. 그래서 여기의 <see cref="Abort"/> 는 대기자만 깨우고,
/// 파이프 완료(= <c>END_STREAM</c>)는 <see cref="DisposeAsync"/> 가 맡는다 —
/// 인메모리·TCP 커넥션과 같은 순서다(2026-08-04 감사 H2 와 같은 규약).
/// </para>
/// <para>
/// <b>수명.</b> 전송의 요청 처리 루프가 만들고, 핸들러가 끝나면 같은 루프의 <c>finally</c> 가
/// <see cref="DisposeAsync"/> 를 부른다. 그 시점에 응답 본문이 완료되어 Kestrel 이 스트림을
/// 정상 종료한다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Input"/> 은 읽기 루프 하나가, <see cref="Output"/> 은 쓰기
/// 경로 하나가 소유한다. <see cref="Abort"/> 와 <see cref="DisposeAsync"/> 는 어느 스레드에서
/// 몇 번을 불러도 안전하다.
/// </para>
/// </remarks>
internal sealed class HttpServerConnection : IConnection, IConnectionEndPointFeature
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly TimeSpan _drainTimeout;
    private readonly CancellationTokenSource _closed;

    /// <summary>0 = 열림, 1 = 종료 진입(<see cref="Abort"/> 또는 <see cref="DisposeAsync"/> 첫 호출).</summary>
    private int _closedFlag;

    /// <summary>0 = 미완료, 1 = 파이프 완료 처리 끝. 완료는 정확히 한 번만 한다.</summary>
    private int _completed;

    internal HttpServerConnection(
        ConnectionId id,
        PipeReader input,
        PipeWriter output,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        TimeSpan drainTimeout,
        CancellationToken requestAborted)
    {
        Id = id;
        _input = input;
        _output = output;
        _drainTimeout = drainTimeout;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;

        // 클라이언트가 스트림을 리셋하면(RST_STREAM) Kestrel 의 RequestAborted 가 발화한다.
        // 그것이 곧 이 커넥션의 종료이므로 하나의 토큰으로 잇는다 — 취소 원천을 둘로
        // 만들지 않는다(IConnection 계약).
        _closed = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);

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
    public EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>마지막으로 기록된 종료 사유.</summary>
    /// <remarks>진단용이다. 아직 닫히지 않았으면 기본값.</remarks>
    public ConnectionCloseInfo CloseInfo { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// 대기자만 깨운다. 파이프 완료는 <see cref="DisposeAsync"/> 가 맡는다 —
    /// 소유하지 않은 파이프 끝을 여기서 완료하면 디스패치에서 돌아온 읽기 루프의
    /// <c>AdvanceTo</c> 가 던진다(인메모리 커넥션과 같은 규약).
    /// </remarks>
    public void Abort(in ConnectionCloseInfo info)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) == 0)
        {
            CloseInfo = info;
        }

        // 재호출에도 깨우기는 반복한다 — 종료 드레인에 걸린 플러시를 나중에 온
        // Abort(예: StopAsync 의 강제 종료)가 깨울 수 있어야 한다.
        //
        // ⚠ 깨우기도 던질 수 있다 — Kestrel 의 요청 본문 리더는 스트림이 리셋된 뒤에는
        // CancelPendingRead 에서도 IOException 을 던진다. Abort 는 "이미 닫힌 커넥션에
        // 호출해도 예외를 던지지 않는다"가 계약이므로 여기서 막는다(2026-08-11 실측).
#pragma warning disable CA1031
        try
        {
            _input.CancelPendingRead();
        }
        catch (Exception)
        {
            // 이미 리셋됐다. 깨울 대기자가 없다.
        }

        try
        {
            _output.CancelPendingFlush();
        }
        catch (Exception)
        {
            // 위와 같다.
        }
#pragma warning restore CA1031

        SignalClosed();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 정상 종료다. 남은 송신 데이터를 <b>상한 시간 안에서</b> 내보낸 뒤 파이프를 완료한다.
    /// 응답 본문 완료가 곧 <c>END_STREAM</c> 이고, 상대는 <c>ReadResult.IsCompleted</c> 로
    /// 이것을 관측한다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        bool graceful = Interlocked.Exchange(ref _closedFlag, 1) == 0;

        if (graceful && CloseInfo.Reason == CloseReason.None)
        {
            CloseInfo = new ConnectionCloseInfo(CloseReason.ServerClosed);
        }

        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        if (graceful)
        {
            try
            {
                // 드레인에는 반드시 상한이 있다 — 상대가 읽지 않으면(흐름 제어 윈도 소진)
                // 이 플러시가 영원히 끝나지 않는다. 인메모리 전송의 감사 H3 과 같은 결함 부류.
                using CancellationTokenSource drainLimit = new(_drainTimeout);
                await _output.FlushAsync(drainLimit.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 종료 경로에서 예외를 던지면 나머지 정리가 실행되지 않는다.
            catch (Exception)
            {
                // 시간 초과·상대 닫힘 — 드레인을 포기하고 정리를 계속한다.
            }
#pragma warning restore CA1031
        }

        // 응답 본문 완료 = END_STREAM. 요청 본문 완료 = 남은 수신 바이트 폐기.
        //
        // ⚠ 둘 다 던질 수 있다 — Kestrel 의 요청 본문 리더는 클라이언트가 스트림을 리셋했으면
        // CompleteAsync 에서 IOException 을 던진다. 여기서 새어 나가면 Kestrel 의 요청 처리
        // 루프 밖으로 빠져 **프로세스가 죽는다**(2026-08-11 실측 — 테스트 호스트 크래시).
        // 종료 경로의 예외는 종료를 막을 이유가 없다.
#pragma warning disable CA1031
        try
        {
            await _output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 스트림이 이미 리셋됐다. 닫힌 상태라는 목적은 달성됐다.
        }

        try
        {
            await _input.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 위와 같다.
        }
#pragma warning restore CA1031

        SignalClosed();
        _closed.Dispose();
    }

    /// <summary>취소 토큰을 발화시킨다.</summary>
    /// <remarks>
    /// 취소 콜백의 예외가 종료 경로를 중단시키지 않게 여기서 막는다
    /// (인메모리 커넥션과 같은 방어).
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
