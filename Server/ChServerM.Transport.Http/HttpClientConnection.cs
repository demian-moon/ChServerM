using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Transport.Http;

/// <summary>
/// 진행 중인 HTTP/2 양방향 스트림 하나를 <see cref="IConnection"/> 으로 비추는 클라이언트 쪽 어댑터.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 서버 쪽과 대칭이다(ADR-0057) — <see cref="Output"/> 에 쓴 바이트는
/// 요청 본문으로 흘러가고, <see cref="Input"/> 은 응답 본문을 읽는다. 클라이언트도 서버와
/// 같은 프레이밍·디스패치 계층을 그대로 쓴다(<c>IClientTransport</c> 계약).
/// </para>
/// <para>
/// <b>정상 종료 = 요청 본문 완료.</b> <see cref="DisposeAsync"/> 가 <see cref="Output"/> 을
/// 완료하면 펌프가 끝나고 <c>END_STREAM</c> 이 나간다(반닫힘). 서버 핸들러는 그것을 EOF 로
/// 관측하고 응답을 완료한다. <see cref="Abort"/> 는 응답 메시지를 폐기해 스트림을 리셋한다
/// (<c>RST_STREAM</c>) — 대기 중 송신 데이터를 보장하지 않는다는 계약 그대로다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Input"/> 은 읽기 루프 하나가, <see cref="Output"/> 은 쓰기
/// 경로 하나가 소유한다. <see cref="Abort"/> 와 <see cref="DisposeAsync"/> 는 어느 스레드에서
/// 몇 번을 불러도 안전하다.
/// </para>
/// </remarks>
internal sealed class HttpClientConnection : IConnection, IConnectionEndPointFeature
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly HttpResponseMessage _response;
    private readonly TimeSpan _drainTimeout;
    private readonly CancellationTokenSource _closed = new();

    /// <summary>0 = 열림, 1 = 종료 진입(<see cref="Abort"/> 또는 <see cref="DisposeAsync"/> 첫 호출).</summary>
    private int _closedFlag;

    /// <summary>0 = 미완료, 1 = 파이프 완료 처리 끝. 완료는 정확히 한 번만 한다.</summary>
    private int _completed;

    internal HttpClientConnection(
        ConnectionId id,
        PipeReader input,
        PipeWriter output,
        HttpResponseMessage response,
        EndPoint remoteEndPoint,
        TimeSpan drainTimeout)
    {
        Id = id;
        _input = input;
        _output = output;
        _response = response;
        _drainTimeout = drainTimeout;
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
    /// <remarks>스트림은 커넥션 하나가 독점하지 않으므로 로컬 종단을 특정하지 않는다.</remarks>
    public EndPoint? LocalEndPoint => null;

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>마지막으로 기록된 종료 사유.</summary>
    /// <remarks>진단용이다. 아직 닫히지 않았으면 기본값.</remarks>
    public ConnectionCloseInfo CloseInfo { get; private set; }

    /// <inheritdoc />
    public void Abort(in ConnectionCloseInfo info)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) == 0)
        {
            CloseInfo = info;
        }

        // 깨우기·폐기 모두 이미 죽은 스트림에서 던질 수 있다 — Abort 는 예외를 내지
        // 않는 것이 계약이다(서버 쪽 커넥션과 같은 방어, 2026-08-11 실측).
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

        try
        {
            // 응답 폐기 = RST_STREAM. 같은 HTTP/2 연결의 다른 스트림에는 영향이 없다.
            _response.Dispose();
        }
        catch (Exception)
        {
            // 이미 폐기됐다.
        }
#pragma warning restore CA1031

        SignalClosed();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        bool graceful = Interlocked.Exchange(ref _closedFlag, 1) == 0;

        if (graceful && CloseInfo.Reason == CloseReason.None)
        {
            CloseInfo = new ConnectionCloseInfo(CloseReason.ClientClosed);
        }

        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        if (graceful)
        {
            try
            {
                // 드레인에는 반드시 상한이 있다(인메모리·TCP 와 같은 규약).
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

        // 요청 본문 완료(END_STREAM 송신) → 서버가 EOF 를 보고 응답을 완료한다.
        // 입력 완료는 응답 스트림 폐기를 겸하므로 스트림이 이미 리셋됐으면 던질 수 있다 —
        // 종료 경로의 예외는 종료를 막을 이유가 없다(서버 쪽 커넥션과 같은 방어).
#pragma warning disable CA1031
        try
        {
            await _output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 이미 닫혔다. 목적은 달성됐다.
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

        // 파이프 완료 이후에 폐기한다 — 읽기 루프가 살아 있는 동안 응답 스트림을
        // 빼앗으면 정상 종료가 리셋으로 관측된다.
        _response.Dispose();
    }

    /// <summary>취소 토큰을 발화시킨다.</summary>
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
