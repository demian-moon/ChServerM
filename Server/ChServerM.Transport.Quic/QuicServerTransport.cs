using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using ChServerM.Transports;

namespace ChServerM.Transport.Quic;

/// <summary>
/// QUIC 위에서 커넥션을 수용하는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0060 — <b>QUIC 양방향 스트림 하나 = 커넥션 하나</b>. HTTP 전송의
/// 다중화 이득(실측 5.9×)에 프로토콜 수준의 스트림 단위 HOL 격리·1-RTT 수립이 얹힌다.
/// <c>System.Net.Quic</c> 은 BCL 이라 이 어댑터는 서드파티 의존 0, Kestrel 참조도 없다.
/// </para>
/// <para>
/// <b>TLS 는 프로토콜 내장이다.</b> 서버 인증서가 조립의 필수 입력이고(기본값 없음 —
/// ADR-0051 결정 6의 규율), <c>ITransportSecurity</c> 를 이 전송에 조립하지 않는다
/// (이중 암호화 — QUIC 은 <c>ServerBuilder.UseTransportSecurity</c> 문서가 처음부터
/// 지목한 그 사례다).
/// </para>
/// <para>
/// <b>3단 종료의 대응.</b> <see cref="UnbindAsync"/> 는 리스너를 닫아 신규 <b>연결</b>을
/// 막고, 기존 연결의 신규 <b>스트림</b>은 즉시 중단으로 거부한다. 기존 스트림(커넥션)은
/// 계속 산다. <see cref="StopAsync"/> 가 드레인한다.
/// </para>
/// <para>
/// <b>⚠ 플랫폼 조건부다.</b> msquic 이 없는 환경(구형 OS, 일부 리눅스)에서는
/// <see cref="QuicListener.IsSupported"/> 가 거짓이고 <see cref="BindAsync"/> 가
/// <see cref="PlatformNotSupportedException"/> 으로 실패한다 — 조용히 폴백하지 않는다.
/// 전송 선택은 조립의 결정이지 런타임의 추측이 아니다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class QuicServerTransport : IServerTransport, ITransportBufferLimits
{
    private static readonly EventId ConnectionRejectedEvent = new(1004, "ConnectionRejected");
    private static readonly EventId HandlerFaultedEvent = new(1006, "ConnectionHandlerFaulted");

    /// <summary>드레인·상한 거부 시 상대에게 실리는 애플리케이션 오류 코드.</summary>
    private const long RejectedErrorCode = 0x0C;

    /// <summary>연결 종료에 실리는 애플리케이션 오류 코드.</summary>
    private const long CloseErrorCode = 0x0B;

    private readonly IPEndPoint _listenEndPoint;
    private readonly QuicTransportOptions _options;
    private readonly IServerLogger _logger;

    private readonly ConcurrentDictionary<ConnectionId, ActiveConnection> _connections = new();
    private readonly ConcurrentDictionary<long, QuicConnection> _quicConnections = new();
    private readonly CancellationTokenSource _stopping = new();

    private QuicListener? _listener;
    private Task? _acceptLoop;
    private IConnectionHandler? _handler;
    private IPEndPoint? _localEndPoint;
    private int _nextSlot;
    private long _nextQuicConnectionId;

    /// <summary>0 = 미바인드, 1 = 수용 중, 2 = 수용 중단(드레인 창).</summary>
    private int _state;

    /// <summary>수용 판정용 활성 커넥션(스트림) 수. 상한 검사는 이것으로만 한다(엄격 유계).</summary>
    private int _activeCount;

    /// <summary>수용 전송을 만든다.</summary>
    /// <param name="listenEndPoint">수용할 종단. 포트 0 이면 OS 가 배정한다.</param>
    /// <param name="options">전송 설정. 서버 인증서가 필수다.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때(인증서 누락 포함).</exception>
    public QuicServerTransport(
        IPEndPoint listenEndPoint,
        QuicTransportOptions options,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(listenEndPoint);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate(requireServerCertificate: true);

        _listenEndPoint = listenEndPoint;
        _options = options;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    /// <remarks>수신 파이프의 일시정지 임계값이 이 전송의 커넥션당 버퍼 한계다.</remarks>
    public long MaxBufferedBytesPerConnection => _options.PauseWriterThreshold;

    /// <inheritdoc />
    /// <remarks>바인드 전이나 수용 중단 후에는 <see langword="null"/>이다.</remarks>
    public EndPoint? LocalEndPoint => Volatile.Read(ref _state) == 1 ? _localEndPoint : null;

    /// <summary>현재 열려 있는 커넥션(활성 스트림) 수.</summary>
    public int ConnectionCount => _connections.Count;

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">이 환경에 QUIC 지원이 없을 때.</exception>
    public async ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();

        if (!QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "이 환경은 QUIC 을 지원하지 않는다(msquic/TLS 스택). 조용한 폴백은 없다 — 전송 선택은 조립의 결정이다.");
        }

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException($"{_listenEndPoint} 에 이미 바인드돼 있다.");
        }

        _handler = handler;

        SslApplicationProtocol alpn = new(_options.AlpnProtocol);

        try
        {
            _listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = _listenEndPoint,
                ApplicationProtocols = [alpn],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = QuicStreamConnection.AbortErrorCode,
                    DefaultCloseErrorCode = CloseErrorCode,
                    MaxInboundBidirectionalStreams = _options.MaxStreamsPerConnection,

                    // 단방향 스트림은 이 대응에 없다. 0 으로 닫는 것이 프로토콜 표면을 줄인다.
                    MaxInboundUnidirectionalStreams = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = [alpn],
                        ServerCertificate = _options.ServerCertificate,
                    },
                }),
            }, cancellationToken).ConfigureAwait(false);

            _localEndPoint = _listener.LocalEndPoint;
        }
        catch
        {
            // 실패한 바인드가 상태를 오염시키면 이 인스턴스는 영원히 좀비가 된다.
            Volatile.Write(ref _state, 0);
            _handler = null;
            throw;
        }

        // CA2016 억제 대신 명시적 None — 수락 루프의 수명은 바인드 토큰이 아니라
        // _stopping(전송 수명)이 다스린다.
        _acceptLoop = Task.Run(() => AcceptConnectionsAsync(_listener, handler), CancellationToken.None);
    }

    /// <summary>연결 수락 루프 — 연결마다 스트림 수락 루프를 띄운다.</summary>
    private async Task AcceptConnectionsAsync(QuicListener listener, IConnectionHandler handler)
    {
        while (!_stopping.IsCancellationRequested)
        {
            QuicConnection quicConnection;
            try
            {
                quicConnection = await listener.AcceptConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (QuicException)
            {
                // 리스너가 닫혔거나 수립 중 실패했다. 루프의 일은 계속 수락하는 것이다.
                if (Volatile.Read(ref _state) != 1)
                {
                    break;
                }

                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            long quicId = Interlocked.Increment(ref _nextQuicConnectionId);
            _quicConnections[quicId] = quicConnection;
            _ = Task.Run(() => AcceptStreamsAsync(quicId, quicConnection, handler));
        }
    }

    /// <summary>한 QUIC 연결의 스트림 수락 루프. 스트림 하나가 커넥션 하나다.</summary>
    private async Task AcceptStreamsAsync(long quicId, QuicConnection quicConnection, IConnectionHandler handler)
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                QuicStream stream;
                try
                {
                    stream = await quicConnection.AcceptInboundStreamAsync(_stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuicException)
                {
                    // 연결이 끝났다(정상 종료·절단). 이 연결의 수락 루프만 끝난다.
                    break;
                }

                // 드레인 창 — 신규 스트림은 즉시 중단으로 거부한다(로드밸런서 드레인 패턴).
                if (Volatile.Read(ref _state) != 1)
                {
                    RejectStream(stream, CloseReasonTags.Draining);
                    continue;
                }

                // 증가 후 검사-롤백 — 유계는 엄격해야 유계다(CLAUDE.md 9.6).
                if (Interlocked.Increment(ref _activeCount) > _options.MaxConnections)
                {
                    Interlocked.Decrement(ref _activeCount);
                    RejectStream(stream, CloseReasonTags.ConnectionLimit);
                    continue;
                }

                _ = Task.Run(() => RunHandlerAsync(handler, stream, quicConnection));
            }
        }
        finally
        {
            _quicConnections.TryRemove(quicId, out _);

#pragma warning disable CA1031 // 종료 경로다. 연결 정리 실패가 수락 루프의 종료를 막지 않는다.
            try
            {
                await quicConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 이미 닫힌 연결.
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>스트림 하나를 커넥션으로 수용해 핸들러 전 생애를 돌린다.</summary>
    /// <remarks>다른 전송의 수락 루프와 같은 골격 — 등록 → 핸들러 → <c>finally</c> 정리(9.2).</remarks>
    private async Task RunHandlerAsync(IConnectionHandler handler, QuicStream stream, QuicConnection quicConnection)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // CA2000 억제: finally 가 DisposeAsync 를 보장한다(수락 루프 골격).
#pragma warning disable CA2000
        QuicStreamConnection connection = new(
            NextConnectionId(), stream, quicConnection.LocalEndPoint, quicConnection.RemoteEndPoint, _options);
#pragma warning restore CA2000

        // 등록이 핸들러 기동보다 먼저다(인메모리·TCP·HTTP·WS 와 같은 결함 부류 방지).
        _connections[connection.Id] = new ActiveConnection(connection, completion.Task);

        try
        {
            await handler.RunAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료·스트림 리셋으로 인한 취소. 정상 경로다.
        }
#pragma warning disable CA1031 // 핸들러는 애플리케이션 코드다. 무엇을 던지든 프로세스를 죽이지 않는다.
        catch (Exception exception)
        {
            LogHandlerFaulted(connection.Id, exception);
            connection.Abort(new ConnectionCloseInfo(
                CloseReason.ApplicationError, ErrorCode.HandlerFaulted, exception.Message));
        }
#pragma warning restore CA1031
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            Interlocked.Decrement(ref _activeCount);

#pragma warning disable CA1031 // 정리 실패가 드레인 신호를 막으면 안 된다.
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 이미 리셋된 스트림.
            }
            finally
            {
                completion.SetResult();
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>스트림을 즉시 중단으로 거부하고 기록한다.</summary>
    private void RejectStream(QuicStream stream, string reason)
    {
#pragma warning disable CA1031 // 거부 대상 스트림의 정리 실패는 거부라는 목적에 영향이 없다.
        try
        {
            stream.Abort(QuicAbortDirection.Both, RejectedErrorCode);
            stream.Dispose();
        }
        catch (Exception)
        {
            // 이미 닫힌 스트림.
        }
#pragma warning restore CA1031

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                ConnectionRejectedEvent,
                (Limit: _options.MaxConnections, Reason: reason),
                null,
                static (state, _) => $"스트림을 거부했다(사유: {state.Reason}, 동시 접속 상한 {state.Limit}).");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 리스너를 닫아 신규 연결을 막고, 기존 연결의 신규 스트림은 즉시 중단으로 거부한다.
    /// 기존 스트림(커넥션)은 계속 산다.
    /// </remarks>
    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
        {
            return;
        }

        QuicListener? listener = Interlocked.Exchange(ref _listener, null);
        if (listener is not null)
        {
#pragma warning disable CA1031 // 리스너 정리 실패가 언바인드를 막지 않는다.
            try
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 이미 닫힌 리스너.
            }
#pragma warning restore CA1031
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // 신규 수용부터 막는다. 반대면 드레인 중에 새 스트림이 들어와 끝나지 않는다.
        await UnbindAsync(CancellationToken.None).ConfigureAwait(false);

        List<Task> pending = [];
        foreach (ActiveConnection active in _connections.Values)
        {
            pending.Add(active.Completion);
        }

        if (pending.Count > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                foreach (ActiveConnection active in _connections.Values)
                {
                    active.Connection.Abort(ConnectionCloseInfo.ShuttingDown);
                }

#pragma warning disable CA1031 // 종료 경로다. 개별 커넥션의 예외로 전체 정리를 멈추지 않는다.
                try
                {
                    await Task.WhenAll(pending)
                        .WaitAsync(_options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 강제 종료된 커넥션의 예외(이미 기록됨)거나 상한 초과다.
                }
#pragma warning restore CA1031
            }
        }

        // 수락 루프·연결 정리.
        await _stopping.CancelAsync().ConfigureAwait(false);

        foreach (QuicConnection quicConnection in _quicConnections.Values)
        {
#pragma warning disable CA1031 // 종료 경로다.
            try
            {
                await quicConnection.CloseAsync(CloseErrorCode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 이미 닫힌 연결이거나 취소됐다 — Dispose 는 스트림 수락 루프의 finally 가 맡는다.
            }
#pragma warning restore CA1031
        }

        if (_acceptLoop is { } acceptLoop)
        {
#pragma warning disable CA1031 // 수락 루프의 마지막 예외가 종료를 막지 않는다.
            try
            {
                // 시간 상한으로만 통제한다 — 호출자 토큰이 이미 취소돼 있어도 정리는 끝까지 간다.
                await acceptLoop.WaitAsync(_options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 상한 초과 — 루프는 _stopping 취소로 곧 끝난다.
            }
#pragma warning restore CA1031
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource immediate = new();
        await immediate.CancelAsync().ConfigureAwait(false);
        await StopAsync(immediate.Token).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    private void LogHandlerFaulted(ConnectionId id, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(
                LogLevel.Error,
                HandlerFaultedEvent,
                id,
                exception,
                static (connectionId, ex) => $"{connectionId} 핸들러가 예외로 끝났다: {ex?.Message}");
        }
    }

    /// <summary>커넥션 거부 로그의 저카디널리티 사유 태그 값.</summary>
    private static class CloseReasonTags
    {
        public const string ConnectionLimit = "connection_limit";
        public const string Draining = "draining";
    }

    private readonly record struct ActiveConnection(QuicStreamConnection Connection, Task Completion);
}
