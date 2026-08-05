using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;
using ChServerM.Transports;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChServerM.Bench.LoadRunner;

/// <summary>
/// Kestrel Socket Transport 를 <see cref="IServerTransport"/> 로 감싼 <b>ADR-0001 벤치 대결
/// 전용 프로토타입</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0001("Kestrel 재사용 vs 순수 Socket+Pipelines")은 양쪽 실측 없이
/// 확정하지 않기로 했다. 순수 소켓 쪽은 `ChServerM.Transport.Tcp` 가 실물이고, 이 클래스가
/// Kestrel 쪽 비교 대상이다. <b>제품 코드가 아니다</b> — 판정이 끝나면 수치와 ADR 만 남는다.
/// </para>
/// <para>
/// <b>프로토타입 한계(공정성 주석).</b> idle timeout, 거부 통지(40004), MaxConnections
/// 상한, ConnectionId 세대 재사용 같은 프로덕션 기능이 없다. 즉 이 비교는
/// "Kestrel 소켓 엔진의 순수 데이터 경로" vs "우리 전송의 전체 기능 경로"라서
/// <b>Kestrel 쪽에 유리하게 기울어 있다.</b> 그래도 우리가 이기거나 비기면 결론은 강하다.
/// </para>
/// </remarks>
internal sealed class KestrelSocketServerTransport : IServerTransport, ITransportBufferLimits
{
    private readonly EndPoint _bindEndPoint;
    private readonly SocketTransportOptions _options;
    private readonly ConcurrentDictionary<ConnectionId, Task> _connections = new();

    private SocketTransportFactory? _factory;
    private IConnectionListener? _listener;
    private IConnectionHandler? _handler;
    private Task _acceptLoop = Task.CompletedTask;
    private uint _nextSlot;
    private int _bound;

    public KestrelSocketServerTransport(EndPoint endPoint, SocketTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        _bindEndPoint = endPoint;

        // 우리 전송의 기본값과 조건을 맞춘다 — NoDelay, 0바이트 수신 대기.
        _options = options ?? new SocketTransportOptions
        {
            NoDelay = true,
            WaitForDataBeforeAllocatingBuffer = true,
        };
    }

    public EndPoint? LocalEndPoint => _listener?.EndPoint;

    public int ConnectionCount => _connections.Count;

    /// <inheritdoc />
    public long MaxBufferedBytesPerConnection => _options.MaxReadBufferSize ?? long.MaxValue;

    public async ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (Interlocked.CompareExchange(ref _bound, 1, 0) != 0)
        {
            throw new InvalidOperationException($"{_bindEndPoint} 에 이미 바인드돼 있다.");
        }

        _handler = handler;
        _factory = new SocketTransportFactory(Options.Create(_options), NullLoggerFactory.Instance);
        _listener = await _factory.BindAsync(_bindEndPoint, cancellationToken).ConfigureAwait(false);
        _acceptLoop = AcceptLoopAsync(_listener, handler);
    }

    private async Task AcceptLoopAsync(IConnectionListener listener, IConnectionHandler handler)
    {
        while (true)
        {
            ConnectionContext? context;

            try
            {
                context = await listener.AcceptAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 벤치 프로토타입 — 수락 루프가 어떤 예외로도 조용히 죽지 않게 넓게 잡는다.
            catch (Exception)
#pragma warning restore CA1031
            {
                break;
            }

            if (context is null)
            {
                // Unbind 됐다 — 수락 종료.
                break;
            }

            ConnectionId id = new(Interlocked.Increment(ref _nextSlot), generation: 0);
            KestrelConnectionAdapter connection = new(id, context);
            _connections[id] = RunConnectionAsync(id, connection, handler);
        }
    }

    private async Task RunConnectionAsync(
        ConnectionId id, KestrelConnectionAdapter connection, IConnectionHandler handler)
    {
        // 수락 루프를 커넥션 처리로 막지 않는다.
        await Task.Yield();

        try
        {
            await handler.RunAsync(connection).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 핸들러 예외는 커넥션 중단으로 처리한다 — 프로세스를 죽이지 않는다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _connections.TryRemove(id, out _);
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        IConnectionListener? listener = _listener;
        if (listener is not null)
        {
            await listener.UnbindAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await UnbindAsync(cancellationToken).ConfigureAwait(false);
        await _acceptLoop.ConfigureAwait(false);

        // 드레인 — 제한 시간이 지나면 남은 커넥션을 중단한다.
        Task drain = Task.WhenAll([.. _connections.Values]);

        try
        {
            await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (Task remaining in _connections.Values)
            {
                _ = remaining; // 어댑터 Dispose 는 RunConnectionAsync finally 가 수행한다.
            }
        }

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
        _factory = null;
    }

    /// <summary>Kestrel <see cref="ConnectionContext"/> 를 <see cref="IConnection"/> 으로 감싼다.</summary>
    private sealed class KestrelConnectionAdapter : IConnection, IConnectionEndPointFeature
    {
        private readonly ConnectionContext _context;

        internal KestrelConnectionAdapter(ConnectionId id, ConnectionContext context)
        {
            Id = id;
            _context = context;

            FeatureCollection features = new(capacity: 1);
            features.Set<IConnectionEndPointFeature>(this);
            Features = features;
        }

        public ConnectionId Id { get; }

        public PipeReader Input => _context.Transport.Input;

        public PipeWriter Output => _context.Transport.Output;

        public IFeatureCollection Features { get; }

        public CancellationToken ConnectionClosed => _context.ConnectionClosed;

        public EndPoint? LocalEndPoint => _context.LocalEndPoint;

        public EndPoint? RemoteEndPoint => _context.RemoteEndPoint;

        public void Abort(in ConnectionCloseInfo info) => _context.Abort();

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }
}
