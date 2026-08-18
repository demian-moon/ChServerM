using System;
using System.Collections.Generic;
using System.Net;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Tests;

/// <summary>
/// 랑데뷰 라우터의 <b>분포·이동량·결정성</b> 검증 (ADR-0048).
/// </summary>
/// <remarks>
/// <para>
/// <b>여기서 고정하는 것은 성능이 아니라 계약이다.</b> 라우팅을 이 축으로 만든 이유가
/// 두 가지 — <b>모든 노드가 같은 답</b>을 내고 구성이 바뀔 때 <b>움직이는 키가 최소</b>여야
/// 한다는 것 — 이고, 그 둘은 눈으로 확인할 수 없다. 수치로 못 박아야 회귀가 잡힌다.
/// </para>
/// <para>
/// <b>⭐ <see cref="ToIndex_movesHalfTheKeys_whichIsWhyThisAxisExists"/> 를 함께 둔다.</b>
/// "왜 <c>PartitionKey.ToIndex(노드 수)</c> 를 쓰면 안 되는가" 를 <b>실행 가능한 형태</b>로
/// 남긴 것이다 — 문서보다 오래 살아남고, 누군가 "간단하게 파티션 인덱스로 바꾸자" 고
/// 할 때 답이 된다.
/// </para>
/// </remarks>
public sealed class RendezvousRouterTests
{
    private const int KeyCount = 100_000;

    // 노드 번호는 1부터다 — 0 은 NodeId.None 센티넬로 예약됐다(감사 2026-08-18 C-6).
    private static ClusterView View(int nodeCount, int startAt = 1)
    {
        List<ClusterNode> nodes = new(nodeCount);
        for (int i = startAt; i < startAt + nodeCount; i++)
        {
            nodes.Add(new ClusterNode(
                new NodeId((ushort)i), $"node-{i:D2}", new DnsEndPoint($"n{i}.internal", 7000)));
        }

        return new ClusterView(nodes, generation: 1);
    }

    private static PartitionKey Key(int i) => PartitionKey.FromValue((ulong)i);

    // ── 결정성 ───────────────────────────────────────────────────────

    [Fact]
    public void SameViewAndKey_alwaysYieldsTheSameOwner()
    {
        // ⚠ 모든 노드가 같은 답을 내야 한다. 라우터 인스턴스가 달라도 결과가 같아야
        // 그 성질이 성립한다 — 프로세스가 다르면 인스턴스도 다르기 때문이다.
        RendezvousRouter a = new(View(8));
        RendezvousRouter b = new(View(8));

        for (int i = 0; i < 1000; i++)
        {
            Assert.True(a.TryGetOwner(Key(i), out ClusterNode? left));
            Assert.True(b.TryGetOwner(Key(i), out ClusterNode? right));
            Assert.Equal(left!.Id, right!.Id);
        }
    }

    [Fact]
    public void NodeOrderInConstruction_doesNotChangeOwnership()
    {
        // 뷰가 번호 순으로 정렬하므로 설정 순서가 결과에 새어 들어갈 수 없다.
        ClusterNode a = new(new NodeId(1), "alpha", new DnsEndPoint("a", 1));
        ClusterNode b = new(new NodeId(2), "beta", new DnsEndPoint("b", 1));
        ClusterNode c = new(new NodeId(3), "gamma", new DnsEndPoint("c", 1));

        RendezvousRouter forward = new(new ClusterView([a, b, c], 1));
        RendezvousRouter shuffled = new(new ClusterView([c, a, b], 1));

        for (int i = 0; i < 1000; i++)
        {
            forward.TryGetOwner(Key(i), out ClusterNode? left);
            shuffled.TryGetOwner(Key(i), out ClusterNode? right);
            Assert.Equal(left!.Id, right!.Id);
        }
    }

    [Fact]
    public void OwnerAlwaysMatchesTheFirstCandidate()
    {
        // 두 경로가 다른 답을 내면 복제본이 소유자와 어긋난다.
        RendezvousRouter router = new(View(6));
        ClusterNode?[] candidates = new ClusterNode?[3];

        for (int i = 0; i < 2000; i++)
        {
            Assert.True(router.TryGetOwner(Key(i), out ClusterNode? owner));
            Assert.Equal(3, router.GetOwners(Key(i), candidates));
            Assert.Equal(owner!.Id, candidates[0]!.Id);
        }
    }

    // ── 분포 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(16)]
    public void Distribution_isEvenWithoutAnyTuningKnob(int nodeCount)
    {
        // ⭐ 링(일관 해싱)을 쓰지 않은 이유가 이것이다. 링은 가상 노드 수를 사람이 정하고,
        // 적게 잡으면 분포가 크게 치우치는데 **아무 오류도 나지 않는다**. 랑데뷰는 설정이 없다.
        RendezvousRouter router = new(View(nodeCount));
        int[] load = new int[nodeCount];
        Dictionary<NodeId, int> index = Index(router.View);

        for (int i = 0; i < KeyCount; i++)
        {
            router.TryGetOwner(Key(i), out ClusterNode? owner);
            load[index[owner!.Id]]++;
        }

        double expected = (double)KeyCount / nodeCount;
        foreach (int count in load)
        {
            double deviation = Math.Abs(count - expected) / expected;

            // 10만 키에서 5% 는 넉넉한 상한이다. 실제로는 1% 안쪽에 들어온다 —
            // 상한을 조이면 통계적 요동으로 테스트가 흔들린다.
            Assert.True(deviation < 0.05, $"노드 부하 편차가 {deviation:P1} 다(기대 {expected:F0}, 실제 {count}).");
        }
    }

    // ── 이동량 ───────────────────────────────────────────────────────

    [Fact]
    public void AddingANode_movesOnlyItsFairShare()
    {
        // ⭐ 이 축이 존재하는 이유. 이론값은 1/N 이고, 그보다 크게 벗어나면
        // 노드 하나 추가가 클러스터 전체의 상태 이동이 된다.
        RendezvousRouter before = new(View(8));
        RendezvousRouter after = new(View(9));

        double moved = MovedFraction(before, after);
        double ideal = 1.0 / 9;

        Assert.True(moved < ideal * 1.2, $"노드 추가로 {moved:P2} 가 움직였다(이론 {ideal:P2}).");
    }

    [Fact]
    public void RemovingANode_movesOnlyItsOwnKeys()
    {
        RendezvousRouter before = new(View(8));
        RendezvousRouter after = new(View(7));

        double moved = MovedFraction(before, after);
        double ideal = 1.0 / 8;

        Assert.True(moved < ideal * 1.2, $"노드 제거로 {moved:P2} 가 움직였다(이론 {ideal:P2}).");
    }

    [Fact]
    public void RemovingANode_neverMovesKeysBetweenSurvivors()
    {
        // ⚠ 이것이 랑데뷰의 강한 성질이다. 사라진 노드의 키만 재배치되고,
        // **살아남은 노드끼리는 키를 주고받지 않는다** — 필요 없는 상태 이동이 0이다.
        ClusterView full = View(8);
        RendezvousRouter before = new(full);

        NodeId removed = full.Nodes[3].Id;
        List<ClusterNode> survivors = [];
        foreach (ClusterNode node in full.Nodes)
        {
            if (node.Id != removed)
            {
                survivors.Add(node);
            }
        }

        RendezvousRouter after = new(new ClusterView(survivors, 2));

        for (int i = 0; i < KeyCount; i++)
        {
            before.TryGetOwner(Key(i), out ClusterNode? was);
            after.TryGetOwner(Key(i), out ClusterNode? now);

            if (was!.Id != removed)
            {
                Assert.Equal(was.Id, now!.Id);
            }
        }
    }

    [Fact]
    public void ToIndex_movesHalfTheKeys_whichIsWhyThisAxisExists()
    {
        // ⭐ "간단하게 파티션 인덱스를 쓰면 되지 않나" 에 대한 실행 가능한 답이다.
        // PartitionKey.ToIndex 는 프로세스 안에서 **파티션 개수가 고정**일 때 옳다.
        //
        // ⚠ 정확히 얼마나 나쁜지는 재 봐야 안다. ToIndex 는 나머지 연산이 아니라
        // **곱셈-시프트 축소**(해시 상위 32비트를 [0, count) 로 선형 사상)라, 8 → 9 에서
        // 나머지 연산의 (N-1)/N ≈ 89% 가 아니라 **약 50%** 가 움직인다. 처음에는 89% 로
        // 적었다가 이 테스트가 정정해 줬다 — 짐작한 수치를 문서에 적으면 그것이 근거처럼
        // 굳는다.
        //
        // 그래도 결론은 같다: **상태의 절반이 노드 하나 추가로 이동한다.**
        // 랑데뷰의 11% 와 비교하면 4.5배이고, 무엇보다 랑데뷰는 살아남은 노드끼리
        // 키를 주고받지 않는다(위 테스트) — ToIndex 는 그 보장도 없다.
        int moved = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (Key(i).ToIndex(8) != Key(i).ToIndex(9))
            {
                moved++;
            }
        }

        double fraction = (double)moved / KeyCount;

        Assert.True(fraction > 0.4, $"ToIndex 이동률이 {fraction:P1} 였다. 이 테스트의 전제가 깨졌다.");
    }

    // ── 후보 순위 ────────────────────────────────────────────────────

    [Fact]
    public void Candidates_areDistinctAndRankedConsistently()
    {
        RendezvousRouter router = new(View(6));
        ClusterNode?[] top3 = new ClusterNode?[3];
        ClusterNode?[] top5 = new ClusterNode?[5];

        for (int i = 0; i < 2000; i++)
        {
            Assert.Equal(3, router.GetOwners(Key(i), top3));
            Assert.Equal(5, router.GetOwners(Key(i), top5));

            // 같은 노드가 두 번 나오면 복제본이 같은 곳에 두 벌 놓인다.
            Assert.Equal(3, new HashSet<NodeId>([top3[0]!.Id, top3[1]!.Id, top3[2]!.Id]).Count);

            // 상위 k 는 상위 k+m 의 앞부분과 같아야 한다 — 순위가 하나여야 한다.
            for (int rank = 0; rank < 3; rank++)
            {
                Assert.Equal(top3[rank]!.Id, top5[rank]!.Id);
            }
        }
    }

    [Fact]
    public void Candidates_secondRankTakesOverWhenTheOwnerLeaves()
    {
        // 소유자가 사라지면 2순위가 받는다 — 장애 조치가 순위 계산 하나로 풀린다.
        ClusterView full = View(6);
        RendezvousRouter before = new(full);
        ClusterNode?[] candidates = new ClusterNode?[2];

        Assert.Equal(2, before.GetOwners(Key(12345), candidates));
        NodeId owner = candidates[0]!.Id;
        NodeId second = candidates[1]!.Id;

        List<ClusterNode> survivors = [];
        foreach (ClusterNode node in full.Nodes)
        {
            if (node.Id != owner)
            {
                survivors.Add(node);
            }
        }

        RendezvousRouter after = new(new ClusterView(survivors, 2));
        Assert.True(after.TryGetOwner(Key(12345), out ClusterNode? now));
        Assert.Equal(second, now!.Id);
    }

    [Fact]
    public void Candidates_moreThanNodes_fillsOnlyWhatExists()
    {
        RendezvousRouter router = new(View(2));
        ClusterNode?[] destination = new ClusterNode?[5];

        Assert.Equal(2, router.GetOwners(Key(1), destination));
        Assert.NotNull(destination[0]);
        Assert.NotNull(destination[1]);
    }

    [Fact]
    public void Candidates_emptyDestination_fillsNothing()
    {
        RendezvousRouter router = new(View(3));

        Assert.Equal(0, router.GetOwners(Key(1), []));
    }

    // ── 경계 ─────────────────────────────────────────────────────────

    [Fact]
    public void SingleNode_ownsEverything()
    {
        RendezvousRouter router = new(View(1));

        for (int i = 0; i < 1000; i++)
        {
            Assert.True(router.TryGetOwner(Key(i), out ClusterNode? owner));
            Assert.Equal("node-01", owner!.Name);
        }
    }

    [Fact]
    public void EmptyView_returnsFalseInsteadOfThrowing()
    {
        // 모든 노드가 사라지는 것은 운영 중 실제로 일어난다. 핫패스의 예외로 만들면
        // 장애가 예외 폭풍이 된다.
        RendezvousRouter router = new(new ClusterView([], generation: 1));

        Assert.False(router.TryGetOwner(Key(1), out ClusterNode? owner));
        Assert.Null(owner);
        Assert.Equal(0, router.GetOwners(Key(1), new ClusterNode?[3]));
    }

    [Fact]
    public void NullView_throws() => Assert.Throws<ArgumentNullException>(() => new RendezvousRouter(null!));

    [Fact]
    public void ContractIsUsableThroughTheInterface()
    {
        // 축은 교체 가능해야 한다 — 소비자가 구체 타입을 알 필요가 없다는 것이
        // 두 번째 구현(일관 해싱 링)이 들어올 자리를 실제로 열어 둔다.
        //
        // CA1859(구체 타입이 빠르다)는 여기서 맞지 않는다. 이 테스트의 대상이 바로
        // "인터페이스로 쓸 수 있는가" 이므로 구체 타입으로 바꾸면 검증이 사라진다.
#pragma warning disable CA1859
        IClusterRouter router = new RendezvousRouter(View(4));
#pragma warning restore CA1859

        // ⚠ ClusterNode 는 참조 타입이라 stackalloc 이 불가능하다. 호출자는 재사용하는
        // 배열을 넘긴다 — 무할당은 구현이 아니라 그 재사용에서 온다.
        ClusterNode?[] candidates = new ClusterNode?[2];

        Assert.True(router.TryGetOwner(Key(7), out ClusterNode? owner));
        Assert.Equal(2, router.GetOwners(Key(7), candidates));
        Assert.Equal(owner!.Id, candidates[0]!.Id);
        Assert.Equal(4, router.View.Count);
    }

    [Fact]
    public void View_isTheOneItWasBuiltFrom()
    {
        // 라우터가 뷰에 묶인다는 것이 "한 작업은 뷰를 한 번만 읽는다" 의 타입 표현이다.
        ClusterView view = View(3);
        RendezvousRouter router = new(view);

        Assert.Same(view, router.View);
    }

    // ── 무할당 ───────────────────────────────────────────────────────

    [Fact]
    public void Routing_doesNotAllocate()
    {
        RendezvousRouter router = new(View(16));
        ClusterNode?[] candidates = new ClusterNode?[3];

        // 워밍업 — JIT 을 측정에서 뺀다.
        Probe(router, candidates);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Probe(router, candidates);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 메시지마다 불릴 수 있는 경로다. 후보 채우기도 호출자의 버퍼에 쓴다.
        Assert.Equal(0, allocated);
    }

    private static void Probe(RendezvousRouter router, ClusterNode?[] candidates)
    {
        for (int i = 0; i < 1000; i++)
        {
            router.TryGetOwner(Key(i), out _);
            router.GetOwners(Key(i), candidates);
        }
    }

    private static double MovedFraction(RendezvousRouter before, RendezvousRouter after)
    {
        int moved = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            before.TryGetOwner(Key(i), out ClusterNode? was);
            after.TryGetOwner(Key(i), out ClusterNode? now);

            if (was!.Id != now!.Id)
            {
                moved++;
            }
        }

        return (double)moved / KeyCount;
    }

    private static Dictionary<NodeId, int> Index(ClusterView view)
    {
        Dictionary<NodeId, int> index = new(view.Count);
        for (int i = 0; i < view.Count; i++)
        {
            index[view.Nodes[i].Id] = i;
        }

        return index;
    }
}
