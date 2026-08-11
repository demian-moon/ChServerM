using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;
using WS = System.Net.WebSockets.WebSocket;

namespace ChServerM.Transport.WebSocket;

/// <summary>
/// 수립된 WebSocket 하나를 <see cref="IConnection"/> 으로 비추는 어댑터. 서버·클라이언트 공용.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 메시지 경계를 버리는 결정이 여기 있다(ADR-0059).</b> WebSocket 은 메시지
/// 지향이지만 <see cref="IConnection"/> 은 바이트 스트림이다. 이 어댑터는 바이너리 메시지의
/// 페이로드를 <b>이어 붙인 바이트 스트림</b>으로 취급한다 — 메시지 경계는 신뢰하지도
/// 전달하지도 않는다. 경계는 프레이밍 축이 긋는 것이고, 전송이 경계를 알게 되는 순간
/// 두 축의 독립 교체(ADR-0002)가 깨진다. 덕분에 같은 핸들러·프레이밍 코드가 TCP·HTTP
/// 와 동일하게 돈다.
/// </para>
/// <para>
/// <b>구조.</b> 내부 유계 파이프 2개 + 펌프 2개다.
/// </para>
/// <code>
///   소켓 ReceiveAsync ──▶ [수신 파이프] ──▶ Input (읽기 루프가 소유)
///   Output (쓰기 경로가 소유) ──▶ [송신 파이프] ──▶ 소켓 SendAsync
/// </code>
/// <para>
/// 수신 파이프의 일시정지 임계값이 <b>이 전송의 백프레셔</b>다 — 소비가 멈추면 수신 펌프의
/// <c>FlushAsync</c> 가 멈추고, 소켓을 더 읽지 않으므로 TCP 흐름 제어가 상대를 멈춘다.
/// </para>
/// <para>
/// <b>수명.</b> 정상 종료(<see cref="DisposeAsync"/>)는 남은 송신을 상한 안에서 내보내고
/// Close 프레임(<c>CloseOutputAsync</c>)으로 반닫힘을 알린다 — 상대는 EOF 로 관측한다.
/// <see cref="Abort"/> 는 소켓을 즉시 중단한다. 상대 쪽 수신 펌프는 어떤 이유로 끝나든
/// 수신 파이프를 <b>정상 완료(EOF)</b> 한다 — 전송에 따라 "EOF vs 예외"가 갈리면 전송
/// 중립 검증이 깨진다(HTTP 전송의 Abort 결정과 같은 자리, ADR-0057 결정 5).
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Input"/> 은 읽기 루프 하나가, <see cref="Output"/> 은 쓰기
/// 경로 하나가 소유한다. 펌프 2개는 이 타입이 소유한 백그라운드 태스크다.
/// <see cref="Abort"/> 와 <see cref="DisposeAsync"/> 는 어느 스레드에서 몇 번을 불러도 안전하다.
/// </para>
/// </remarks>
internal sealed class WebSocketDuplexConnection : IConnection, IConnectionEndPointFeature
{
    /// <summary>수신 펌프가 소켓에 요청하는 최소 버퍼. 파이프 블록 크기와 맞춘다.</summary>
    private const int MinReceiveBufferSize = 4096;

    private readonly WS _webSocket;
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly Task _receivePump;
    private readonly Task _sendPump;
    private readonly TimeSpan _drainTimeout;
    private readonly CancellationTokenSource _closed = new();

    /// <summary>0 = 열림, 1 = 종료 진입(<see cref="Abort"/> 또는 <see cref="DisposeAsync"/> 첫 호출).</summary>
    private int _closedFlag;

    /// <summary>0 = 미완료, 1 = 파이프 완료 처리 끝. 완료는 정확히 한 번만 한다.</summary>
    private int _completed;

    internal WebSocketDuplexConnection(
        ConnectionId id,
        WS webSocket,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        WebSocketTransportOptions options)
    {
        Id = id;
        _webSocket = webSocket;
        _drainTimeout = options.ShutdownTimeout;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;

        _receivePipe = new Pipe(options.CreatePipeOptions());
        _sendPipe = new Pipe(options.CreatePipeOptions());

        FeatureCollection features = new(capacity: 1);
        features.Set<IConnectionEndPointFeature>(this);
        Features = features;

        // 펌프는 생성 즉시 돈다. 첫 await 에서 바로 비동기로 넘어가므로 생성자를 붙잡지 않는다.
        _receivePump = Task.Run(ReceivePumpAsync);
        _sendPump = Task.Run(SendPumpAsync);
    }

    /// <inheritdoc />
    public ConnectionId Id { get; }

    /// <inheritdoc />
    public PipeReader Input => _receivePipe.Reader;

    /// <inheritdoc />
    public PipeWriter Output => _sendPipe.Writer;

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

    /// <summary>두 펌프가 모두 끝나면 완료된다. 전송의 드레인 판정용.</summary>
    internal Task PumpsCompleted => Task.WhenAll(_receivePump, _sendPump);

    /// <summary>소켓 → 수신 파이프. 메시지 경계를 버리고 바이트를 이어 붙인다.</summary>
    private async Task ReceivePumpAsync()
    {
        PipeWriter writer = _receivePipe.Writer;

#pragma warning disable CA1031 // 소켓 오류·중단·취소 — 어느 쪽이든 결론은 "수신 끝"이며 EOF 로 알린다.
        try
        {
            while (true)
            {
                Memory<byte> buffer = writer.GetMemory(MinReceiveBufferSize);
                ValueWebSocketReceiveResult received =
                    await _webSocket.ReceiveAsync(buffer, _closed.Token).ConfigureAwait(false);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    // 상대의 정상 종료. Close 응답은 종료 경로(CloseOutputAsync)가 맡는다.
                    break;
                }

                writer.Advance(received.Count);

                // 여기서 기다리는 것이 백프레셔다 — 소비자가 느리면 소켓을 더 읽지 않는다.
                FlushResult flushed = await writer.FlushAsync(_closed.Token).ConfigureAwait(false);
                if (flushed.IsCompleted || flushed.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 프로토콜 위반·소켓 절단·취소. "왜"는 소켓 상태가 알고, 여기서의 일은 EOF 전달이다.
        }
        finally
        {
            // ⚠ 예외가 아니라 정상 완료(EOF)로 알린다 — 같은 종료를 TCP 는 EOF 로, WS 는
            // 예외로 관측하게 만들면 전송 중립 검증(ADR-0004)이 깨진다.
            await writer.CompleteAsync().ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    /// <summary>송신 파이프 → 소켓. 읽기 배치 하나를 바이너리 메시지 하나로 보낸다.</summary>
    private async Task SendPumpAsync()
    {
        PipeReader reader = _sendPipe.Reader;

#pragma warning disable CA1031 // 소켓 오류·중단 — 송신 불가라는 결론만 남는다. 파이프 완료로 상류를 깨운다.
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(_closed.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (result.IsCanceled)
                {
                    break;
                }

                if (!buffer.IsEmpty)
                {
                    // 다중 세그먼트는 조각(endOfMessage=false)으로 잇고 마지막에 닫는다.
                    // 메시지 경계는 상대도 무시하므로(같은 어댑터) 배치 크기는 의미가 아니라
                    // 성능의 문제다.
                    ReadOnlySequence<byte> remaining = buffer;
                    while (!remaining.IsEmpty)
                    {
                        ReadOnlyMemory<byte> segment = remaining.First;
                        bool last = segment.Length == remaining.Length;
                        await _webSocket.SendAsync(
                            segment, WebSocketMessageType.Binary, endOfMessage: last, _closed.Token)
                            .ConfigureAwait(false);
                        remaining = remaining.Slice(segment.Length);
                    }
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    // 송신 끝 = 반닫힘. Close 프레임으로 알려야 상대의 수신 펌프가 EOF 를 본다.
                    using CancellationTokenSource closeLimit = new(_drainTimeout);
                    await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, null, closeLimit.Token).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 상대 절단·취소. 남은 송신 데이터는 보장 대상이 아니다(Abort 계약).
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    /// <remarks>
    /// 소켓을 즉시 중단하고 대기자를 깨운다. 파이프 완료는 펌프의 <c>finally</c> 와
    /// <see cref="DisposeAsync"/> 가 맡는다.
    /// </remarks>
    public void Abort(in ConnectionCloseInfo info)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) == 0)
        {
            CloseInfo = info;
        }

#pragma warning disable CA1031 // Abort 는 이미 닫힌 커넥션에 호출해도 예외를 던지지 않는다(계약).
        try
        {
            _receivePipe.Reader.CancelPendingRead();
            _sendPipe.Writer.CancelPendingFlush();

            // 소켓 중단이 펌프의 진행 중 소켓 호출을 깨운다.
            _webSocket.Abort();
        }
        catch (Exception)
        {
            // 이미 죽은 소켓. 닫힌 상태라는 목적은 달성됐다.
        }
#pragma warning restore CA1031

        SignalClosed();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 정상 종료다. 남은 송신 데이터를 <b>상한 시간 안에서</b> 내보낸 뒤 Close 프레임으로
    /// 반닫힘을 알린다. 상대는 <c>ReadResult.IsCompleted</c> 로 이것을 관측한다.
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

#pragma warning disable CA1031 // 종료 경로의 예외는 종료를 막을 이유가 없다.
        if (graceful)
        {
            try
            {
                // 드레인 상한 — 상대가 읽지 않으면 이 플러시가 영원히 끝나지 않는다(감사 H3 부류).
                using CancellationTokenSource drainLimit = new(_drainTimeout);
                await _sendPipe.Writer.FlushAsync(drainLimit.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 시간 초과·상대 닫힘 — 드레인을 포기하고 정리를 계속한다.
            }
        }

        try
        {
            // 송신 파이프 완료 → 송신 펌프가 Close 프레임을 보내고 끝난다.
            await _sendPipe.Writer.CompleteAsync().ConfigureAwait(false);
            await _receivePipe.Reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 이미 완료됐다.
        }

        try
        {
            // 펌프 종료 대기에는 상한이 있다 — Close 응답이 오지 않는 상대가 종료를
            // 볼모로 잡지 않게. 상한을 넘기면 소켓 중단이 펌프를 깨운다.
            await PumpsCompleted.WaitAsync(_drainTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _webSocket.Abort();
            try
            {
                await PumpsCompleted.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 중단된 펌프의 예외는 이미 각자 삼켰다.
            }
        }
#pragma warning restore CA1031

        _webSocket.Dispose();
        SignalClosed();
        _closed.Dispose();
    }

    /// <summary>취소 토큰을 발화시킨다.</summary>
    /// <remarks>취소 콜백의 예외가 종료 경로를 중단시키지 않게 여기서 막는다.</remarks>
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
