using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Tests;

/// <summary>
/// 뷰-유도 리더와 정족수 게이트 검증 (ADR-0053).
/// </summary>
/// <remarks>
/// <para>
/// <b>여기서 고정하는 계약.</b> 같은 뷰를 보는 노드들 사이에 리더는 <b>정확히 하나</b>다 ·
/// 역할이 다르면 리더가 흩어진다 · 리더가 빠지면 <b>자동으로</b> 다음이 된다 ·
/// <b>정족수를 못 채우면 아무도 리더가 아니다</b> · 소유자 계산보다 정족수를 <b>먼저</b> 본다.
/// </para>
/// <para>
/// <b>⚠ 여기서 검증하지 <i>않는</i> 것 — 상호 배제.</b> 뷰가 갈리면 각 무리가 자기 리더를
/// 뽑고, 정족수를 켜도 <b>옛 리더가 자기가 밀려난 것을 아직 모르는 구간</b>은 남는다.
/// 그것은 결함이 아니라 <b>이 축이 주지 않는 것</b>이며, 아래
/// <see cref="SplitBrain_bothSidesElectLeaders_whenQuorumIsNotUsed"/> 가 그 사실을
/// <b>계약으로</b> 고정한다 — 나중에 누군가 "리더니까 하나겠지" 로 읽는 것을 막는다.
/// </para>
/// </remarks>
public sealed class ClusterLeadershipTests
{
    private sealed class FixedMembership(ClusterNode self, ClusterView view) : IClusterMembership
    {
        public ClusterNode Self { get; } = self;

        public ClusterView Current { get; } = view;

        public ValueTask<ClusterView> WaitForChangeAsync(
            int knownGeneration,
            CancellationToken cancellationToken) =>
            new(Task.FromCanceled<ClusterView>(new CancellationToken(canceled: true)));

        public ValueTask DisposeAsync() => default;
    }

    private static readonly PartitionKey CompactionRole = PartitionKey.FromValue(0xC0FFEE);
    private static readonly PartitionKey ReportingRole = PartitionKey.FromValue(0xBEEF);

    private static ClusterNode Node(ushort id) =>
        new(new NodeId(id), $"node-{id:D2}", new DnsEndPoint($"n{id}.internal", 7000));

    private static ClusterView View(int generation, params ushort[] ids)
    {
        List<ClusterNode> nodes = new(ids.Length);
        foreach (ushort id in ids)
        {
            nodes.Add(Node(id));
        }

        return new ClusterView(nodes, generation);
    }

    /// <summary>그 뷰에서 자기가 리더라고 답하는 노드의 수를 센다.</summary>
    private static int CountLeaders(ClusterView view, PartitionKey role, ClusterQuorum quorum)
    {
        int leaders = 0;
        foreach (ClusterNode self in view.Nodes)
        {
            FixedMembership membership = new(self, view);
            ClusterRouteResolver resolver = new(membership);

            if (resolver.IsLeaderFor(role, quorum))
            {
                leaders++;
            }
        }

        return leaders;
    }

    // ── 선출하지 않는다. 계산한다 ────────────────────────────────────

    [Fact]
    public void SameView_electsExactlyOneLeader_withoutAnyMessages()
    {
        // ⭐ 이것이 이 설계의 전부다 — 합의도 메시지도 없이 모든 노드가 같은 답을 낸다.
        ClusterView view = View(1, 1, 2, 3, 4, 5);

        Assert.Equal(1, CountLeaders(view, CompactionRole, ClusterQuorum.None));
        Assert.Equal(1, CountLeaders(view, CompactionRole, ClusterQuorum.MajorityOf(5)));
    }

    [Fact]
    public void DifferentRoles_spreadLeadershipAcrossNodes()
    {
        // 역할마다 다른 키를 주면 리더가 흩어진다 — 한 노드에 전부 몰리면 그 노드가
        // 병목이 되고, 그것을 피할 수단이 "키를 다르게 준다" 하나뿐이라는 것을 고정한다.
        ClusterView view = View(1, 1, 2, 3, 4, 5, 6, 7, 8);

        ClusterNode? compactionLeader = null;
        ClusterNode? reportingLeader = null;

        foreach (ClusterNode self in view.Nodes)
        {
            ClusterRouteResolver resolver = new(new FixedMembership(self, view));

            if (resolver.IsLeaderFor(CompactionRole, ClusterQuorum.None))
            {
                compactionLeader = self;
            }

            if (resolver.IsLeaderFor(ReportingRole, ClusterQuorum.None))
            {
                reportingLeader = self;
            }
        }

        Assert.NotNull(compactionLeader);
        Assert.NotNull(reportingLeader);
        Assert.NotEqual(compactionLeader!.Id, reportingLeader!.Id);
    }

    [Fact]
    public void LeaderLeaves_successionIsAutomatic_becauseItIsJustAComputation()
    {
        // 리더가 빠져도 아무 절차가 없다. 남은 노드들이 같은 계산을 다시 할 뿐이다.
        ClusterView before = View(1, 1, 2, 3, 4, 5);

        ClusterNode leader = Array.Find(
            [.. before.Nodes],
            node => new ClusterRouteResolver(new FixedMembership(node, before))
                .IsLeaderFor(CompactionRole, ClusterQuorum.None))!;

        Assert.NotNull(leader);

        List<ushort> remaining = [];
        foreach (ClusterNode node in before.Nodes)
        {
            if (node.Id != leader.Id)
            {
                remaining.Add((ushort)node.Id.Value);
            }
        }

        ClusterView after = View(2, [.. remaining]);

        // 여전히 정확히 한 명이고, 그 사람은 떠난 리더가 아니다.
        Assert.Equal(1, CountLeaders(after, CompactionRole, ClusterQuorum.None));
        Assert.False(after.Contains(leader.Id));
    }

    // ── 정족수 게이트 ────────────────────────────────────────────────

    [Fact]
    public void BelowQuorum_nobodyIsLeader()
    {
        // 5대 클러스터가 2대만 보이는 무리로 갈렸다. 그쪽은 통째로 물러난다.
        ClusterView minority = View(9, 1, 2);

        Assert.Equal(0, CountLeaders(minority, CompactionRole, ClusterQuorum.MajorityOf(5)));

        // 게이트를 끄면 그 무리도 리더를 세운다 — None 이 "안전한 기본" 이 아님을 고정한다.
        Assert.Equal(1, CountLeaders(minority, CompactionRole, ClusterQuorum.None));
    }

    [Fact]
    public void SplitBrain_onlyTheMajoritySideKeepsALeader()
    {
        // ⭐ 정족수가 실제로 하는 일 — 분할을 **감지**하는 것이 아니라
        //   소수파가 **스스로 물러나게** 하는 것이다.
        ClusterQuorum quorum = ClusterQuorum.MajorityOf(5);

        ClusterView majoritySide = View(10, 1, 2, 3);
        ClusterView minoritySide = View(10, 4, 5);

        Assert.Equal(1, CountLeaders(majoritySide, CompactionRole, quorum));
        Assert.Equal(0, CountLeaders(minoritySide, CompactionRole, quorum));
    }

    [Fact]
    public void SplitBrain_bothSidesElectLeaders_whenQuorumIsNotUsed()
    {
        // ⚠ 이것은 결함이 아니라 **계약**이다. None 을 고르면 분할 시 리더가 둘이다.
        //   그 사실을 테스트로 고정해 두지 않으면 언젠가 "리더니까 하나겠지" 로 읽힌다.
        ClusterView sideA = View(10, 1, 2, 3);
        ClusterView sideB = View(10, 4, 5);

        Assert.Equal(1, CountLeaders(sideA, CompactionRole, ClusterQuorum.None));
        Assert.Equal(1, CountLeaders(sideB, CompactionRole, ClusterQuorum.None));
    }

    [Fact]
    public void EvenSizedCluster_splitInHalf_leavesNobody()
    {
        // ⚠ 짝수는 손해다 — 3:3 이면 **양쪽 다** 물러나 아무 일도 일어나지 않는다.
        //   정족수를 쓸 거면 홀수로 배치하라는 문서의 근거가 이것이다.
        ClusterQuorum quorum = ClusterQuorum.MajorityOf(6);

        Assert.Equal(0, CountLeaders(View(11, 1, 2, 3), CompactionRole, quorum));
        Assert.Equal(0, CountLeaders(View(11, 4, 5, 6), CompactionRole, quorum));
    }

    [Fact]
    public void QuorumIsCheckedBeforeOwnership_soMinorityNeverActsBriefly()
    {
        // 순서가 반대면 소수파도 자기 무리의 소유자를 계산해 잠시 리더로 행동한 뒤
        // 물러난다. 빈 뷰에서도 예외 없이 false 여야 그 순서가 지켜진 것이다.
        ClusterView empty = new([], generation: 1);
        FixedMembership membership = new(Node(1), empty);
        ClusterRouteResolver resolver = new(membership);

        Assert.False(resolver.IsLeaderFor(CompactionRole, ClusterQuorum.MajorityOf(3)));
    }

    // ── 정족수 값 자체 ───────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    [InlineData(7, 4)]
    public void MajorityOf_isHalfPlusOne(int expected, int required)
    {
        Assert.Equal(required, ClusterQuorum.MajorityOf(expected).RequiredNodes);
        Assert.True(ClusterQuorum.MajorityOf(expected).IsEnabled);
    }

    [Fact]
    public void None_isAnExplicitChoice_notADefault()
    {
        Assert.False(ClusterQuorum.None.IsEnabled);
        Assert.Equal(0, ClusterQuorum.None.RequiredNodes);
        Assert.True(ClusterQuorum.None.IsSatisfiedBy(new ClusterView([], generation: 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MajorityOf_rejectsNonPositiveExpectedSize(int expected) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterQuorum.MajorityOf(expected));
}
