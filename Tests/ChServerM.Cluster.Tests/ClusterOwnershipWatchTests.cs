using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Tests;

/// <summary>
/// 소유권 재검토 신호 검증 — <b>리밸런싱에서 프레임워크가 주는 것의 전부</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>여기서 고정하는 계약.</b> 첫 항목은 지금 뷰다 · 바뀌면 새 라우터가 나온다 ·
/// <b>라우터의 뷰가 실제로 그 새 뷰다</b>(짝이 어긋나지 않는다) ·
/// <b>밀린 세대는 합쳐진다</b> · 취소하면 끝난다 ·
/// <b>소유권 판정이 실제로 뒤집힌다</b>(이것이 "리밸런싱" 의 관측 가능한 의미다).
/// </para>
/// <para>
/// <b>⚠ 이 축은 상태를 옮기지 않는다.</b> 프레임워크가 가진 저장 축은 공유(Redis·Garnet·
/// PostgreSQL)이거나 다중 노드에서 못 쓴다고 이미 문서화된 것(인메모리)이므로 옮길 것이
/// 없다. 없는 것은 <b>소유권이 바뀌었다는 신호</b>였고, 그것만 검증한다.
/// 옛 소유자의 늦은 쓰기는 저장소의 단일 키 CAS 가 막는다(CONSISTENCY 5절) —
/// <b>여기서 그것까지 검증하지 않는다.</b>
/// </para>
/// </remarks>
public sealed class ClusterOwnershipWatchTests
{
    /// <summary>뷰를 갈아 끼우고 <b>실제로 기다리는</b> 테스트용 멤버십.</summary>
    /// <remarks>
    /// <b><see cref="ClusterRouteResolverTests"/> 의 것과 달리 즉시 완료하지 않는다.</b>
    /// 즉시 완료하는 가짜를 쓰면 감시 루프가 빈 회전으로 돌아 <b>테스트가 통과해도 아무것도
    /// 증명하지 못한다</b> — 기다림이 이 계약의 절반이다.
    /// </remarks>
    private sealed class WaitingMembership(ClusterNode self, ClusterView initial) : IClusterMembership
    {
        private readonly Lock _gate = new();
        private ClusterView _view = initial;
        private TaskCompletionSource<ClusterView> _changed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClusterNode Self { get; } = self;

        public ClusterView Current => Volatile.Read(ref _view);

        /// <summary>뷰를 바꾸고 기다리는 쪽을 깨운다.</summary>
        public void Swap(ClusterView view)
        {
            TaskCompletionSource<ClusterView> waiters;
            lock (_gate)
            {
                Volatile.Write(ref _view, view);
                waiters = _changed;
                _changed = new TaskCompletionSource<ClusterView>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            waiters.TrySetResult(view);
        }

        public ValueTask<ClusterView> WaitForChangeAsync(
            int knownGeneration,
            CancellationToken cancellationToken)
        {
            Task<ClusterView> pending;
            lock (_gate)
            {
                // ⚠ 세대를 먼저 본다 — "확인 직후·대기 직전" 창을 닫는 것이 이 인자의 목적이다.
                if (knownGeneration < _view.Generation)
                {
                    return new ValueTask<ClusterView>(_view);
                }

                pending = _changed.Task;
            }

            return new ValueTask<ClusterView>(pending.WaitAsync(cancellationToken));
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>깨우는 신호에 <b>낡은 뷰</b>를 실어 보내는 멤버십.</summary>
    /// <remarks>
    /// <para>
    /// <b>이 가짜가 없으면 "밀린 세대는 합쳐진다" 를 증명할 수 없다.</b> 흔한 구현은
    /// <see cref="WaitForChangeAsync"/> 가 <see cref="Current"/> 를 그대로 돌려주므로,
    /// 감시 루프가 <b>반환값을 쓰든 <c>Current</c> 를 다시 읽든 결과가 같다</b> —
    /// 실제로 고의 회귀에서 두 경로 모두 초록이었다.
    /// </para>
    /// <para>
    /// 여기서는 <b>첫 변화에서만</b> 신호를 완료시킨다. 그 뒤의 변화는
    /// <see cref="Current"/> 만 앞서가므로 <b>신호에 실린 뷰가 낡는다</b> —
    /// 큐로 알림을 나르는 제공자에서 실제로 생기는 모양이고, 그때 반환값을 그대로 믿으면
    /// <b>앱이 이미 틀린 뷰로 소유권을 재검토</b>한다.
    /// </para>
    /// </remarks>
    private sealed class StaleSignalMembership(ClusterNode self, ClusterView initial) : IClusterMembership
    {
        private readonly TaskCompletionSource<ClusterView> _firstChange =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ClusterView _view = initial;

        public ClusterNode Self { get; } = self;

        public ClusterView Current => Volatile.Read(ref _view);

        public void Swap(ClusterView view)
        {
            Volatile.Write(ref _view, view);

            // 첫 변화만 신호로 나간다. 이후 변화는 Current 에만 반영돼 신호가 낡는다.
            _firstChange.TrySetResult(view);
        }

        public ValueTask<ClusterView> WaitForChangeAsync(
            int knownGeneration,
            CancellationToken cancellationToken) =>
            new(_firstChange.Task.WaitAsync(cancellationToken));

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

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(10));

    // ── 신호의 모양 ──────────────────────────────────────────────────

    [Fact]
    public async Task FirstItem_isTheCurrentView_soStartupAndRebalanceShareOneLoop()
    {
        ClusterView initial = View(7, 1, 2, 3);
        await using WaitingMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource timeout = Timeout();

        await foreach (IClusterRouter router in resolver.WatchAsync(timeout.Token))
        {
            // 첫 항목은 "변화" 가 아니라 지금이다. 그래야 초기 배치를 루프 밖에 따로
            // 두지 않아도 되고, 두 벌로 갈라지지 않는다.
            Assert.Equal(7, router.View.Generation);
            Assert.Same(initial, router.View);
            break;
        }
    }

    [Fact]
    public async Task ViewChange_yieldsRouterBoundToTheNewView()
    {
        ClusterView initial = View(1, 1, 2, 3);
        await using WaitingMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource timeout = Timeout();
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(timeout.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(1, watch.Current.View.Generation);

            ClusterView grown = View(2, 1, 2, 3, 4);
            membership.Swap(grown);

            Assert.True(await watch.MoveNextAsync());

            // ⭐ 뷰만 새것이고 라우터가 옛것이면 사라진 노드로 보낸다. 짝을 확인한다.
            Assert.Same(grown, watch.Current.View);
            Assert.Equal(4, watch.Current.View.Count);
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }

    [Fact]
    public async Task MissedGenerations_areCoalescedToTheNewest()
    {
        // ⚠ 뷰는 이벤트가 아니라 상태다. 밀린 것을 다 흘려보내면 소비자는 **이미 틀린
        //   답**으로 재검토하게 되고, 쌓아 두면 무제한 큐가 된다(9.6).
        //
        // ⚠ 이 테스트는 **관측 가능한 계약**(밀린 세대는 안 나온다)을 고정할 뿐,
        //   그것을 만드는 장치를 홀로 증명하지 못한다 — 여기 쓰는 멤버십은 신호에
        //   Current 를 실어 보내므로 감시 루프가 반환값을 믿어도 답이 같다(고의 회귀로
        //   확인했다). 장치의 증명은 StaleWakeupSignal 테스트가 한다.
        ClusterView initial = View(1, 1, 2);
        await using WaitingMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource timeout = Timeout();
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(timeout.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(1, watch.Current.View.Generation);

            // 소비하지 않는 동안 세 번 바뀐다.
            membership.Swap(View(2, 1, 2, 3));
            membership.Swap(View(3, 1, 2, 3, 4));
            ClusterView newest = View(4, 1, 2, 3, 4, 5);
            membership.Swap(newest);

            Assert.True(await watch.MoveNextAsync());

            // 2·3 은 나오지 않는다. 가장 새것 하나다.
            Assert.Same(newest, watch.Current.View);
            Assert.Equal(4, watch.Current.View.Generation);
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }

    [Fact]
    public async Task StaleWakeupSignal_isIgnored_theNewestViewIsYielded()
    {
        // ⭐ 위의 합치기 테스트는 **이 장치를 홀로 증명하지 못한다** — 흔한 멤버십은
        //   신호에 Current 를 실어 보내므로 반환값을 믿어도 답이 같다(고의 회귀로 확인).
        //   신호가 낡을 수 있는 제공자에서만 차이가 드러나고, 그것이 이 테스트다.
        ClusterView initial = View(1, 1, 2);
        await using StaleSignalMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource timeout = Timeout();
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(timeout.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(1, watch.Current.View.Generation);

            membership.Swap(View(2, 1, 2, 3));       // 이것만 신호로 나간다
            ClusterView newest = View(3, 1, 2, 3, 4); // 신호 없이 Current 만 앞서간다
            membership.Swap(newest);

            Assert.True(await watch.MoveNextAsync());

            // 신호에 실린 것은 2세대다. 그것을 그대로 쓰면 앱이 낡은 뷰로 재검토한다.
            Assert.Same(newest, watch.Current.View);
            Assert.Equal(3, watch.Current.View.Generation);
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnchangingProvider_yieldsOnceThenWaits()
    {
        // 정적 목록은 바뀌지 않는다. "바뀌지 않을 것을 바뀌었다고 깨우면" 소비자가 헛돈다
        // (ADR-0047). 첫 항목 뒤로는 아무것도 나오지 않아야 한다.
        ClusterView initial = View(1, 1, 2, 3);
        await using WaitingMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource shortWait = new(TimeSpan.FromMilliseconds(300));
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(shortWait.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await watch.MoveNextAsync());
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancellation_endsTheWatch()
    {
        ClusterView initial = View(1, 1, 2, 3);
        await using WaitingMembership membership = new(initial.Nodes[0], initial);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource cancel = new();
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(cancel.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await watch.MoveNextAsync());
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }

    // ── 이것이 "리밸런싱" 의 관측 가능한 의미다 ──────────────────────

    [Fact]
    public async Task NodeJoins_someKeysStopBeingLocal_andTheWatchIsHowYouLearnIt()
    {
        // ⭐ 이 테스트가 이 축의 존재 이유다. 노드가 늘면 **내 것이던 키의 일부가 남의 것이
        //   된다**. 신호가 없으면 앱은 그 사실을 모른 채 남의 키를 계속 처리한다.
        ClusterView before = View(1, 1, 2, 3);
        await using WaitingMembership membership = new(before.Nodes[0], before);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource timeout = Timeout();
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(timeout.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());

            // 앱이 "들고 있는" 키 — 프레임워크는 이 집합을 모른다. 앱만 안다.
            List<PartitionKey> held = [];
            for (int i = 0; i < 3000; i++)
            {
                PartitionKey key = Key(i);
                if (resolver.Resolve(watch.Current, key).IsLocal)
                {
                    held.Add(key);
                }
            }

            Assert.NotEmpty(held);

            membership.Swap(View(2, 1, 2, 3, 4, 5, 6));
            Assert.True(await watch.MoveNextAsync());

            int lost = 0;
            foreach (PartitionKey key in held)
            {
                if (!resolver.Resolve(watch.Current, key).IsLocal)
                {
                    lost++;
                }
            }

            // 노드가 3 → 6 이면 잃는 것이 있어야 하고, 전부를 잃어서도 안 된다.
            // ⭐ 랑데뷰의 요점이 바로 이것이다 — 재배치가 **일부**로 끝난다(ADR-0048).
            Assert.True(lost > 0, "노드가 늘었는데 잃은 키가 없다면 라우팅이 뷰를 안 보고 있다.");
            Assert.True(lost < held.Count, "전부를 잃었다면 랑데뷰가 아니라 나머지 연산처럼 굴고 있다.");
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }

    [Fact]
    public async Task NodeLeaves_keysItOwnedBecomeLocal_withoutMovingOtherKeys()
    {
        // 떠난 노드의 키만 재배치되고 **살아남은 노드끼리는 키를 주고받지 않는다** —
        // 랑데뷰가 링(일관 해싱) 대신 선택된 이유(ADR-0048)가 여기서 관측된다.
        ClusterView before = View(1, 1, 2, 3, 4);
        await using WaitingMembership membership = new(before.Nodes[0], before);
        ClusterRouteResolver resolver = new(membership);

        using CancellationTokenSource timeout = Timeout();
        IAsyncEnumerator<IClusterRouter> watch = resolver.WatchAsync(timeout.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await watch.MoveNextAsync());

            List<PartitionKey> mineBefore = [];
            for (int i = 0; i < 3000; i++)
            {
                PartitionKey key = Key(i);
                if (resolver.Resolve(watch.Current, key).IsLocal)
                {
                    mineBefore.Add(key);
                }
            }

            // 4번이 떠난다. 나(1번)는 남는다.
            membership.Swap(View(2, 1, 2, 3));
            Assert.True(await watch.MoveNextAsync());

            // ⭐ 내가 이미 갖고 있던 키는 하나도 잃지 않는다. 노드가 빠졌을 뿐이므로
            //   내 점수가 남들보다 높았다는 사실은 변하지 않는다.
            foreach (PartitionKey key in mineBefore)
            {
                Assert.True(
                    resolver.Resolve(watch.Current, key).IsLocal,
                    $"노드가 떠났을 뿐인데 내 키를 잃었다: {key}");
            }

            int gained = 0;
            for (int i = 0; i < 3000; i++)
            {
                if (resolver.Resolve(watch.Current, Key(i)).IsLocal)
                {
                    gained++;
                }
            }

            Assert.True(gained > mineBefore.Count, "떠난 노드의 키를 아무도 받지 않았다.");
        }
        finally
        {
            await watch.DisposeAsync();
        }
    }
}
