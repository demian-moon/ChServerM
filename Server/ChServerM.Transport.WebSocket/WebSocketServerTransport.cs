using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using ChServerM.Transports;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WS = System.Net.WebSockets.WebSocket;

namespace ChServerM.Transport.WebSocket;

/// <summary>
/// Kestrel 위에서 WebSocket 커넥션을 수용하는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 브라우저·프록시 통과가 필요한 배포에서 상시 연결을 나르는 전송 축이다
/// (ADR-0059). 업그레이드가 끝난 뒤에는 <see cref="WebSocketDuplexConnection"/> 이 바이트
/// 스트림을 만들므로, 프레이밍·디스패치·핸들러가 <b>TCP·HTTP 와 동일한 코드로</b> 돈다.
/// </para>
/// <para>
/// <b>호스팅은 HTTP 전송과 같은 결정이다(ADR-0057)</b> — <c>WebApplication</c> 없이
/// <see cref="KestrelServer"/> + <see cref="IHttpApplication{TContext}"/>. 업그레이드 핸드셰이크
/// (RFC 6455)는 <see cref="IHttpUpgradeFeature"/> 로 직접 수행한다 — ASP.NET 의
/// <c>WebSocketMiddleware</c> 는 미들웨어 파이프라인(호스팅 스택)을 요구하므로 쓰지 않는다.
/// 핸드셰이크는 헤더 검증 + 고정 GUID 해시 한 줄이라 직접 하는 비용이 미미하다.
/// </para>
/// <para>
/// <b>3단 종료의 대응.</b> HTTP 전송과 같다 — <see cref="UnbindAsync"/> 는 신규 업그레이드를
/// <c>503</c> 으로 거부하고(로드밸런서 드레인), 기존 커넥션은 계속 산다.
/// <see cref="StopAsync"/> 가 드레인한다.
/// </para>
/// <para>
/// <b>평문 HTTP/1.1 업그레이드 전용이다.</b> WebSocket over HTTP/2(RFC 8441)는 배포 이득이
/// 확인되면 별도 결정으로 더한다. TLS(wss)는 Kestrel 소유의 후속 옵션이다 —
/// <c>ITransportSecurity</c> 를 이 전송에 조립하지 않는다(이중 암호화).
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// </remarks>
public sealed class WebSocketServerTransport : IServerTransport, ITransportBufferLimits
{
    private static readonly EventId ConnectionRejectedEvent = new(1004, "ConnectionRejected");
    private static readonly EventId HandlerFaultedEvent = new(1006, "ConnectionHandlerFaulted");

    /// <summary>RFC 6455 가 고정한 accept 키 GUID. 프로토콜 상수이지 비밀이 아니다.</summary>
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly IPEndPoint _listenEndPoint;
    private readonly WebSocketTransportOptions _options;
    private readonly IServerLogger _logger;

    private readonly ConcurrentDictionary<ConnectionId, ActiveConnection> _connections = new();

    // CA2213 억제 근거: 동기 Dispose 가 없다. 정리는 StopAsync(비동기 종료 경로)가 수행한다.
#pragma warning disable CA2213
    private KestrelServer? _server;
#pragma warning restore CA2213
    private IConnectionHandler? _handler;
    private IPEndPoint? _localEndPoint;
    private int _nextSlot;

    /// <summary>0 = 미바인드, 1 = 수용 중, 2 = 수용 중단(드레인 창).</summary>
    private int _state;

    /// <summary>수용 판정용 활성 커넥션 수. 상한 검사는 이것으로만 한다(엄격 유계).</summary>
    private int _activeCount;

    /// <summary>수용 전송을 만든다.</summary>
    /// <param name="listenEndPoint">수용할 종단. 포트 0 이면 OS 가 배정한다.</param>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="ArgumentNullException"><paramref name="listenEndPoint"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public WebSocketServerTransport(
        IPEndPoint listenEndPoint,
        WebSocketTransportOptions? options = null,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(listenEndPoint);

        options ??= new WebSocketTransportOptions();
        options.Validate();

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

    /// <summary>현재 열려 있는 커넥션 수.</summary>
    public int ConnectionCount => _connections.Count;

    /// <inheritdoc />
    public async ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                $"{_listenEndPoint} 에 이미 바인드돼 있다. 전송 인스턴스는 1회용이다 — "
                + "다시 바인드하려면 StopAsync 후 새 인스턴스를 만든다. 재호출이 의도가 아니라면 BindAsync 를 두 곳에서 부르는 조립을 의심한다.");
        }

        _handler = handler;

        KestrelServerOptions kestrelOptions = new();

        // 업그레이드 후에는 요청·응답 개념이 없다. 유휴 커넥션이 정상 상태이므로
        // 최소 전송 속도 감시를 끈다(HTTP 전송과 같은 판단).
        kestrelOptions.Limits.MinRequestBodyDataRate = null;
        kestrelOptions.Limits.MinResponseDataRate = null;

        // 업그레이드는 HTTP/1.1 의 기제다. HTTP/2 WebSocket(RFC 8441)은 별도 결정.
        kestrelOptions.Listen(_listenEndPoint, listen => listen.Protocols = HttpProtocols.Http1);

        SocketTransportFactory socketTransport = new(
            Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance);

        KestrelServer server = new(Options.Create(kestrelOptions), socketTransport, NullLoggerFactory.Instance);

        try
        {
            await server.StartAsync(new Application(this), cancellationToken).ConfigureAwait(false);

            string? address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            int port = address is not null ? new Uri(address).Port : _listenEndPoint.Port;
            _localEndPoint = new IPEndPoint(_listenEndPoint.Address, port);
        }
        catch
        {
            // 실패한 바인드가 상태를 오염시키면 이 인스턴스는 영원히 좀비가 된다.
            Volatile.Write(ref _state, 0);
            _handler = null;
            server.Dispose();
            throw;
        }

        _server = server;
    }

    /// <inheritdoc />
    /// <remarks>신규 업그레이드를 <c>503</c> 으로 거부한다. 기존 커넥션은 계속 산다.</remarks>
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.CompareExchange(ref _state, 2, 1);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
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

        KestrelServer? server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
#pragma warning disable CA1031 // 종료 경로다. 리스너 정리 실패가 호출자의 종료를 막지 않는다.
            try
            {
                using CancellationTokenSource stopLimit = new(_options.ShutdownTimeout);
                await server.StopAsync(stopLimit.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 종료 상한 초과 — Dispose 가 남은 자원을 회수한다.
            }
#pragma warning restore CA1031
            server.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource immediate = new();
        await immediate.CancelAsync().ConfigureAwait(false);
        await StopAsync(immediate.Token).ConfigureAwait(false);
    }

    /// <summary>업그레이드 요청 하나를 커넥션으로 수용해 핸들러 전 생애를 돌린다.</summary>
    /// <remarks>
    /// 인메모리·TCP·HTTP 전송의 수락 루프와 같은 골격이다(Template Method) —
    /// 등록 → 핸들러 → <c>finally</c> 에서 제거·정리(CLAUDE.md 9.2).
    /// </remarks>
    private async Task ProcessRequestAsync(IFeatureCollection features)
    {
        IHttpRequestFeature request = features.GetRequiredFeature<IHttpRequestFeature>();
        IHttpResponseFeature response = features.GetRequiredFeature<IHttpResponseFeature>();

        if (!string.Equals(request.Path, _options.Path, StringComparison.Ordinal))
        {
            response.StatusCode = 404;
            return;
        }

        IHttpUpgradeFeature? upgrade = features.Get<IHttpUpgradeFeature>();
        string? key = request.Headers.SecWebSocketKey;

        if (upgrade is not { IsUpgradableRequest: true }
            || !string.Equals(request.Method, "GET", StringComparison.Ordinal)
            || string.IsNullOrEmpty(key)
            || !request.Headers.SecWebSocketVersion.ToString().Contains("13", StringComparison.Ordinal))
        {
            // WebSocket 업그레이드가 아니다. 426 이 "이 경로는 업그레이드 전용"을 정확히 말한다.
            response.StatusCode = 426;
            response.Headers.Upgrade = "websocket";
            return;
        }

        IConnectionHandler? handler = _handler;
        if (handler is null || Volatile.Read(ref _state) != 1)
        {
            EmitRejected(response, CloseReasonTags.Draining);
            return;
        }

        // 증가 후 검사-롤백 — 유계는 엄격해야 유계다(CLAUDE.md 9.6).
        if (Interlocked.Increment(ref _activeCount) > _options.MaxConnections)
        {
            Interlocked.Decrement(ref _activeCount);
            EmitRejected(response, CloseReasonTags.ConnectionLimit);
            return;
        }

        WebSocketDuplexConnection? connection = null;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // RFC 6455 핸드셰이크 — accept 키는 프로토콜이 고정한 GUID 의 SHA-1 이다.
            // CA5350 억제: 보안 용도가 아니라 프로토콜 상수 계산이다. 규격이 SHA-1 을 지정한다.
#pragma warning disable CA5350
            string accept = Convert.ToBase64String(
                System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.ASCII.GetBytes(key + WebSocketAcceptGuid)));
#pragma warning restore CA5350

            response.StatusCode = 101;
            response.Headers.Connection = "Upgrade";
            response.Headers.Upgrade = "websocket";
            response.Headers.SecWebSocketAccept = accept;

            // 101 과 함께 헤더가 나가고, 이후 이 스트림이 곧 소켓이다.
            Stream stream = await upgrade.UpgradeAsync().ConfigureAwait(false);

            // keep-alive ping 은 켜지 않는다 — 하트비트는 애플리케이션 레벨의 몫이다
            // (TCP 전송의 keep-alive 판단과 같은 자리).
            WS webSocket = WS.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.Zero,
            });

            IHttpConnectionFeature? httpConnection = features.Get<IHttpConnectionFeature>();
            IPEndPoint? remote = httpConnection?.RemoteIpAddress is { } remoteIp
                ? new IPEndPoint(remoteIp, httpConnection.RemotePort)
                : null;
            IPEndPoint? local = httpConnection?.LocalIpAddress is { } localIp
                ? new IPEndPoint(localIp, httpConnection.LocalPort)
                : _localEndPoint;

#pragma warning disable CA2000 // 이 메서드의 finally 가 DisposeAsync 를 보장한다(수락 루프 골격).
            connection = new WebSocketDuplexConnection(
                NextConnectionId(), webSocket, local, remote, _options);
#pragma warning restore CA2000

            // 등록이 핸들러 기동보다 먼저다(인메모리·TCP·HTTP 와 같은 결함 부류 방지).
            _connections[connection.Id] = new ActiveConnection(connection, completion.Task);

            await handler.RunAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료·소켓 절단으로 인한 취소. 정상 경로다.
        }
#pragma warning disable CA1031 // 핸들러는 애플리케이션 코드다. 무엇을 던지든 프로세스를 죽이지 않는다.
        catch (Exception exception)
        {
            LogHandlerFaulted(connection?.Id ?? default, exception);
            connection?.Abort(new ConnectionCloseInfo(
                CloseReason.ApplicationError, ErrorCode.HandlerFaulted, exception.Message));
        }
#pragma warning restore CA1031
        finally
        {
            if (connection is not null)
            {
                _connections.TryRemove(connection.Id, out _);
            }

            Interlocked.Decrement(ref _activeCount);

#pragma warning disable CA1031 // 여기서 새어 나간 예외는 Kestrel 요청 루프 밖으로 빠져 프로세스를 죽인다(ADR-0057 실측).
            try
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 정리 실패가 드레인 신호를 막으면 안 된다.
            }
            finally
            {
                completion.SetResult();
            }
#pragma warning restore CA1031
        }
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    /// <summary>거부 응답(503)을 만들고 기록한다.</summary>
    private void EmitRejected(IHttpResponseFeature response, string reason)
    {
        response.StatusCode = 503;

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                ConnectionRejectedEvent,
                (Limit: _options.MaxConnections, Reason: reason),
                null,
                static (state, _) => $"업그레이드를 거부했다(사유: {state.Reason}, 동시 접속 상한 {state.Limit}).");
        }
    }

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

    private readonly record struct ActiveConnection(WebSocketDuplexConnection Connection, Task Completion);

    /// <summary>Kestrel 에 넘기는 요청 처리기 — 업그레이드 요청 = 커넥션이라는 대응의 진입점.</summary>
    private sealed class Application(WebSocketServerTransport transport) : IHttpApplication<RequestContext>
    {
        public RequestContext CreateContext(IFeatureCollection contextFeatures) => new(contextFeatures);

        public Task ProcessRequestAsync(RequestContext context) =>
            transport.ProcessRequestAsync(context.Features);

        public void DisposeContext(RequestContext context, Exception? exception)
        {
            // 커넥션 정리는 ProcessRequestAsync 의 finally 가 이미 끝냈다.
        }
    }

    /// <summary>요청 하나의 기능 컬렉션을 나르는 문맥. 할당을 피하려고 구조체다.</summary>
    private readonly record struct RequestContext(IFeatureCollection Features);
}
