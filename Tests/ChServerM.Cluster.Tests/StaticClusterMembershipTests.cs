using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Tests;

/// <summary>
/// 클러스터 축의 참조 구현 검증 (ADR-0047, ADR-0048).
/// </summary>
/// <remarks>
/// <para>
/// <b>고정하는 것은 계약이다.</b> 정적 목록 자체는 단순하지만, 이 테스트가 지키는 것은
/// <see cref="IClusterMembership"/> 의 규약 — <b>순서 결정성</b>, <b>세대 인자가 경합을
/// 닫는다</b>, <b>바뀌지 않으면 깨우지 않는다</b>, <b>노드 번호 유일성</b> — 이며,
/// 두 번째 구현(Consul·K8s)이 나올 때 그대로 적용돼야 하는 것들이다.
/// </para>
/// </remarks>
public sealed class StaticClusterMembershipTests
{
    private static StaticClusterMembershipOptions ThreeNodes(ushort self = 2)
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(self) };

        // ⚠ 일부러 번호 순이 아닌 순서로 넣는다 — 뷰가 정렬을 강제하는지 보기 위해서다.
        options.Nodes.Add((new NodeId(3), "gamma", new DnsEndPoint("gamma.internal", 7000)));
        options.Nodes.Add((new NodeId(1), "alpha", new DnsEndPoint("alpha.internal", 7000)));
        options.Nodes.Add((new NodeId(2), "beta", new DnsEndPoint("beta.internal", 7000)));

        return options;
    }

    // ── 뷰의 계약 ────────────────────────────────────────────────────

    [Fact]
    public async Task View_isSortedByNodeNumber_regardlessOfConfigurationOrder()
    {
        // ⭐ 발견 순서에 의존하면 노드마다 다른 순서를 보게 되고, 순서에 기대는 라우팅이
        // 노드마다 다른 답을 낸다 — 모든 노드가 자기만 옳다고 믿는 형태의 장애다.
        await using StaticClusterMembership membership = new(ThreeNodes());

        Assert.Equal(
            [(ushort)1, (ushort)2, (ushort)3],
            membership.Current.Nodes.Select(static n => n.Id.Value));
    }

    [Fact]
    public async Task Self_isTheConfiguredNode()
    {
        await using StaticClusterMembership membership = new(ThreeNodes(self: 3));

        Assert.Equal(new NodeId(3), membership.Self.Id);
        Assert.Equal("gamma", membership.Self.Name);
        Assert.Equal(new DnsEndPoint("gamma.internal", 7000), membership.Self.EndPoint);

        // 자기 자신은 구성원이기도 하다 — 같은 인스턴스여야 두 경로가 갈라지지 않는다.
        Assert.True(membership.Current.TryGetNode(new NodeId(3), out ClusterNode? fromView));
        Assert.Same(membership.Self, fromView);
    }

    [Fact]
    public async Task Lookup_findsMembersAndRejectsStrangers()
    {
        await using StaticClusterMembership membership = new(ThreeNodes());
        ClusterView view = membership.Current;

        Assert.Equal(3, view.Count);
        Assert.True(view.Contains(new NodeId(1)));
        Assert.False(view.Contains(new NodeId(9)));
        Assert.False(view.TryGetNode(new NodeId(9), out _));
    }

    [Fact]
    public async Task Current_returnsTheSameSnapshot_soRoutingCannotTear()
    {
        // 정적 목록은 바뀌지 않으므로 같은 인스턴스여야 한다. 매번 새로 만들면
        // "한 작업은 한 번만 읽는다" 규약이 없어도 되는 것처럼 보이게 만든다.
        await using StaticClusterMembership membership = new(ThreeNodes());

        Assert.Same(membership.Current, membership.Current);
        Assert.Equal(1, membership.Current.Generation);
    }

    // ── 변화 대기의 계약 ─────────────────────────────────────────────

    [Fact]
    public async Task WaitForChange_withStaleGeneration_returnsImmediately()
    {
        // ⚠ 세대 인자가 경합을 닫는다. "바뀌면 알려 줘" 만으로는 확인 직후·대기 직전에
        // 일어난 변화를 영원히 놓친다.
        await using StaticClusterMembership membership = new(ThreeNodes());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ClusterView view = await membership.WaitForChangeAsync(knownGeneration: 0, cts.Token);

        Assert.Same(membership.Current, view);
    }

    [Fact]
    public async Task WaitForChange_withCurrentGeneration_doesNotComplete()
    {
        // 바뀌지 않을 것을 "바뀌었다" 고 깨우면 소비자가 헛돈다.
        await using StaticClusterMembership membership = new(ThreeNodes());

        using CancellationTokenSource cts = new();
        Task<ClusterView> task = membership.WaitForChangeAsync(knownGeneration: 1, cts.Token).AsTask();

        Task finished = await Task.WhenAny(task, Task.Delay(200, CancellationToken.None));
        Assert.NotSame(task, finished);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task WaitForChange_alreadyCanceledToken_throwsWithoutWaiting()
    {
        await using StaticClusterMembership membership = new(ThreeNodes());

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await membership.WaitForChangeAsync(knownGeneration: 1, cts.Token));
    }

    [Fact]
    public async Task WaitForChange_manyWaitersCanceled_doNotLeakRegistrations()
    {
        // ⚠ 취소 등록을 풀지 않으면 기다렸다 그만두기를 반복하는 소비자가 곧 누수가 된다.
        await using StaticClusterMembership membership = new(ThreeNodes());

        for (int i = 0; i < 500; i++)
        {
            using CancellationTokenSource cts = new();
            Task<ClusterView> waiting = membership.WaitForChangeAsync(1, cts.Token).AsTask();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        }
    }

    [Fact]
    public async Task WaitForChange_supportsConcurrentWaiters()
    {
        // 라우터·리밸런서·진단이 각자 기다릴 수 있어야 한다.
        await using StaticClusterMembership membership = new(ThreeNodes());

        using CancellationTokenSource cts = new();
        Task<ClusterView>[] waiters =
        [
            membership.WaitForChangeAsync(1, cts.Token).AsTask(),
            membership.WaitForChangeAsync(1, cts.Token).AsTask(),
            membership.WaitForChangeAsync(1, cts.Token).AsTask(),
        ];

        await cts.CancelAsync();

        foreach (Task<ClusterView> waiter in waiters)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        }
    }

    // ── 조립 시점 검증 ───────────────────────────────────────────────

    [Fact]
    public void SelfMissingFromNodes_failsAtAssembly()
    {
        // ⭐ 가장 진단이 어려운 구성 실수다 — 이 노드만 "자기에게는 아무것도 오지 않는다" 고
        // 믿는데 다른 노드들은 자기에게 보낸다.
        StaticClusterMembershipOptions options = ThreeNodes(self: 9);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => new StaticClusterMembership(options));

        Assert.Contains("9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoNodes_failsAtAssembly()
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };

        Assert.Throws<InvalidOperationException>(() => new StaticClusterMembership(options));
    }

    [Fact]
    public void DuplicateNodeNumber_failsAtAssembly()
    {
        // ⭐ 지금까지 아무 데서도 확인하지 않던 것이다. 두 노드가 같은 번호를 쓰면
        // ObjectId 가 조용히 충돌하고, 그 충돌은 한참 뒤 데이터에서 발견된다.
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };
        options.Nodes.Add((new NodeId(1), "alpha", new DnsEndPoint("a", 1)));
        options.Nodes.Add((new NodeId(1), "beta", new DnsEndPoint("b", 2)));

        ArgumentException error =
            Assert.Throws<ArgumentException>(() => new StaticClusterMembership(options));

        Assert.Contains("ObjectId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateNodeName_failsAtAssembly()
    {
        // 이름이 겹치면 로그에서 두 노드를 구분할 수 없다.
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };
        options.Nodes.Add((new NodeId(1), "alpha", new DnsEndPoint("a", 1)));
        options.Nodes.Add((new NodeId(2), "alpha", new DnsEndPoint("b", 2)));

        ArgumentException error =
            Assert.Throws<ArgumentException>(() => new StaticClusterMembership(options));

        Assert.Contains("이름이 중복", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyNodeName_failsAtAssembly()
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };
        options.Nodes.Add((new NodeId(1), "  ", new DnsEndPoint("a", 1)));

        Assert.Throws<InvalidOperationException>(() => new StaticClusterMembership(options));
    }

    [Fact]
    public void NullEndPoint_failsAtAssembly()
    {
        StaticClusterMembershipOptions options = new() { SelfId = new NodeId(1) };
        options.Nodes.Add((new NodeId(1), "alpha", null!));

        Assert.Throws<InvalidOperationException>(() => new StaticClusterMembership(options));
    }

    [Fact]
    public void NullOptions_throws() =>
        Assert.Throws<ArgumentNullException>(() => new StaticClusterMembership(null!));

    // ── 값 타입의 계약 ───────────────────────────────────────────────

    [Fact]
    public void ClusterView_rejectsDuplicatesAndBadGeneration()
    {
        ClusterNode a = new(new NodeId(1), "alpha", new DnsEndPoint("a", 1));
        ClusterNode sameNumber = new(new NodeId(1), "beta", new DnsEndPoint("b", 2));
        ClusterNode sameName = new(new NodeId(2), "alpha", new DnsEndPoint("b", 2));

        Assert.Throws<ArgumentException>(() => new ClusterView([a, sameNumber], generation: 1));
        Assert.Throws<ArgumentException>(() => new ClusterView([a, sameName], generation: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClusterView([a], generation: 0));
    }

    [Fact]
    public void ClusterNode_rejectsEmptyNameAndNullEndPoint()
    {
        Assert.Throws<ArgumentException>(() => new ClusterNode(new NodeId(1), " ", new DnsEndPoint("a", 1)));
        Assert.Throws<ArgumentNullException>(() => new ClusterNode(new NodeId(1), "alpha", null!));
    }
}
