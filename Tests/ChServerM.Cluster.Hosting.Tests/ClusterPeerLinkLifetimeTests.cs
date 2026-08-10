using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Cluster;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Cluster.Hosting.Tests;

/// <summary>
/// 피어 링크의 <b>수명</b> 검증 — ADR-0050 이 "미검증" 으로 남겨 둔 넷을 닫는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 미검증으로 적어 둔 것은 결국 결함을 감춘다.</b> ADR-0050 은 큐 포화·
/// 대여 버퍼 반납·재연결을 "구조로만 보장" 한다고 적었는데, 그 셋을 실제로 돌려 보니
/// <b>재연결이 아예 트리거되지 않는</b> 결함이 나왔다: 상대가 파이프를 닫아도
/// <see cref="System.IO.Pipelines.PipeWriter.FlushAsync"/> 는 <b>예외를 던지지 않고</b>
/// <see cref="System.IO.Pipelines.FlushResult.IsCompleted"/> 만 <see langword="true"/> 로
/// 돌려준다. 그 결과를 버리면 링크는 <b>살아 있는 척하며 모든 프레임을 조용히 삼킨다</b> —
/// 레거시의 조용한 유실(CLAUDE.md 9.6)과 정확히 같은 모양이다.
/// </para>
/// <para>
/// 여기서 계약으로 고정하는 것: <b>큐 포화의 결정적 거절</b> ·
/// <b>모든 경로에서 대여 버퍼 반납</b> · <b>상대 종료 감지와 재연결</b> ·
/// <b>끊길 때 큐의 프레임은 사라진다(재전송하지 않는다)</b>.
/// </para>
/// <para>
/// <b>결정성을 어떻게 얻었는가.</b> "소비자를 멈춰 세울 방법이 없다" 가 ADR-0050 이
/// 큐 포화를 검증하지 못한 이유였다. <see cref="GatedClientTransport"/> 가 그 답이다 —
/// 연결 수립 자체를 붙잡으면 소비자는 첫 프레임에서 멈추고, 큐는 <b>정확히</b> 채워진다.
/// <see cref="Task.Delay(int)"/> 로 "아마 밀렸겠지" 를 기대하지 않는다.
/// </para>
/// </remarks>
public sealed class ClusterPeerLinkLifetimeTests : IAsyncLifetime
{
    private static readonly MessageId PeerMessage = new(301);

    private readonly FramingOptions _framing = new() { MaxPayloadLength = 4096 };
    private readonly InMemoryTransportHub _hub = new();
    private readonly InMemoryTransportOptions _transportOptions = new();
    private readonly List<IAsyncDisposable> _owned = [];

    private InMemoryEndPoint _peerEndPoint = null!;
    private Channel<byte[]> _received = null!;

    public Task InitializeAsync()
    {
        _received = Channel.CreateUnbounded<byte[]>();
        _peerEndPoint = new InMemoryEndPoint($"peer-{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            await _owned[i].DisposeAsync();
        }
    }

    // ── 큐 포화 ──────────────────────────────────────────────────────

    [Fact]
    public async Task QueueFull_isRejected_notSilentlyDropped()
    {
        // ⭐ 레거시는 Wait 모드에 TryWrite 를 써 놓고 반환값을 버려 조용히 유실했다(9.6).
        //   여기서는 그 false 가 호출자에게 나온다는 것을 **실제 포화로** 증명한다.
        await using ChServerMServer peer = await StartPeerAsync();
        GatedClientTransport gate = new(NewClientTransport());
        ChServerMClient client = BuildClient(gate);

        await using StaticClusterMembership membership = Membership();
        CountingArrayPool pool = new();
        ClusterPeerSet peers = PeerSet(
            membership, client, new ClusterPeerOptions { SendQueueDepth = 4 }, pool);

        // 소비자는 첫 프레임의 연결 수립에서 멈춘다 — 큐는 결정적으로 찬다.
        List<PeerSendStatus> results = [];
        for (int i = 0; i < 32; i++)
        {
            results.Add(await peers.SendAsync(
                new NodeId(2), PeerMessage, new byte[] { (byte)i }, CancellationToken.None));

            if (results[^1] == PeerSendStatus.QueueFull)
            {
                break;
            }
        }

        Assert.Equal(PeerSendStatus.QueueFull, results[^1]);

        // 깊이 4 + 소비자가 붙들고 있는 1 = 5 를 넘겨서 찬다. 그 전에 차면 계약 위반이다.
        Assert.All(results[..^1], status => Assert.Equal(PeerSendStatus.Sent, status));
        Assert.InRange(results.Count, 5, 7);

        // ⚠ 거절된 프레임의 대여 버퍼는 그 자리에서 반납된다 — 거절이 곧 누수면 안 된다.
        gate.Release();
        await peers.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    // ── 대여 버퍼 반납 ───────────────────────────────────────────────

    [Fact]
    public async Task EveryPath_returnsRentedBuffers()
    {
        // 정상 전송 · 종료 시 큐에 남은 것 · 쓰기 실패 — 셋 다 반납되어야 한다.
        // 누수는 한참 뒤 할당 폭증으로만 나타나 원인을 찾을 수 없다(9.2·9.7).
        ChServerMServer peer = await StartPeerAsync();
        ChServerMClient client = BuildClient(NewClientTransport());

        await using StaticClusterMembership membership = Membership();
        CountingArrayPool pool = new();
        ClusterPeerSet peers = PeerSet(membership, client, new ClusterPeerOptions(), pool);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(
                PeerSendStatus.Sent,
                await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { (byte)i }, CancellationToken.None));
        }

        for (int i = 0; i < 50; i++)
        {
            _ = await ReadAsync();
        }

        Assert.Equal(0, pool.Outstanding);

        // 상대를 죽여 쓰기 실패 경로를 태운다.
        await peer.DisposeAsync();
        for (int i = 0; i < 50; i++)
        {
            await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { (byte)i }, CancellationToken.None);
        }

        await peers.DisposeAsync();

        Assert.Equal(0, pool.Outstanding);
        Assert.True(pool.Rented > 0, "이 테스트가 실제로 풀을 쓰지 않았다면 검증한 것이 없다.");
    }

    // ── 재연결 ───────────────────────────────────────────────────────

    [Fact]
    public async Task PeerClosesLink_isDetected_andNextSendReconnects()
    {
        // ⚠⚠ 이것이 ADR-0050 의 "재연결" 이 실제로는 트리거되지 않던 자리다.
        //   FlushAsync 는 상대가 닫혀도 던지지 않는다 — IsCompleted 를 봐야 안다.
        ChServerMServer peer = await StartPeerAsync();
        ChServerMClient client = BuildClient(NewClientTransport());

        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership, client, new ClusterPeerOptions());

        Assert.Equal(
            PeerSendStatus.Sent,
            await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal([1], await ReadAsync());

        // 상대가 사라진다. 클라이언트 쪽 커넥션은 예외를 보지 못한다.
        await peer.DisposeAsync();

        // 같은 주소로 새 피어가 올라온다(롤링 배포와 같은 모양이다).
        ChServerMServer restarted = await StartPeerAsync();
        _owned.Add(restarted);

        // 죽은 링크를 감지하고 다시 연결해 **실제로 도착해야 한다**.
        // 감지에 프레임 한 장이 소모될 수 있다 — 재전송하지 않는 것이 계약이므로
        // 그 한 장의 유실은 허용하되, 링크가 영영 삼키는 것은 허용하지 않는다.
        byte[]? arrived = null;
        for (int attempt = 0; attempt < 20 && arrived is null; attempt++)
        {
            await peers.SendAsync(
                new NodeId(2), PeerMessage, new byte[] { 7 }, CancellationToken.None);
            arrived = await TryReadAsync(TimeSpan.FromMilliseconds(500));
        }

        Assert.NotNull(arrived);
        Assert.Equal([7], arrived);
    }

    [Fact]
    public async Task WriteToClosedReader_isDetected_whenNothingThrowsAndNothingElseSignals()
    {
        // ⭐ 이 테스트만이 FlushResult 검사를 **홀로** 증명한다.
        //   앞의 재연결 테스트는 읽기 루프 완료라는 다른 신호로도 통과하므로,
        //   FlushResult 를 다시 버려도 초록이다 — 그것을 확인하고 이 테스트를 붙였다.
        //
        //   여기서는 상대가 **읽기 쪽만** 닫은 상태를 만든다(TCP 의 half-close 와 같다).
        //   ConnectionClosed 는 발화하지 않고, 읽기 루프도 끝나지 않으며,
        //   FlushAsync 는 예외 없이 IsCompleted 만 세운다. 그것을 보지 않으면
        //   링크는 영원히 살아 있는 척한다.
        HalfClosedTransport transport = new();
        ChServerMClient client = BuildClient(transport);

        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership, client, new ClusterPeerOptions());

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                PeerSendStatus.Sent,
                await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { 1 }, CancellationToken.None));

            await WaitForAsync(() => transport.ConnectCount >= i + 1);
        }

        // 죽은 것을 알아차렸다면 전송마다 새로 연결한다. 못 알아차리면 1 에서 멈춘다.
        await WaitForAsync(() => transport.ConnectCount >= 3);
        Assert.True(
            transport.ConnectCount >= 3,
            $"상대가 읽기를 닫았는데도 링크를 다시 열지 않았다(연결 {transport.ConnectCount}회). " +
            "FlushResult 를 버리면 이 링크는 모든 프레임을 조용히 삼킨다.");

        // ⚠ 재연결마다 옛 커넥션을 놓아주어야 한다. 참조만 지우면 소켓과 읽기 루프가
        //   하나씩 쌓이고, 증상은 며칠 뒤 핸들 고갈로만 나타난다.
        await WaitForAsync(() => transport.UndisposedCount <= 1);
        Assert.True(
            transport.UndisposedCount <= 1,
            $"끊긴 커넥션을 놓아주지 않았다(살아 있는 커넥션 {transport.UndisposedCount}개). " +
            "재연결마다 하나씩 샌다.");
    }

    [Fact]
    public async Task DeadLink_doesNotLeakConnections()
    {
        // 끊길 때마다 옛 커넥션을 놓아주지 않으면 소켓이 샌다.
        // 인메모리 전송은 그것을 ConnectionCount 로 보여 준다.
        ChServerMServer peer = await StartPeerAsync();
        InMemoryClientTransport transport = NewClientTransport();
        ChServerMClient client = BuildClient(transport);

        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership, client, new ClusterPeerOptions());

        await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { 1 }, CancellationToken.None);
        _ = await ReadAsync();

        await peer.DisposeAsync();

        // 상대가 없는 동안의 전송은 실패한다. 실패마다 커넥션이 쌓이면 안 된다.
        for (int i = 0; i < 20; i++)
        {
            await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { (byte)i }, CancellationToken.None);
        }

        await peers.DisposeAsync();
        Assert.Equal(0, peers.OpenLinkCount);
    }

    // ── 배관 ─────────────────────────────────────────────────────────

    private async Task<ChServerMServer> StartPeerAsync()
    {
        ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(_hub, _peerEndPoint, _transportOptions))
            .UseFraming(new FixedHeaderFrameDecoder(_framing), new FixedHeaderFrameEncoder(_framing))
            .ConfigureDispatcher(d => d.MapRaw(PeerMessage, context =>
            {
                _received.Writer.TryWrite(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        await server.StartAsync(CancellationToken.None);
        return server;
    }

    private InMemoryClientTransport NewClientTransport() => new(_hub, null, _transportOptions);

    private ChServerMClient BuildClient(IClientTransport transport)
    {
        ChServerMClient client = new ClientBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(_framing), new FixedHeaderFrameEncoder(_framing))
            .Build();

        _owned.Add(client);
        return client;
    }

    private StaticClusterMembership Membership()
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };
        options.Nodes.Add((new NodeId(1), "self", new InMemoryEndPoint("unused-self")));
        options.Nodes.Add((new NodeId(2), "peer", _peerEndPoint));
        return new StaticClusterMembership(options);
    }

    private ClusterPeerSet PeerSet(
        IClusterMembership membership,
        ChServerMClient client,
        ClusterPeerOptions options,
        ArrayPool<byte>? pool = null)
    {
        // 풀은 필수 인자다(ADR-0051) — 지정하지 않은 테스트는 Shared 로 돈다.
        ClusterPeerSet set = new(
            membership, client, options, NullServerLogger.Instance, pool ?? ArrayPool<byte>.Shared);

        _owned.Add(set);
        return set;
    }

    private async Task<byte[]> ReadAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        return await _received.Reader.ReadAsync(timeout.Token);
    }

    private async Task<byte[]?> TryReadAsync(TimeSpan window)
    {
        using CancellationTokenSource timeout = new(window);
        try
        {
            return await _received.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>조건이 참이 될 때까지 기다린다. 참이 되지 않으면 창이 끝나고 호출자가 단정한다.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>읽는 쪽이 이미 닫힌 커넥션만 돌려주는 전송.</summary>
    /// <remarks>
    /// <b>TCP 의 half-close 를 프로세스 안에서 재현한다.</b> 상대가 <c>FIN</c> 을 보낸 뒤
    /// 우리 쪽 소켓에 쓰면 커널 버퍼가 받아 주므로 <b>쓰기는 한동안 성공한다</b> —
    /// 예외도, 커넥션 종료 신호도 없다. 파이프에서 그에 대응하는 것이
    /// <see cref="FlushResult.IsCompleted"/> 다.
    /// </remarks>
    private sealed class HalfClosedTransport : IClientTransport
    {
        private readonly ConcurrentDictionary<HalfClosedConnection, byte> _live = new();
        private int _connects;

        public int ConnectCount => Volatile.Read(ref _connects);

        /// <summary>아직 해제되지 않은 커넥션 수. <b>재연결 누수를 관측 가능하게 한다.</b></summary>
        public int UndisposedCount
        {
            get
            {
                int alive = 0;
                foreach (HalfClosedConnection connection in _live.Keys)
                {
                    if (!connection.IsDisposed)
                    {
                        alive++;
                    }
                }

                return alive;
            }
        }

        public ValueTask<IConnection> ConnectAsync(
            EndPoint endPoint, CancellationToken cancellationToken = default)
        {
            HalfClosedConnection connection = new(new ConnectionId((uint)Interlocked.Increment(ref _connects), 1));
            _live[connection] = 0;
            return ValueTask.FromResult<IConnection>(connection);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (HalfClosedConnection connection in _live.Keys)
            {
                _live.TryRemove(connection, out _);
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>쓰기는 받지만 읽는 쪽이 완료된 커넥션.</summary>
    private sealed class HalfClosedConnection : IConnection
    {
        private readonly Pipe _outbound = new();
        private readonly Pipe _inbound = new();
        private readonly CancellationTokenSource _closed = new();
        private int _disposed;

        public HalfClosedConnection(ConnectionId id)
        {
            Id = id;

            // ⭐ 상대가 읽기를 끝냈다. 이제 Output.FlushAsync 는 던지지 않고
            //   IsCompleted 만 세운다 — 이 테스트가 겨누는 바로 그 상태다.
            _outbound.Reader.Complete();
        }

        public ConnectionId Id { get; }

        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        public PipeReader Input => _inbound.Reader;

        public PipeWriter Output => _outbound.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(0);

        /// <summary>발화하지 않는다 — 그래야 FlushResult 만 남는다.</summary>
        public CancellationToken ConnectionClosed => _closed.Token;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            // 읽기 루프를 놓아준다 — 아니면 정리가 그 태스크를 기다리다 멈춘다.
            await _inbound.Writer.CompleteAsync();
            await _outbound.Writer.CompleteAsync();
            await _closed.CancelAsync();
            _closed.Dispose();
        }
    }

    /// <summary>연결 수립을 붙잡아 두는 전송. <b>소비자를 결정적으로 멈춰 세운다.</b></summary>
    /// <remarks>
    /// 큐 포화를 검증하려면 소비자가 확실히 멈춰 있어야 한다. 지연으로 흉내 내면
    /// 느린 CI 에서 깨지거나(거짓 실패) 빠른 기계에서 통과만 하고(거짓 성공)
    /// 아무것도 증명하지 못한다.
    /// </remarks>
    private sealed class GatedClientTransport(InMemoryClientTransport inner) : IClientTransport
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async ValueTask<IConnection> ConnectAsync(
            EndPoint endPoint, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _gate.TrySetResult();
            return inner.DisposeAsync();
        }
    }

    /// <summary>대여와 반납을 센다. <b>누수를 관측 가능하게 만드는 것이 목적이다.</b></summary>
    /// <remarks>
    /// 이중 반납도 잡는다 — 반납한 배열을 다시 반납하면 풀이 같은 배열을 두 곳에
    /// 빌려주고, 그 증상은 "가끔 페이로드가 섞인다" 로 나타나 추적이 거의 불가능하다.
    /// </remarks>
    private sealed class CountingArrayPool : ArrayPool<byte>
    {
        // byte[] 는 Equals 를 재정의하지 않으므로 기본 비교자가 곧 참조 동일성이다.
        private readonly ConcurrentDictionary<byte[], byte> _live = new();

        private int _rented;
        private int _outstanding;

        public int Rented => Volatile.Read(ref _rented);

        public int Outstanding => Volatile.Read(ref _outstanding);

        public override byte[] Rent(int minimumLength)
        {
            byte[] buffer = new byte[Math.Max(minimumLength, 1)];
            _live[buffer] = 0;
            Interlocked.Increment(ref _rented);
            Interlocked.Increment(ref _outstanding);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_live.TryRemove(array, out _), "빌린 적 없거나 이미 반납한 배열을 반납했다.");
            Interlocked.Decrement(ref _outstanding);
        }
    }
}
