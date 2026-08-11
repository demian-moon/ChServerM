using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
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

namespace ChServerM.Transport.Http;

/// <summary>
/// Kestrel 위에서 커넥션을 수용하는 HTTP 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <c>stateless-web</c> 참조 프로필(ADR-0004)의 전송 축이다.
/// 핵심 결정은 <b>HTTP/2 스트림 하나 = 커넥션 하나</b>(ADR-0057) — 요청 본문이
/// <see cref="IConnection.Input"/>, 응답 본문이 <see cref="IConnection.Output"/> 이 된다.
/// 그래서 프레이밍·디스패치·핸들러가 <b>TCP 와 동일한 코드로</b> HTTP 위에서 돌고,
/// "두 프로필이 같은 핸들러로 동작한다"는 합격 기준이 이 어셈블리 추가만으로 성립한다.
/// </para>
/// <para>
/// <b>Kestrel 을 직접 세운다.</b> <c>WebApplication</c>/제네릭 호스트/DI 컨테이너를 쓰지
/// 않고 <see cref="KestrelServer"/> + <see cref="IHttpApplication{TContext}"/> 로 요청을
/// 받는다(ADR-0057). 호스팅 스택 없이 검증된 HTTP 엔진만 가져오는 최소 표면이며,
/// 리플렉션 기반 DI 가 없어 AOT 하드 룰과도 충돌하지 않는다.
/// </para>
/// <para>
/// <b>3단 종료의 대응.</b> Kestrel 은 리스너만 닫는 단계를 노출하지 않으므로,
/// <see cref="UnbindAsync"/> 는 <b>신규 스트림 거부(503)</b> 로 구현된다 — 로드밸런서
/// 드레인과 같은 패턴이고, "신규 수용만 중단하고 기존 커넥션은 유지"라는 계약의 의미를
/// 스트림 수준에서 지킨다. <see cref="StopAsync"/> 가 기존 스트림을 드레인한다.
/// </para>
/// <para>
/// <b>평문 HTTP/2(h2c) 전용이다.</b> 양방향 스트리밍은 HTTP/2 가 필요하고, 평문 포트에서
/// HTTP/1.1 과의 프로토콜 협상은 불가능하다(협상은 TLS ALPN 의 몫). TLS 는 Kestrel 이
/// 소유하는 후속 옵션이다 — <c>ITransportSecurity</c> 를 이 전송에 조립하지 않는다
/// (이중 암호화가 된다, <c>ServerBuilder.UseTransportSecurity</c> 문서 참조).
/// </para>
/// <para>
/// <b>백프레셔.</b> HTTP/2 흐름 제어 윈도(<see cref="HttpTransportOptions.StreamReceiveWindowSize"/>)가
/// TCP 의 수신 버퍼 임계값에 대응하며, <see cref="ITransportBufferLimits"/> 로 노출되어
/// "최대 프레임 &gt; 버퍼" 교착 조합을 조립 시점에 잡는다(ADR-0007).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다. 요청 처리는 Kestrel 의 IO 스레드에서 시작해
/// 스레드풀로 이어진다.
/// </para>
/// </remarks>
public sealed class HttpServerTransport : IServerTransport, ITransportBufferLimits
{
    private static readonly EventId ConnectionRejectedEvent = new(1004, "ConnectionRejected");
    private static readonly EventId HandlerFaultedEvent = new(1006, "ConnectionHandlerFaulted");

    private readonly IPEndPoint _listenEndPoint;
    private readonly string _path;
    private readonly int _maxConnections;
    private readonly int _streamReceiveWindowSize;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IServerLogger _logger;

    private readonly ConcurrentDictionary<ConnectionId, ActiveConnection> _connections = new();

    // CA2213 억제 근거: 동기 Dispose 가 없다. 정리는 StopAsync(비동기 종료 경로)가 수행하며
    // DisposeAsync 가 그 경로를 즉시 취소 토큰으로 재사용한다.
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
    public HttpServerTransport(
        IPEndPoint listenEndPoint,
        HttpTransportOptions? options = null,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(listenEndPoint);

        options ??= new HttpTransportOptions();
        options.Validate();

        _listenEndPoint = listenEndPoint;
        _path = options.Path;
        _maxConnections = options.MaxConnections;
        _streamReceiveWindowSize = options.StreamReceiveWindowSize;
        _shutdownTimeout = options.ShutdownTimeout;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    /// <remarks>스트림 수신 윈도가 이 전송의 커넥션당 버퍼 한계다.</remarks>
    public long MaxBufferedBytesPerConnection => _streamReceiveWindowSize;

    /// <inheritdoc />
    /// <remarks>바인드 전이나 수용 중단 후에는 <see langword="null"/>이다.</remarks>
    public EndPoint? LocalEndPoint => Volatile.Read(ref _state) == 1 ? _localEndPoint : null;

    /// <summary>현재 열려 있는 커넥션(활성 스트림) 수.</summary>
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

        // 프레임 스트림은 길이가 없다 — 커넥션이 사는 동안 계속 흐른다.
        kestrelOptions.Limits.MaxRequestBodySize = null;

        // 최소 전송 속도 감시를 끈다. 유휴 커넥션(오래 조용한 스트림)은 이 워크로드의
        // 정상 상태인데, 기본 감시(240B/5s)는 그것을 지연 공격으로 판정해 끊는다.
        kestrelOptions.Limits.MinRequestBodyDataRate = null;
        kestrelOptions.Limits.MinResponseDataRate = null;

        // 흐름 제어 윈도 = 이 전송의 백프레셔 임계값(ADR-0007 조합 검사의 입력).
        kestrelOptions.Limits.Http2.InitialStreamWindowSize = _streamReceiveWindowSize;

        // 연결 윈도는 스트림 여러 개가 나눠 쓴다. 스트림 윈도보다 작으면 스트림 하나가
        // 소진되기도 전에 연결 전체가 멈추므로, 최소한 스트림 윈도의 2배로 벌린다.
        kestrelOptions.Limits.Http2.InitialConnectionWindowSize =
            (int)Math.Min((long)_streamReceiveWindowSize * 2, int.MaxValue);

        // 평문 포트에서 HTTP/1.1 과 협상할 방법이 없다(ALPN 은 TLS 의 것). h2c 전용으로
        // 고정해야 사전 지식(prior knowledge) 클라이언트가 붙는다.
        kestrelOptions.Listen(_listenEndPoint, listen => listen.Protocols = HttpProtocols.Http2);

        SocketTransportFactory socketTransport = new(
            Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance);

        KestrelServer server = new(Options.Create(kestrelOptions), socketTransport, NullLoggerFactory.Instance);

        try
        {
            await server.StartAsync(new Application(this), cancellationToken).ConfigureAwait(false);

            // 포트 0 으로 바인드했으면 실제 배정된 포트를 주소 기능에서 읽는다.
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
    /// <remarks>
    /// Kestrel 은 리스너만 닫는 단계가 없으므로 신규 스트림을 <c>503</c> 으로 거부한다 —
    /// 로드밸런서 드레인 패턴. 기존 스트림은 계속 산다.
    /// </remarks>
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 바인드되지 않았어도 조용히 성공한다(IServerTransport 계약).
        Interlocked.CompareExchange(ref _state, 2, 1);
        return ValueTask.CompletedTask;
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
                // 드레인 제한 시간이 끝났다. 남은 것은 끊는다.
                foreach (ActiveConnection active in _connections.Values)
                {
                    active.Connection.Abort(ConnectionCloseInfo.ShuttingDown);
                }

#pragma warning disable CA1031 // 종료 경로다. 개별 커넥션의 예외로 전체 정리를 멈추지 않는다.
                try
                {
                    // 상한이 있어야 한다 — 취소 토큰을 무시하는 사용자 핸들러가 서버 종료를
                    // 볼모로 잡지 않게(TCP·인메모리와 같은 장치).
                    await Task.WhenAll(pending)
                        .WaitAsync(_shutdownTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 강제 종료된 커넥션의 예외(이미 기록됨)거나 상한 초과다.
                }
#pragma warning restore CA1031
            }
        }

        // 스트림이 전부 끝난 뒤 Kestrel 을 내린다. 남은 것이 있어도(상한 초과) Kestrel 의
        // 종료가 상한 안에서 연결을 강제로 끊는다.
        KestrelServer? server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
#pragma warning disable CA1031 // 종료 경로다. 리스너 정리 실패가 호출자의 종료를 막지 않는다.
            try
            {
                using CancellationTokenSource stopLimit = new(_shutdownTimeout);
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
        // 이미 종료 중이면 즉시 끊는다. Dispose 는 기다려주는 자리가 아니다.
        using CancellationTokenSource immediate = new();
        await immediate.CancelAsync().ConfigureAwait(false);

        await StopAsync(immediate.Token).ConfigureAwait(false);
    }

    /// <summary>요청 하나를 커넥션으로 수용해 핸들러 전 생애를 돌린다.</summary>
    /// <remarks>
    /// 인메모리·TCP 전송의 수락 루프와 같은 골격이다(Template Method, CLAUDE.md 4장) —
    /// 등록 → 핸들러 → <c>finally</c> 에서 제거·정리. <c>finally</c> 의 목록 제거를 빠뜨리면
    /// 종료된 커넥션이 영원히 남아 <see cref="StopAsync"/> 가 끝나지 않는다(CLAUDE.md 9.2).
    /// </remarks>
    private async Task ProcessRequestAsync(IFeatureCollection features)
    {
        IHttpRequestFeature request = features.GetRequiredFeature<IHttpRequestFeature>();
        IHttpResponseFeature response = features.GetRequiredFeature<IHttpResponseFeature>();
        IHttpResponseBodyFeature responseBody = features.GetRequiredFeature<IHttpResponseBodyFeature>();

        // 이 전송은 프레임 스트림 하나만 나른다. 다른 경로·메서드는 커넥션이 아니다.
        if (!string.Equals(request.Path, _path, StringComparison.Ordinal))
        {
            response.StatusCode = 404;
            return;
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
        {
            response.StatusCode = 405;
            return;
        }

        IConnectionHandler? handler = _handler;
        if (handler is null || Volatile.Read(ref _state) != 1)
        {
            // 드레인 창 — 로드밸런서가 이 신호로 트래픽을 다른 노드로 돌린다.
            EmitRejected(response, CloseReasonTags.Draining);
            return;
        }

        // 증가 후 검사-롤백 — 요청은 여러 IO 스레드에서 동시에 도착하므로 Count 검사로는
        // 상한을 소폭 초과할 수 있다. 유계는 엄격해야 유계다(CLAUDE.md 9.6).
        if (Interlocked.Increment(ref _activeCount) > _maxConnections)
        {
            Interlocked.Decrement(ref _activeCount);

            // 거부가 붕괴보다 낫다.
            EmitRejected(response, CloseReasonTags.ConnectionLimit);
            return;
        }

        HttpServerConnection? connection = null;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            IHttpRequestLifetimeFeature lifetime = features.GetRequiredFeature<IHttpRequestLifetimeFeature>();
            IHttpConnectionFeature? httpConnection = features.Get<IHttpConnectionFeature>();

            IPEndPoint? remote = httpConnection?.RemoteIpAddress is { } remoteIp
                ? new IPEndPoint(remoteIp, httpConnection.RemotePort)
                : null;
            IPEndPoint? local = httpConnection?.LocalIpAddress is { } localIp
                ? new IPEndPoint(localIp, httpConnection.LocalPort)
                : _localEndPoint;

            // 요청 본문은 파이프로 직접 읽는다 — Stream 어댑터 계층을 끼우지 않는다.
            PipeReader input = features.Get<IRequestBodyPipeFeature>()?.Reader
                ?? PipeReader.Create(request.Body);

            // 수용 확정(200)을 즉시 내보낸다. 이 플러시가 있어야 클라이언트의 연결 수립이
            // 완료되고, 이후의 응답 프레임을 스트리밍으로 읽는다.
            response.StatusCode = 200;
            response.Headers.ContentType = "application/octet-stream";
            await responseBody.StartAsync(lifetime.RequestAborted).ConfigureAwait(false);
            await responseBody.Writer.FlushAsync(lifetime.RequestAborted).ConfigureAwait(false);

#pragma warning disable CA2000 // 이 메서드의 finally 가 DisposeAsync 를 보장한다(수락 루프 골격).
            connection = new HttpServerConnection(
                NextConnectionId(), input, responseBody.Writer, local, remote,
                _shutdownTimeout, lifetime.RequestAborted);
#pragma warning restore CA2000

            // 등록이 핸들러 기동보다 먼저다 — 즉시 끝난 핸들러의 정리가 등록보다 먼저
            // 실행되면 죽은 항목이 영구히 남는다(인메모리·TCP 와 같은 결함 부류).
            _connections[connection.Id] = new ActiveConnection(connection, completion.Task);

            await handler.RunAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료·스트림 리셋으로 인한 취소. 정상 경로다.
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

#pragma warning disable CA1031 // 여기서 새어 나간 예외는 Kestrel 요청 루프 밖으로 빠져 프로세스를 죽인다.
            try
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 정리 실패(이미 리셋된 스트림 등)가 드레인 신호를 막으면 안 된다.
            }
            finally
            {
                // StopAsync 가 이 신호로 드레인 완료를 판정한다. 정리가 던져도 반드시 알린다.
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
                (Limit: _maxConnections, Reason: reason),
                null,
                static (state, _) => $"스트림을 거부했다(사유: {state.Reason}, 동시 접속 상한 {state.Limit}).");
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

    private readonly record struct ActiveConnection(HttpServerConnection Connection, Task Completion);

    /// <summary>Kestrel 에 넘기는 요청 처리기. 요청 = 커넥션이라는 대응의 진입점.</summary>
    /// <remarks>
    /// <see cref="IHttpApplication{TContext}"/> 를 직접 구현하면 <c>HttpContext</c> 조립 비용과
    /// 호스팅 미들웨어 스택이 통째로 사라진다 — 요청 기능(feature) 컬렉션만 그대로 쓴다.
    /// </remarks>
    private sealed class Application(HttpServerTransport transport) : IHttpApplication<RequestContext>
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
