using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Cluster;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Cluster.Hosting.Tests;

/// <summary>
/// 피어 링크의 종단 검증 (ADR-0050) — <b>실제 두 노드가 프레임을 주고받는다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>새 전송을 만들지 않았다는 것이 여기서 증명된다.</b> 이 테스트는 앱이
/// <see cref="ClientBuilder"/>·<see cref="ServerBuilder"/> 로 고른 축(InMemory 전송 +
/// 고정 헤더 프레이밍)을 그대로 쓴다 — 피어 통신 전용 경로가 없다는 뜻이다.
/// </para>
/// <para>
/// 계약으로 고정하는 것: <b>실제 도착</b> · <b>순서</b> · <b>동시 전송이 스트림을 깨지
/// 않음</b> · <b>루프백 거부</b> · <b>비구성원 거부</b> · <b>구성원에서 빠지면 링크가 닫힘</b>.
/// </para>
/// <para>
/// <b>⚠ 여기서 검증하지 <i>않는</i> 것.</b> 큐 포화(<see cref="PeerSendStatus.QueueFull"/>)는
/// 소비자를 결정적으로 멈춰 세울 방법이 없어 테스트가 없다 — 구조(유계 채널 +
/// <c>TryWrite</c> 반환값 확인)로만 보장한다. 대여 버퍼 반납도 <c>finally</c> 로만 보장하며
/// 누수를 관측하는 테스트가 없다. <b>둘 다 ADR-0050 에 미검증으로 적어 뒀다</b> —
/// 검증하지 않은 것을 검증한 것처럼 적는 것이 이 문서를 못 믿게 만든다.
/// </para>
/// </remarks>
public sealed class ClusterPeerSetTests : IAsyncLifetime
{
    private static readonly MessageId PeerMessage = new(300);

    private readonly FramingOptions _framing = new() { MaxPayloadLength = 4096 };
    private readonly InMemoryTransportHub _hub = new();
    private readonly InMemoryTransportOptions _transportOptions = new();
    private readonly List<IAsyncDisposable> _owned = [];

    private ChServerMServer _peerServer = null!;
    private ChServerMClient _client = null!;
    private InMemoryEndPoint _peerEndPoint = null!;
    private Channel<byte[]> _received = null!;

    public async Task InitializeAsync()
    {
        _received = Channel.CreateUnbounded<byte[]>();
        _peerEndPoint = new InMemoryEndPoint($"peer-{Guid.NewGuid():N}");

        _peerServer = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(_hub, _peerEndPoint, _transportOptions))
            .UseFraming(new FixedHeaderFrameDecoder(_framing), new FixedHeaderFrameEncoder(_framing))
            .ConfigureDispatcher(d => d.MapRaw(PeerMessage, context =>
            {
                _received.Writer.TryWrite(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        _owned.Add(_peerServer);
        await _peerServer.StartAsync(CancellationToken.None);

        _client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(_hub, null, _transportOptions))
            .UseFraming(new FixedHeaderFrameDecoder(_framing), new FixedHeaderFrameEncoder(_framing))
            .Build();

        _owned.Add(_client);
    }

    public async Task DisposeAsync()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            await _owned[i].DisposeAsync();
        }
    }

    /// <summary>자기(1)와 피어(2). 피어의 주소는 실제로 살아 있는 서버다.</summary>
    private StaticClusterMembership Membership(ushort self = 1)
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(self) };
        options.Nodes.Add((new NodeId(1), "self", new InMemoryEndPoint("unused-self")));
        options.Nodes.Add((new NodeId(2), "peer", _peerEndPoint));
        return new StaticClusterMembership(options);
    }

    private ClusterPeerSet PeerSet(IClusterMembership membership, ClusterPeerOptions? options = null)
    {
        // 풀은 필수 인자다(ADR-0051). 이 테스트들은 얕은 큐라 Shared 가 맞는 선택이다.
        ClusterPeerSet set = new(
            membership,
            _client,
            options ?? new ClusterPeerOptions(),
            NullServerLogger.Instance,
            ArrayPool<byte>.Shared);

        _owned.Add(set);
        return set;
    }

    // ── 실제로 도착한다 ──────────────────────────────────────────────

    [Fact]
    public async Task SendToPeer_arrivesAtTheRemoteDispatcher()
    {
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership);

        PeerSendStatus status = await peers.SendAsync(
            new NodeId(2), PeerMessage, new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Equal(PeerSendStatus.Sent, status);

        byte[] arrived = await ReadAsync();
        Assert.Equal([1, 2, 3], arrived);
        Assert.Equal(1, peers.OpenLinkCount);
    }

    [Fact]
    public async Task ManySends_arriveInOrder()
    {
        // ⚠ 피어당 소비자가 하나라는 계약의 관측 가능한 결과다. 둘이면 순서가 섞인다.
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership);

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(
                PeerSendStatus.Sent,
                await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { (byte)i }, CancellationToken.None));
        }

        for (int i = 0; i < 200; i++)
        {
            byte[] arrived = await ReadAsync();
            Assert.Equal((byte)i, arrived[0]);
        }
    }

    [Fact]
    public async Task ConcurrentSends_doNotCorruptTheStream()
    {
        // PipeWriter 는 동시 기록을 허용하지 않는다. 채널이 직렬화하지 않으면
        // 프레임이 섞여 상대 디코더가 깨진다 — 그러면 아래 수신 개수가 모자란다.
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership);

        Task[] senders = new Task[8];
        for (int t = 0; t < senders.Length; t++)
        {
            int worker = t;
            senders[t] = Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    await peers.SendAsync(
                        new NodeId(2), PeerMessage, new byte[] { (byte)worker, (byte)i }, CancellationToken.None);
                }
            });
        }

        await Task.WhenAll(senders);

        int total = 0;
        for (int i = 0; i < 400; i++)
        {
            _ = await ReadAsync();
            total++;
        }

        Assert.Equal(400, total);
    }

    // ── 거절의 계약 ──────────────────────────────────────────────────

    [Fact]
    public async Task SendToSelf_isLoopback_notSilentSuccess()
    {
        // ⭐ 조용히 성공시키면 자기에게 연결하는 커넥션이 생겨 접속 한도와 통계를
        // 오염시킨다. 로컬 단락을 빠뜨린 코드를 드러내야 한다.
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership);

        PeerSendStatus status = await peers.SendAsync(
            new NodeId(1), PeerMessage, new byte[] { 9 }, CancellationToken.None);

        Assert.Equal(PeerSendStatus.Loopback, status);
        Assert.Equal(0, peers.OpenLinkCount);
    }

    [Fact]
    public async Task SendToStranger_isNotAMember()
    {
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership);

        Assert.Equal(
            PeerSendStatus.NotAMember,
            await peers.SendAsync(new NodeId(99), PeerMessage, new byte[] { 9 }, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedPayload_throwsBeforeQueueing()
    {
        // 상한이 없으면 큐 깊이만으로 메모리를 묶을 수 없다.
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = PeerSet(membership, new ClusterPeerOptions { MaxPayloadLength = 16 });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await peers.SendAsync(
                new NodeId(2), PeerMessage, new byte[32], CancellationToken.None));
    }

    [Fact]
    public async Task AfterDispose_sendsAreClosed()
    {
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = new(
            membership, _client, new ClusterPeerOptions(), NullServerLogger.Instance, ArrayPool<byte>.Shared);

        await peers.DisposeAsync();

        Assert.Equal(
            PeerSendStatus.Closed,
            await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        await using StaticClusterMembership membership = Membership();
        ClusterPeerSet peers = new(
            membership, _client, new ClusterPeerOptions(), NullServerLogger.Instance, ArrayPool<byte>.Shared);

        await peers.DisposeAsync();
        await peers.DisposeAsync();
    }

    // ── 구성원 변화 ──────────────────────────────────────────────────

    [Fact]
    public async Task DepartedPeer_hasItsLinkClosed()
    {
        // ⚠ 떠난 노드로 가는 커넥션을 남겨 두면 소켓과 큐가 살아 있고, 그 노드가 다시
        // 다른 역할로 들어올 때 **옛 링크로 보내게** 된다.
        ClusterNode self = new(new NodeId(1), "self", new InMemoryEndPoint("unused-self"));
        ClusterNode peer = new(new NodeId(2), "peer", _peerEndPoint);

        MutableMembership membership = new(self, new ClusterView([self, peer], generation: 1));
        ClusterPeerSet peers = PeerSet(membership);

        Assert.Equal(
            PeerSendStatus.Sent,
            await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { 1 }, CancellationToken.None));
        _ = await ReadAsync();
        Assert.Equal(1, peers.OpenLinkCount);

        // 피어가 구성원에서 빠진다. 다음 전송이 그것을 알아채고 링크를 닫는다.
        membership.Swap(new ClusterView([self], generation: 2));

        Assert.Equal(
            PeerSendStatus.NotAMember,
            await peers.SendAsync(new NodeId(2), PeerMessage, new byte[] { 2 }, CancellationToken.None));

        // 정리는 비동기라 잠깐 기다린다 — 보내는 경로가 정리 때문에 느려지지 않게 한 결과다.
        for (int i = 0; i < 100 && peers.OpenLinkCount != 0; i++)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        Assert.Equal(0, peers.OpenLinkCount);
    }

    /// <summary>뷰를 갈아 끼울 수 있는 테스트용 멤버십.</summary>
    private sealed class MutableMembership(ClusterNode self, ClusterView initial) : IClusterMembership
    {
        private ClusterView _view = initial;

        public ClusterNode Self { get; } = self;

        public ClusterView Current => Volatile.Read(ref _view);

        public void Swap(ClusterView view) => Volatile.Write(ref _view, view);

        public ValueTask<ClusterView> WaitForChangeAsync(int knownGeneration, CancellationToken cancellationToken) =>
            new(Current);

        public ValueTask DisposeAsync() => default;
    }

    // ── 조립 검증 ────────────────────────────────────────────────────

    [Fact]
    public async Task NullArguments_throw()
    {
        await using StaticClusterMembership membership = Membership();

        ArrayPool<byte> pool = ArrayPool<byte>.Shared;

        Assert.Throws<ArgumentNullException>(
            () => new ClusterPeerSet(null!, _client, new ClusterPeerOptions(), NullServerLogger.Instance, pool));
        Assert.Throws<ArgumentNullException>(
            () => new ClusterPeerSet(membership, null!, new ClusterPeerOptions(), NullServerLogger.Instance, pool));
        Assert.Throws<ArgumentNullException>(
            () => new ClusterPeerSet(membership, _client, null!, NullServerLogger.Instance, pool));
        Assert.Throws<ArgumentNullException>(
            () => new ClusterPeerSet(membership, _client, new ClusterPeerOptions(), null!, pool));

        // 풀은 필수 인자다 — 기본값이 없으므로 null 도 거부해야 한다(ADR-0051).
        Assert.Throws<ArgumentNullException>(
            () => new ClusterPeerSet(
                membership, _client, new ClusterPeerOptions(), NullServerLogger.Instance, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidQueueDepth_failsAtAssembly(int depth)
    {
        await using StaticClusterMembership membership = Membership();

        Assert.Throws<InvalidOperationException>(
            () => new ClusterPeerSet(
                membership,
                _client,
                new ClusterPeerOptions { SendQueueDepth = depth },
                NullServerLogger.Instance,
                ArrayPool<byte>.Shared));
    }

    [Fact]
    public async Task InvalidPayloadLimit_failsAtAssembly()
    {
        await using StaticClusterMembership membership = Membership();

        Assert.Throws<InvalidOperationException>(
            () => new ClusterPeerSet(
                membership,
                _client,
                new ClusterPeerOptions { MaxPayloadLength = 0 },
                NullServerLogger.Instance,
                ArrayPool<byte>.Shared));
    }

    private async Task<byte[]> ReadAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        return await _received.Reader.ReadAsync(timeout.Token);
    }
}
