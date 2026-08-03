using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Transport.Tcp;

/// <summary>
/// 하나의 TCP 소켓을 <see cref="IConnection"/>으로 감싼다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 소켓은 "읽어라/써라"만 아는 반면 상위 계층은 백프레셔·부분 읽기·
/// 버퍼 재사용이 이미 해결된 바이트 경로를 원한다. 그 간극을 <see cref="Pipe"/> 두 개와
/// 펌프 두 개로 메운다.
/// </para>
/// <code>
///   소켓 ──▶ [수신 펌프] ──▶ receivePipe ──▶ Input  (애플리케이션이 읽는다)
///   소켓 ◀── [송신 펌프] ◀── sendPipe    ◀── Output (애플리케이션이 쓴다)
/// </code>
/// <para>
/// <b><c>NetworkStream</c> 을 쓰지 않는다.</b> 레거시는 <c>TcpClient.GetStream()</c> 노선이라
/// 소켓 위에 계층이 하나 더 있었다(ADR-0001). 여기서는 <see cref="Socket"/> 의
/// <see cref="Memory{T}"/> 오버로드를 직접 쓴다.
/// </para>
/// <para>
/// <b>소켓 작업에 <see cref="CancellationToken"/>을 넘기지 않는다.</b> 플랫폼마다 실제
/// 취소 가능 여부가 다르고, 취소 등록 자체가 작업당 비용이다. 대신 중단은
/// <see cref="Socket.Dispose()"/> 로 한다 — 대기 중인 작업이 즉시 예외로 깨어난다.
/// Kestrel 도 같은 방식이다.
/// </para>
/// <para>
/// <b>2단 종료.</b> 정상 종료는 (1) 송신 파이프를 완료해 남은 데이터를 다 보내고
/// FIN 을 보낸 뒤, (2) 상대의 FIN 을 <see cref="TcpTransportOptions.ShutdownTimeout"/>
/// 동안 기다린다. 시간이 지나면 강제로 끊는다. 상한 없는 대기는 종료를 영원히 막는다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Input"/>은 읽기 루프 하나가, <see cref="Output"/>은
/// 쓰기 경로 하나가 소유한다. <see cref="Abort"/>와 <see cref="DisposeAsync"/>는
/// 어느 스레드에서 몇 번을 불러도 안전하다.
/// </para>
/// </remarks>
public sealed class SocketConnection : IConnection, IConnectionEndPointFeature
{
    private static readonly EventId ReceiveFaultedEvent = new(1010, "ReceiveFaulted");
    private static readonly EventId SendFaultedEvent = new(1011, "SendFaulted");

    private readonly Socket _socket;
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _closed = new();
    private readonly int _minReceiveBufferSize;
    private readonly bool _waitForData;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IServerLogger _logger;

    private Task _receivePump = Task.CompletedTask;
    private Task _sendPump = Task.CompletedTask;

    /// <summary>0 = 열림, 1 = 닫힘. <see cref="Interlocked"/>로만 바꾼다.</summary>
    private int _closedFlag;

    internal SocketConnection(
        ConnectionId id,
        Socket socket,
        TcpTransportOptions options,
        IServerLogger logger)
    {
        _socket = socket;
        _minReceiveBufferSize = options.MinReceiveBufferSize;
        _waitForData = options.WaitForDataBeforeAllocating;
        _shutdownTimeout = options.ShutdownTimeout;
        _logger = logger;

        PipeOptions pipeOptions = options.CreatePipeOptions();
        _receivePipe = new Pipe(pipeOptions);
        _sendPipe = new Pipe(pipeOptions);

        Id = id;

        // 소켓이 이미 끊긴 뒤라면 EndPoint 접근이 던진다. 진단 정보 때문에
        // 커넥션 생성을 실패시킬 이유는 없다.
        LocalEndPoint = TryGetEndPoint(static s => s.LocalEndPoint, socket);
        RemoteEndPoint = TryGetEndPoint(static s => s.RemoteEndPoint, socket);

        FeatureCollection features = new(capacity: 1);
        features.Set<IConnectionEndPointFeature>(this);
        Features = features;
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

    /// <summary>마지막으로 기록된 종료 사유. 진단용이다.</summary>
    public ConnectionCloseInfo CloseInfo { get; private set; }

    /// <summary>두 펌프를 시작한다.</summary>
    /// <remarks>
    /// 생성자에서 시작하지 않는다. 생성자가 <c>this</c> 를 캡처한 태스크를 띄우면
    /// 객체가 완전히 초기화되기 전에 다른 스레드가 그것을 보게 된다.
    /// </remarks>
    internal void Start()
    {
        _receivePump = ReceivePumpAsync();
        _sendPump = SendPumpAsync();
    }

    /// <inheritdoc />
    public void Abort(in ConnectionCloseInfo info)
    {
        // 여러 번 불러도 안전해야 한다(IConnection 계약).
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
        {
            return;
        }

        CloseInfo = info;

        // 소켓을 버리면 대기 중인 수신·송신이 즉시 예외로 깨어난다.
        // 대기 중인 송신 데이터는 보장하지 않는다 — 그것이 Abort 와 DisposeAsync 의 차이다.
        CloseSocket();
        SignalClosed();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 정상 종료다. 남은 송신 데이터를 내보내고 FIN 을 보낸 뒤,
    /// 상대의 응답을 <see cref="TcpTransportOptions.ShutdownTimeout"/> 동안만 기다린다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
        {
            // 이미 Abort 되었거나 해제 중이다. 펌프가 끝나기만 기다린다.
            await WaitForPumpsAsync().ConfigureAwait(false);
            _closed.Dispose();
            return;
        }

        if (CloseInfo.Reason == CloseReason.None)
        {
            CloseInfo = new ConnectionCloseInfo(CloseReason.ServerClosed);
        }

        // 1단계 — 송신 파이프를 완료한다. 송신 펌프가 남은 데이터를 다 보내고 끝난다.
        await CompleteQuietlyAsync(_sendPipe.Writer).ConfigureAwait(false);

        try
        {
            await _sendPump.WaitAsync(_shutdownTimeout).ConfigureAwait(false);

            // 2단계 — FIN 을 보낸다. 상대는 이제 스트림 끝을 관측한다.
            _socket.Shutdown(SocketShutdown.Send);

            // 상대의 FIN 을 기다린다. 오지 않으면 아래 타임아웃이 걷어낸다.
            await _receivePump.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 종료 경로다. 어떤 실패든 나머지 정리를 막으면 안 된다.
        catch (Exception)
        {
            // 타임아웃이거나 상대가 이미 끊었다. 어느 쪽이든 아래에서 강제로 정리한다.
        }
#pragma warning restore CA1031

        CloseSocket();
        SignalClosed();

        await WaitForPumpsAsync().ConfigureAwait(false);
        _closed.Dispose();
    }

    /// <summary>소켓에서 읽어 수신 파이프에 넣는다.</summary>
    private async Task ReceivePumpAsync()
    {
        Exception? failure = null;

        try
        {
            while (true)
            {
                if (_waitForData)
                {
                    // 0바이트 수신 — 읽을 것이 생길 때까지 버퍼를 잡지 않고 대기한다.
                    // 유휴 커넥션이 버퍼를 붙들지 않게 하는 것이 목적이다.
                    // 1만 유휴 접속 × 4KB = 40MB 를 아끼는 최적화이며,
                    // 레거시는 커넥션당 64KB 를 상수로 붙들고 있었다(= 640MB).
                    await _socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None).ConfigureAwait(false);
                }

                Memory<byte> buffer = _receivePipe.Writer.GetMemory(_minReceiveBufferSize);
                int received = await _socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);

                if (received == 0)
                {
                    // 상대가 FIN 을 보냈다. 정상 종료다.
                    break;
                }

                _receivePipe.Writer.Advance(received);

                FlushResult flush = await _receivePipe.Writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    // 애플리케이션이 읽기를 끝냈다. 더 받을 이유가 없다.
                    break;
                }
            }
        }
        catch (Exception exception) when (IsExpectedDisconnect(exception))
        {
            // 소켓이 버려졌거나 상대가 비정상 종료했다. 정상적인 종료 경로다.
        }
#pragma warning disable CA1031 // 펌프에서 예외가 새면 그 커넥션이 응답 없이 매달린다.
        catch (Exception exception)
        {
            failure = exception;
            Log(ReceiveFaultedEvent, exception);
        }
#pragma warning restore CA1031
        finally
        {
            // 반드시 완료한다. 빠뜨리면 애플리케이션의 ReadAsync 가 영원히 대기한다.
            await CompleteQuietlyAsync(_receivePipe.Writer, failure).ConfigureAwait(false);
            SignalClosed();
        }
    }

    /// <summary>송신 파이프에서 읽어 소켓에 쓴다.</summary>
    private async Task SendPumpAsync()
    {
        Exception? failure = null;

        try
        {
            while (true)
            {
                ReadResult read = await _sendPipe.Reader.ReadAsync().ConfigureAwait(false);

                if (read.IsCanceled)
                {
                    break;
                }

                ReadOnlySequence<byte> buffer = read.Buffer;

                try
                {
                    if (buffer.IsSingleSegment)
                    {
                        await SendAllAsync(buffer.First).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (ReadOnlyMemory<byte> segment in buffer)
                        {
                            await SendAllAsync(segment).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    // 보냈든 실패했든 소비 위치는 진행시킨다. 빠뜨리면 같은 데이터를
                    // 무한히 다시 읽는다.
                    _sendPipe.Reader.AdvanceTo(buffer.End);
                }

                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (IsExpectedDisconnect(exception))
        {
            // 상대가 먼저 끊었다. 흔한 일이다.
        }
#pragma warning disable CA1031 // 위와 같은 이유.
        catch (Exception exception)
        {
            failure = exception;
            Log(SendFaultedEvent, exception);
        }
#pragma warning restore CA1031
        finally
        {
            await CompleteQuietlyAsync(_sendPipe.Reader, failure).ConfigureAwait(false);
            SignalClosed();
        }
    }

    /// <summary>세그먼트 하나를 끝까지 보낸다.</summary>
    /// <remarks>
    /// <see cref="Socket.SendAsync(ReadOnlyMemory{byte}, SocketFlags, CancellationToken)"/>가
    /// 요청한 전부를 보낸다는 보장은 없다. 부분 전송을 처리하지 않으면
    /// <b>조용히 잘린 프레임</b>이 나가고, 수신 측 프레임 경계가 통째로 밀린다.
    /// </remarks>
    private async ValueTask SendAllAsync(ReadOnlyMemory<byte> segment)
    {
        while (!segment.IsEmpty)
        {
            int sent = await _socket.SendAsync(segment, SocketFlags.None).ConfigureAwait(false);

            if (sent <= 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            segment = segment[sent..];
        }
    }

    private async Task WaitForPumpsAsync()
    {
#pragma warning disable CA1031 // 정리 경로. 펌프의 예외는 이미 기록됐다.
        try
        {
            await Task.WhenAll(_receivePump, _sendPump).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 흡수한다.
        }
#pragma warning restore CA1031
    }

    private void CloseSocket()
    {
        try
        {
            _socket.Dispose();
        }
        catch (SocketException)
        {
            // 이미 끊긴 소켓이다.
        }
        catch (ObjectDisposedException)
        {
            // 이미 버려졌다.
        }
    }

    private void SignalClosed()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 해제됐다. 닫혔다는 목적은 달성됐다.
        }
        catch (AggregateException)
        {
            // 취소 콜백이 던진 예외로 종료를 막지 않는다.
        }
    }

    private void Log(EventId eventId, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(
                LogLevel.Error,
                eventId,
                Id,
                exception,
                static (id, ex) => $"{id} 소켓 펌프가 실패했다: {ex?.Message}");
        }
    }

    /// <summary>끊긴 소켓에서 나오는 정상적인 예외인지 판별한다.</summary>
    /// <remarks>
    /// 이것들을 오류로 기록하면 로그가 소음으로 가득 차 <b>진짜 오류가 묻힌다.</b>
    /// 상대가 먼저 끊는 것은 사고가 아니라 일상이다.
    /// </remarks>
    private static bool IsExpectedDisconnect(Exception exception) => exception switch
    {
        ObjectDisposedException => true,
        OperationCanceledException => true,
        SocketException socketException => socketException.SocketErrorCode is
            SocketError.ConnectionReset or
            SocketError.ConnectionAborted or
            SocketError.OperationAborted or
            SocketError.Interrupted or
            SocketError.Shutdown,
        _ => false,
    };

    private static async ValueTask CompleteQuietlyAsync(PipeWriter writer, Exception? failure = null)
    {
#pragma warning disable CA1031 // 정리 경로.
        try
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 이미 완료됐다.
        }
#pragma warning restore CA1031
    }

    private static async ValueTask CompleteQuietlyAsync(PipeReader reader, Exception? failure = null)
    {
#pragma warning disable CA1031 // 정리 경로.
        try
        {
            await reader.CompleteAsync(failure).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 이미 완료됐다.
        }
#pragma warning restore CA1031
    }

    private static EndPoint? TryGetEndPoint(Func<Socket, EndPoint?> accessor, Socket socket)
    {
        try
        {
            return accessor(socket);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
