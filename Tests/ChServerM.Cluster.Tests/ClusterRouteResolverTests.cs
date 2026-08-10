using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Tests;

/// <summary>
/// 라우팅 결정과 <b>뷰-라우터 짝 유지</b> 검증 (ADR-0049).
/// </summary>
/// <remarks>
/// <para>
/// 여기서 고정하는 것 셋 — <b>자기 자신에게 네트워크를 타지 않는다</b>,
/// <b>뷰가 바뀌면 라우터가 따라 바뀐다</b>, <b>이미 받은 라우터로 결정하면 그 뷰를 유지한다</b>.
/// 셋 다 어겼을 때의 증상이 "느리다" 가 아니라 <b>틀린 노드로 간다</b> 이므로 계약으로 못 박는다.
/// </para>
/// </remarks>
public sealed class ClusterRouteResolverTests
{
    /// <summary>뷰를 갈아 끼울 수 있는 테스트용 멤버십.</summary>
    /// <remarks>
    /// 정적 목록은 바뀌지 않으므로 재생성 경로를 검증할 수 없다. 두 번째 구현이 나오기 전에
    /// <b>바뀌는 멤버십</b>의 계약을 확인해 두는 자리다 — 그것이 이 축의 실제 사용 형태다.
    /// </remarks>
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

    private static PartitionKey Key(int i) => PartitionKey.FromValue((ulong)i);

    // ── 로컬 단락 ────────────────────────────────────────────────────

    [Fact]
    public void SelfOwnedKeys_resolveToLocal_andOthersToRemote()
    {
        // ⭐ 자기 자신에게 네트워크 왕복을 하지 않는 것이 이 타입의 첫 번째 일이다.
        ClusterView view = View(1, 1, 2, 3, 4);
        MutableMembership membership = new(view.Nodes[0], view);
        ClusterRouteResolver resolver = new(membership);

        int local = 0;
        int remote = 0;

        for (int i = 0; i < 2000; i++)
        {
            ClusterRoute route = resolver.Resolve(Key(i));

            Assert.True(route.HasTarget);
            Assert.NotNull(route.Target);

            if (route.IsLocal)
            {
                // 로컬이면 대상이 반드시 자기 자신이다 — 로그가 "누가 처리했는가" 를
                // 같은 방식으로 적을 수 있어야 한다.
                Assert.Equal(membership.Self.Id, route.Target!.Id);
                local++;
            }
            else
            {
                Assert.Equal(ClusterRouteKind.Remote, route.Kind);
                Assert.NotEqual(membership.Self.Id, route.Target!.Id);
                remote++;
            }
        }

        // 4노드이므로 대략 1/4 이 로컬이다. 정확한 비율이 아니라 "둘 다 나온다" 를 본다.
        Assert.True(local > 0, "로컬 결정이 하나도 없다 — 단락 경로가 죽어 있다.");
        Assert.True(remote > 0);
        Assert.Equal(2000, local + remote);
    }

    [Fact]
    public void SingleNodeCluster_resolvesEverythingLocally()
    {
        ClusterView view = View(1, 7);
        MutableMembership membership = new(view.Nodes[0], view);
        ClusterRouteResolver resolver = new(membership);

        for (int i = 0; i < 500; i++)
        {
            Assert.True(resolver.Resolve(Key(i)).IsLocal);
        }
    }

    [Fact]
    public void EmptyView_resolvesToUnavailable()
    {
        // 모든 노드가 사라진 상태에서 요청을 어딘가에 쌓아 두면 그것이 곧 OOM 이다 —
        // 호출자가 즉시 거절할 수 있어야 한다(거부가 붕괴보다 낫다).
        MutableMembership membership = new(Node(1), new ClusterView([], generation: 1));
        ClusterRouteResolver resolver = new(membership);

        ClusterRoute route = resolver.Resolve(Key(1));

        Assert.Equal(ClusterRouteKind.Unavailable, route.Kind);
        Assert.Null(route.Target);
        Assert.False(route.HasTarget);
        Assert.False(route.IsLocal);
    }

    // ── 뷰-라우터 짝 유지 ────────────────────────────────────────────

    [Fact]
    public void Router_isCachedWhileTheViewIsUnchanged()
    {
        // 결정마다 라우터를 새로 만들면 노드 해시 계산이 조회 비용이 된다.
        ClusterView view = View(1, 1, 2, 3);
        MutableMembership membership = new(view.Nodes[0], view);
        ClusterRouteResolver resolver = new(membership);

        Assert.Same(resolver.Router, resolver.Router);
        Assert.Same(view, resolver.Router.View);
    }

    [Fact]
    public void Router_rebuildsWhenTheViewChanges()
    {
        // ⚠ 뷰는 새것인데 라우터가 옛것이면 **사라진 노드로 보내게** 된다.
        ClusterView first = View(1, 1, 2, 3);
        MutableMembership membership = new(first.Nodes[0], first);
        ClusterRouteResolver resolver = new(membership);

        IClusterRouter before = resolver.Router;
        Assert.Same(first, before.View);

        ClusterView second = View(2, 1, 2, 3, 4);
        membership.Swap(second);

        IClusterRouter after = resolver.Router;
        Assert.NotSame(before, after);
        Assert.Same(second, after.View);
    }

    [Fact]
    public void Router_neverPointsAtADepartedNode()
    {
        // 위 재생성이 실제로 무엇을 막는지 — 사라진 노드가 결정에 나오지 않는다.
        ClusterView full = View(1, 1, 2, 3, 4);
        MutableMembership membership = new(full.Nodes[0], full);
        ClusterRouteResolver resolver = new(membership);

        // 4번 노드가 소유하는 키를 하나 찾는다.
        int keyOwnedByFour = -1;
        for (int i = 0; i < 10_000 && keyOwnedByFour < 0; i++)
        {
            if (resolver.Resolve(Key(i)).Target!.Id == new NodeId(4))
            {
                keyOwnedByFour = i;
            }
        }

        Assert.True(keyOwnedByFour >= 0, "4번 노드가 소유하는 키를 찾지 못했다 — 분포가 깨졌다.");

        membership.Swap(View(2, 1, 2, 3));

        ClusterRoute route = resolver.Resolve(Key(keyOwnedByFour));
        Assert.NotEqual(new NodeId(4), route.Target!.Id);
    }

    [Fact]
    public void ResolveWithAGivenRouter_keepsThatViewEvenAfterMembershipMoves()
    {
        // ⚠⚠ 한 작업 = 한 뷰. 작업 도중에 구성이 바뀌어도 이미 받은 라우터로 내린
        // 결정들은 서로 일관돼야 한다 — 그러지 않으면 같은 요청의 두 조각이 다른
        // 구성을 보고 결정된다.
        ClusterView first = View(1, 1, 2, 3, 4);
        MutableMembership membership = new(first.Nodes[0], first);
        ClusterRouteResolver resolver = new(membership);

        IClusterRouter pinned = resolver.Router;

        List<NodeId> before = [];
        for (int i = 0; i < 200; i++)
        {
            before.Add(resolver.Resolve(pinned, Key(i)).Target!.Id);
        }

        membership.Swap(View(2, 1, 2));

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(before[i], resolver.Resolve(pinned, Key(i)).Target!.Id);
        }

        // 반면 뷰를 다시 읽는 경로는 새 구성을 본다.
        Assert.Same(membership.Current, resolver.Router.View);
    }

    [Fact]
    public async Task Router_isSafeUnderConcurrentViewChanges()
    {
        // 여러 파티션 워커가 동시에 결정을 내리는 것이 기본 사용 형태다.
        ClusterView initial = View(1, 1, 2, 3);
        MutableMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));

        Task swapper = Task.Run(
            () =>
            {
                int generation = 2;
                while (!stop.Token.IsCancellationRequested)
                {
                    membership.Swap(View(generation++, 1, 2, 3, (ushort)(generation % 5 + 10)));
                }
            },
            CancellationToken.None);

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(
                () =>
                {
                    while (!stop.Token.IsCancellationRequested)
                    {
                        IClusterRouter router = resolver.Router;

                        // 라우터가 가리키는 노드는 그 라우터의 뷰 안에 반드시 있다 —
                        // 짝이 어긋나면 여기서 걸린다.
                        for (int i = 0; i < 64; i++)
                        {
                            ClusterRoute route = resolver.Resolve(router, Key(i));
                            Assert.True(router.View.Contains(route.Target!.Id));
                        }
                    }
                },
                CancellationToken.None);
        }

        await swapper;
        await Task.WhenAll(readers);
    }

    // ── 조립과 무할당 ────────────────────────────────────────────────

    [Fact]
    public void CustomRouterFactory_isUsed()
    {
        // 라우팅 전략을 고르는 지점이다 — 두 번째 구현(일관 해싱 링)이 들어올 자리.
        ClusterView view = View(1, 1, 2);
        MutableMembership membership = new(view.Nodes[0], view);

        int built = 0;
        ClusterRouteResolver resolver = new(
            membership,
            v =>
            {
                built++;
                return new RendezvousRouter(v);
            });

        Assert.Equal(1, built);
        _ = resolver.Router;
        Assert.Equal(1, built);

        membership.Swap(View(2, 1, 2, 3));
        _ = resolver.Router;
        Assert.Equal(2, built);
    }

    [Fact]
    public void NullArguments_throw()
    {
        ClusterView view = View(1, 1);
        MutableMembership membership = new(view.Nodes[0], view);
        ClusterRouteResolver resolver = new(membership);

        Assert.Throws<ArgumentNullException>(() => new ClusterRouteResolver(null!));
        Assert.Throws<ArgumentNullException>(() => new ClusterRouteResolver(membership, null!));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, Key(1)));
    }

    [Fact]
    public void Resolve_doesNotAllocateWhenTheViewIsStable()
    {
        ClusterView view = View(1, 1, 2, 3, 4);
        MutableMembership membership = new(view.Nodes[0], view);
        ClusterRouteResolver resolver = new(membership);

        Probe(resolver);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Probe(resolver);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 뷰가 그대로면 라우터를 다시 만들지 않으므로 결정 경로에 할당이 없다.
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Self_isExposedForDiagnostics()
    {
        ClusterView view = View(1, 5, 6);
        MutableMembership membership = new(view.Nodes[0], view);
        ClusterRouteResolver resolver = new(membership);

        Assert.Same(membership.Self, resolver.Self);
    }

    private static void Probe(ClusterRouteResolver resolver)
    {
        for (int i = 0; i < 500; i++)
        {
            _ = resolver.Resolve(Key(i));
        }
    }
}
