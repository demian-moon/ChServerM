using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Cluster;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Cluster.Hosting.Tests;

/// <summary>
/// 다중 노드 시나리오 — <b>ADR-0051·0052·0054 가 "미검증" 으로 남긴 것을 닫는다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 지금까지의 클러스터 검증은 대부분 <b>피어가 하나</b>이거나
/// <b>뷰를 조립해 넣은 단위 테스트</b>였다. 그 조건에서는 드러나지 않는 것이 셋 있다:
/// 미처리 대여가 <b>피어 수에 비례</b>한다는 것(ADR-0051) · 분할이 <b>동작으로</b>
/// 어떻게 보이는가(ADR-0054) · 한 노드의 배포가 <b>다른 노드의 트래픽을 끊지 않는다</b>는 것.
/// </para>
/// <para>
/// <b>⭐ 결정성을 어떻게 얻었는가.</b> <c>GatedClientTransport</c>(연결 수립을 붙잡는다)로
/// 모든 피어의 소비자를 <b>첫 프레임에서</b> 멈춰 세운다. 그러면 각 피어의 큐는
/// <b>정확히</b> 깊이만큼 차고, 미처리 대여 수를 <b>짐작이 아니라 등식으로</b> 잴 수 있다.
/// <see cref="Task.Delay(int)"/> 로 "아마 밀렸겠지" 를 기대하지 않는다.
/// </para>
/// </remarks>
public sealed class MultiNodeClusterTests : IAsyncLifetime
{
    private static readonly MessageId PeerMessage = new(302);
    private static readonly PartitionKey LeaderRole = PartitionKey.FromValue(0xA11CE);

    private readonly FramingOptions _framing = new() { MaxPayloadLength = 4096 };
    private readonly InMemoryTransportHub _hub = new();
    private readonly InMemoryTransportOptions _transportOptions = new();
    private readonly List<IAsyncDisposable> _owned = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            await _owned[i].DisposeAsync();
        }
    }

    // ── ⭐⭐ ADR-0051 이 남긴 미검증: 깊이 × 피어 수 ──────────────────

    [Fact]
    public async Task OutstandingRentals_scaleWithPeerCount_notJustQueueDepth()
    {
        // ⭐⭐ 이것이 ADR-0051 결정 6 의 근거 중 **유일하게 재지 않았던 항**이다.
        //   "보관 개수 = 큐 깊이" 규칙이 틀린 이유: 보낼 큐는 **링크마다** 생기는데
        //   버퍼 풀은 **집합에 하나**다. 피어가 넷이면 최악 미처리 대여도 네 배다.
        const int PeerCount = 4;
        const int Depth = 4;

        CountingArrayPool pool = new();

        // 모든 피어의 소비자를 연결 수립에서 멈춘다 → 큐가 결정적으로 찬다.
        GatedClientTransport gate = new(NewClientTransport());
        ChServerMClient client = BuildClient(gate);

        await using StaticClusterMembership membership = Membership(PeerCount);
        ClusterPeerSet peers = PeerSet(
            membership, client, new ClusterPeerOptions { SendQueueDepth = Depth }, pool);

        for (ushort peer = 2; peer <= PeerCount + 1; peer++)
        {
            PeerSendStatus status = PeerSendStatus.Unspecified;
            for (int i = 0; i < 64 && status != PeerSendStatus.QueueFull; i++)
            {
                status = await peers.SendAsync(
                    new NodeId(peer), PeerMessage, new byte[] { (byte)i }, CancellationToken.None);
            }

            Assert.Equal(PeerSendStatus.QueueFull, status);
        }

        // 각 피어는 큐에 Depth 개를 붙들고, **소비자가 이미 하나를 꺼내 든 채**
        // 연결에서 멈췄으면 하나가 더 붙는다. 몇 개의 소비자가 그 상태인지는
        // 스케줄링에 달렸으므로(실측 17 = 4×4 + 1) 상·하한으로 고정한다 —
        // ⚠ 여기서 한 값을 못 박으면 통과하다가 CI 에서 깨지는 테스트가 된다.
        Assert.InRange(pool.PeakOutstanding, PeerCount * Depth, PeerCount * (Depth + 1));

        // ⭐ 그리고 이것이 규칙이 틀렸다는 증거다 — 보관 개수를 큐 깊이로 잡으면
        //   PeerCount 배 작다. 16노드 클러스터면 15배다.
        Assert.True(
            pool.PeakOutstanding > Depth,
            $"미처리 대여가 큐 깊이({Depth})를 넘지 않았다면 이 테스트가 증명한 것이 없다.");

        gate.Release();
        await peers.DisposeAsync();

        // 거절과 종료를 거쳐도 누수는 없다.
        Assert.Equal(0, pool.Outstanding);
    }

    // ── 실제 세 노드가 라우팅 결정대로 주고받는다 ────────────────────

    [Fact]
    public async Task ThreeNodes_routeByOwnership_localShortCircuits_remoteArrives()
    {
        // 라우팅·로컬 단락·피어 링크가 **함께** 동작하는지는 노드가 여럿일 때만 보인다.
        NodeHarness[] nodes = await StartNodesAsync(3);

        int localDecisions = 0;
        int remoteDeliveries = 0;

        for (int i = 0; i < 60; i++)
        {
            PartitionKey key = PartitionKey.FromValue((ulong)i);
            NodeHarness sender = nodes[i % nodes.Length];

            IClusterRouter router = sender.Resolver.Router;
            ClusterRoute route = sender.Resolver.Resolve(router, key);

            Assert.True(route.HasTarget);

            if (route.IsLocal)
            {
                // ⭐ 자기에게는 네트워크를 타지 않는다. 보내면 Loopback 으로 드러난다.
                Assert.Equal(
                    PeerSendStatus.Loopback,
                    await sender.Peers.SendAsync(
                        route.Target!.Id, PeerMessage, new byte[] { (byte)i }, CancellationToken.None));

                localDecisions++;
                continue;
            }

            Assert.Equal(
                PeerSendStatus.Sent,
                await sender.Peers.SendAsync(
                    route.Target!.Id, PeerMessage, new byte[] { (byte)i }, CancellationToken.None));

            NodeHarness owner = Array.Find(nodes, n => n.Id == route.Target!.Id)!;
            byte[] arrived = await owner.ReadAsync();
            Assert.Equal((byte)i, arrived[0]);
            remoteDeliveries++;
        }

        // 셋 다 소유자가 되므로 두 경로가 모두 밟혀야 한다 — 한쪽만 밟히면
        // 이 테스트는 단일 노드 테스트와 다를 바가 없다.
        Assert.True(localDecisions > 0, "로컬 단락 경로를 한 번도 밟지 않았다.");
        Assert.True(remoteDeliveries > 0, "원격 전달 경로를 한 번도 밟지 않았다.");
    }

    // ── ⭐ ADR-0054 가 남긴 미검증: 분할은 동작으로 어떻게 보이는가 ──

    [Fact]
    public async Task SplitBrain_isObservableAsBehaviour_notJustABooleanFlag()
    {
        // ⭐ 단위 테스트는 뷰를 조립해 IsLeaderFor 를 물었다. 여기서는 **각 노드가
        //   자기 멤버십 인스턴스를 들고** 서로를 구성원으로 보지 않는 상태를 만든다 —
        //   그것이 분할의 실제 모양이다.
        ushort[] sideA = [1, 2, 3];
        ushort[] sideB = [4, 5];

        NodeHarness a = await StartNodeAsync(new NodeId(1), sideA);
        NodeHarness b = await StartNodeAsync(new NodeId(4), sideB);

        // 1. ⚠ 게이트가 없으면 **양쪽 다** 리더를 세운다. 이것이 계약이다(ADR-0054 결정 2).
        //    ⚠ "노드 1 이 리더인가" 를 묻지 않는다 — 어느 노드가 이기는지는 해시가 정하고,
        //      우리가 고정하려는 것은 **각 무리에 리더가 몇 명인가** 다.
        Assert.Equal(1, CountLeaders(sideA, ClusterQuorum.None));
        Assert.Equal(1, CountLeaders(sideB, ClusterQuorum.None));

        // 2. 5대 기준 과반을 요구하면 소수파(2대)가 통째로 물러난다.
        ClusterQuorum quorum = ClusterQuorum.MajorityOf(5);
        Assert.Equal(1, CountLeaders(sideA, quorum));
        Assert.Equal(0, CountLeaders(sideB, quorum));

        // 3. ⭐ 그리고 분할은 전송에서도 보인다 — 서로를 구성원으로 보지 않으므로
        //    상대에게 보낼 수 없다. "리더가 둘" 이 추상적인 말이 아니라는 뜻이다.
        Assert.Equal(
            PeerSendStatus.NotAMember,
            await a.Peers.SendAsync(new NodeId(4), PeerMessage, new byte[] { 1 }, CancellationToken.None));

        Assert.Equal(
            PeerSendStatus.NotAMember,
            await b.Peers.SendAsync(new NodeId(1), PeerMessage, new byte[] { 1 }, CancellationToken.None));
    }

    // ── 롤링 배포: 한 노드를 빼도 나머지는 계속 받는다 ───────────────

    [Fact]
    public async Task DrainingOneNode_doesNotInterruptTrafficToTheOthers()
    {
        // ⚠ 무중단 배포가 실제로 주장하는 것은 "그 노드가 조용히 빠진다" 가 아니라
        //   **"나머지가 계속 일한다"** 다. 노드가 하나뿐이면 그것을 물을 수 없다.
        NodeHarness[] nodes = await StartNodesAsync(3);
        NodeHarness sender = nodes[0];
        NodeHarness staying = nodes[1];
        NodeHarness leaving = nodes[2];

        // 드레인 전: 남는 노드가 받는다.
        Assert.Equal(
            PeerSendStatus.Sent,
            await sender.Peers.SendAsync(staying.Id, PeerMessage, new byte[] { 1 }, CancellationToken.None));

        Assert.Equal(1, (await staying.ReadAsync())[0]);

        // 한 노드만 무중단 절차로 뺀다.
        // ⚠ 여기서 소요 시간을 단언하지 않는다. 이 테스트의 주제는 **남는 노드가 계속
        //   받는가** 이고, 시간 단언은 Task.Delay 와 Stopwatch 가 다른 시계라 경계에서
        //   흔들린다(반복 실행 12회 중 2회 실패로 실제로 드러났다, CLAUDE.md 9.9).
        //   전파 대기 자체의 계약은 DrainOrchestrationTests 가 **동작으로** 고정한다.
        _ = await leaving.Server.DrainAsync(
            new DrainOptions
            {
                ReadinessPropagationDelay = TimeSpan.FromMilliseconds(50),
                ConnectionDrainTimeout = TimeSpan.FromSeconds(2),
            },
            CancellationToken.None);

        // ⭐ 드레인 뒤에도 남는 노드로 가는 트래픽은 그대로다.
        for (byte i = 2; i < 8; i++)
        {
            Assert.Equal(
                PeerSendStatus.Sent,
                await sender.Peers.SendAsync(staying.Id, PeerMessage, new byte[] { i }, CancellationToken.None));

            Assert.Equal(i, (await staying.ReadAsync())[0]);
        }
    }

    // ── 배관 ─────────────────────────────────────────────────────────

    /// <summary>노드 하나에 필요한 것 전부 — 서버·클라이언트·멤버십·리졸버·피어 집합.</summary>
    private sealed record NodeHarness(
        NodeId Id,
        ChServerMServer Server,
        ClusterRouteResolver Resolver,
        ClusterPeerSet Peers,
        Channel<byte[]> Received)
    {
        public async Task<byte[]> ReadAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            return await Received.Reader.ReadAsync(timeout.Token);
        }
    }

    /// <summary>노드 번호 → 이 테스트 클래스 안에서 유일한 엔드포인트.</summary>
    private readonly Dictionary<ushort, InMemoryEndPoint> _endPoints = [];

    private InMemoryEndPoint EndPointFor(ushort id)
    {
        if (!_endPoints.TryGetValue(id, out InMemoryEndPoint? endPoint))
        {
            endPoint = new InMemoryEndPoint($"node-{id}-{Guid.NewGuid():N}");
            _endPoints[id] = endPoint;
        }

        return endPoint;
    }

    private async Task<NodeHarness[]> StartNodesAsync(int count)
    {
        ushort[] ids = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = (ushort)(i + 1);
        }

        NodeHarness[] nodes = new NodeHarness[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = await StartNodeAsync(new NodeId(ids[i]), ids);
        }

        return nodes;
    }

    /// <summary>노드 하나를 세운다. <paramref name="view"/> 는 <b>그 노드가 보는</b> 구성원이다.</summary>
    /// <remarks>
    /// <b>노드마다 뷰를 따로 준다</b> — 그래야 분할을 만들 수 있다. 모두가 같은 목록을
    /// 공유하는 배관이면 <b>분할을 표현할 방법 자체가 없다</b>.
    /// </remarks>
    private async Task<NodeHarness> StartNodeAsync(NodeId self, ushort[] view)
    {
        Channel<byte[]> received = Channel.CreateUnbounded<byte[]>();

        ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(_hub, EndPointFor((ushort)self.Value), _transportOptions))
            .UseFraming(new FixedHeaderFrameDecoder(_framing), new FixedHeaderFrameEncoder(_framing))
            .ConfigureDispatcher(d => d.MapRaw(PeerMessage, context =>
            {
                received.Writer.TryWrite(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        _owned.Add(server);
        await server.StartAsync(CancellationToken.None);

        StaticClusterMembershipOptions options = new() { SelfId = self };
        foreach (ushort id in view)
        {
            options.Nodes.Add((new NodeId(id), $"node-{id}", EndPointFor(id)));
        }

        StaticClusterMembership membership = new(options);
        _owned.Add(membership);

        ChServerMClient client = BuildClient(NewClientTransport());
        ClusterPeerSet peers = PeerSet(membership, client, new ClusterPeerOptions());

        return new NodeHarness(self, server, new ClusterRouteResolver(membership), peers, received);
    }

    /// <summary>그 무리 안에서 자기가 리더라고 답하는 노드의 수.</summary>
    /// <remarks>
    /// 서버를 세우지 않는다 — 리더 판정은 <b>뷰만 보는 계산</b>이므로 전송이 필요 없다.
    /// 그 사실 자체가 ADR-0054 의 요점이다.
    /// </remarks>
    private int CountLeaders(ushort[] side, ClusterQuorum quorum)
    {
        int leaders = 0;

        foreach (ushort self in side)
        {
            StaticClusterMembershipOptions options = new() { SelfId = new NodeId(self) };
            foreach (ushort id in side)
            {
                options.Nodes.Add((new NodeId(id), $"node-{id}", EndPointFor(id)));
            }

            StaticClusterMembership membership = new(options);
            _owned.Add(membership);

            if (new ClusterRouteResolver(membership).IsLeaderFor(LeaderRole, quorum))
            {
                leaders++;
            }
        }

        return leaders;
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

    /// <summary>자기 1번 + 피어 <paramref name="peerCount"/> 대짜리 멤버십.</summary>
    private StaticClusterMembership Membership(int peerCount)
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };
        options.Nodes.Add((new NodeId(1), "self", new InMemoryEndPoint("unused-self")));

        for (ushort id = 2; id <= peerCount + 1; id++)
        {
            options.Nodes.Add((new NodeId(id), $"peer-{id}", EndPointFor(id)));
        }

        return new StaticClusterMembership(options);
    }

    private ClusterPeerSet PeerSet(
        IClusterMembership membership,
        ChServerMClient client,
        ClusterPeerOptions options,
        ArrayPool<byte>? pool = null)
    {
        ClusterPeerSet set = new(
            membership, client, options, NullServerLogger.Instance, pool ?? ArrayPool<byte>.Shared);

        _owned.Add(set);
        return set;
    }

    /// <summary>연결 수립을 붙잡아 소비자를 <b>결정적으로</b> 멈춰 세운다.</summary>
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

    /// <summary>대여·반납을 세고 <b>최고 수위</b>를 기억한다.</summary>
    /// <remarks>
    /// <b>최고 수위가 이 파일의 요점이다.</b> 현재 미처리 수만 보면 언제 봤느냐에 따라
    /// 답이 달라지고, 알고 싶은 것은 <b>동시에 최대 몇 개를 붙들 수 있는가</b>이므로
    /// 풀 크기 결정의 입력이 되는 값은 최고 수위 쪽이다(ADR-0051).
    /// </remarks>
    private sealed class CountingArrayPool : ArrayPool<byte>
    {
        private readonly ConcurrentDictionary<byte[], byte> _live = new();

        private int _outstanding;
        private int _peak;

        public int Outstanding => Volatile.Read(ref _outstanding);

        public int PeakOutstanding => Volatile.Read(ref _peak);

        public override byte[] Rent(int minimumLength)
        {
            byte[] buffer = new byte[Math.Max(minimumLength, 1)];
            _live[buffer] = 0;

            int now = Interlocked.Increment(ref _outstanding);

            // CAS 루프 — 최고 수위는 여러 스레드가 동시에 올릴 수 있다.
            int peak = Volatile.Read(ref _peak);
            while (now > peak)
            {
                int seen = Interlocked.CompareExchange(ref _peak, now, peak);
                if (seen == peak)
                {
                    break;
                }

                peak = seen;
            }

            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_live.TryRemove(array, out _), "빌린 적 없거나 이미 반납한 배열을 반납했다.");
            Interlocked.Decrement(ref _outstanding);
        }
    }
}
