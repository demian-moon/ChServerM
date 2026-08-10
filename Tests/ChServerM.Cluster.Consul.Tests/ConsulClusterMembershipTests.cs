using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Consul.Tests;

/// <summary>
/// Consul 어댑터 — <b>이 구현이 나오면서 <c>IClusterMembership</c> 이 가설에서 벗어난다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>여기서 고정하는 계약.</b> 첫 조회로 구성원을 발견한다 ·
/// <b>블로킹 쿼리로 변화를 당겨 받는다</b> ·
/// <b>⭐⭐ 세대는 Consul 인덱스가 아니다</b>(구성원이 그대로면 인덱스가 올라도 세대는 그대로) ·
/// 번호 메타가 없는 등록은 <b>구성원에서 제외</b>된다 ·
/// <b>첫 조회에 실패하면 만들어지지 않는다</b>.
/// </para>
/// <para>
/// <b>⚠ 실제 Consul 로 검증한다.</b> 인덱스가 언제 오르는지는 Consul 이 정하는 동작이라
/// 가짜로는 이 어댑터의 핵심 판단을 확인할 수 없다.
/// </para>
/// </remarks>
public sealed class ConsulClusterMembershipTests : IClassFixture<ConsulFixture>, IAsyncLifetime
{
    private readonly ConsulFixture _consul;
    private readonly string _serviceName = $"chserverm-{Guid.NewGuid():N}";
    private readonly List<string> _registered = [];

    public ConsulClusterMembershipTests(ConsulFixture consul) => _consul = consul;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_consul.Http is null)
        {
            return;
        }

        foreach (string id in _registered)
        {
            try
            {
                await _consul.DeregisterAsync(id);
            }
#pragma warning disable CA1031 // 정리 경로. 남은 등록 하나가 나머지 정리를 막지 않게 한다.
            catch (Exception)
            {
                // 이미 해제됐거나 컨테이너가 내려갔다.
            }
#pragma warning restore CA1031
        }
    }

    private ConsulClusterMembershipOptions Options() => new()
    {
        Address = _consul.Address!,
        ServiceName = _serviceName,
        SelfId = new NodeId(1),
        SelfName = "self",
        SelfEndPoint = new DnsEndPoint("10.0.0.1", 9001),
        WaitTime = TimeSpan.FromSeconds(10),
    };

    private async Task RegisterAsync(string id, ushort? nodeId, int port, string? extraMeta = null)
    {
        await _consul.RegisterAsync(_serviceName, id, nodeId, port, extraMeta);
        _registered.Add(id);
    }

    private void SkipIfUnavailable() =>
        Skip.If(_consul.SkipReason is not null, _consul.SkipReason ?? string.Empty);

    // ── 발견 ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FirstQuery_discoversRegisteredMembers()
    {
        SkipIfUnavailable();

        await RegisterAsync("svc-1", 1, 9001);
        await RegisterAsync("svc-2", 2, 9002);
        await RegisterAsync("svc-3", 3, 9003);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        ClusterView view = membership.Current;

        Assert.Equal(3, view.Count);
        Assert.True(view.Contains(new NodeId(1)));
        Assert.True(view.Contains(new NodeId(3)));

        // 노드 간 포트 메타가 주소에 실렸는가 — 클라이언트 포트와 섞으면
        // "연결은 되는데 엉뚱한 경로" 가 된다.
        Assert.True(view.TryGetNode(new NodeId(2), out ClusterNode? node));
        Assert.Equal(9002, ((DnsEndPoint)node!.EndPoint).Port);
    }

    [SkippableFact]
    public async Task RegistrationWithoutNodeIdMeta_isExcluded_notGuessed()
    {
        SkipIfUnavailable();

        // ⚠ 번호를 짐작해서 넣으면 ObjectId 가 조용히 충돌한다(ADR-0048).
        //   모르면 구성원이 아닌 것이 맞다.
        await RegisterAsync("svc-good", 1, 9001);
        await RegisterAsync("svc-nameless", null, 9002);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        Assert.Equal(1, membership.Current.Count);
        Assert.True(membership.Current.Contains(new NodeId(1)));
    }

    [SkippableFact]
    public async Task SelfIsKnownFromConfiguration_evenWhenNotAMember()
    {
        SkipIfUnavailable();

        // ⚠ 자기 자신을 뷰에 끼워 넣지 않는다 — "뷰에 있다 = 지금 보낼 수 있다" 가
        //   이 축의 규칙이고(ADR-0047), 나만 예외로 두면 소유권이 갈라진다.
        await RegisterAsync("svc-2", 2, 9002);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        Assert.Equal(new NodeId(1), membership.Self.Id);
        Assert.False(membership.Current.Contains(new NodeId(1)));
    }

    // ── ⭐ 블로킹 쿼리로 변화를 당겨 받는다 ──────────────────────────

    [SkippableFact]
    public async Task MemberJoins_wakesTheWaiter_andBumpsGeneration()
    {
        SkipIfUnavailable();

        await RegisterAsync("svc-1", 1, 9001);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        int seen = membership.Current.Generation;
        ValueTask<ClusterView> waiting = membership.WaitForChangeAsync(seen, TestTimeout());

        await RegisterAsync("svc-2", 2, 9002);

        ClusterView changed = await waiting;

        Assert.True(changed.Generation > seen, "구성이 바뀌었는데 세대가 오르지 않았다.");
        Assert.Equal(2, changed.Count);
        Assert.True(changed.Contains(new NodeId(2)));
    }

    [SkippableFact]
    public async Task MemberLeaves_isObserved()
    {
        SkipIfUnavailable();

        await RegisterAsync("svc-1", 1, 9001);
        await RegisterAsync("svc-2", 2, 9002);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        Assert.Equal(2, membership.Current.Count);

        int seen = membership.Current.Generation;
        ValueTask<ClusterView> waiting = membership.WaitForChangeAsync(seen, TestTimeout());

        await _consul.DeregisterAsync("svc-2");

        ClusterView changed = await waiting;

        Assert.Equal(1, changed.Count);
        Assert.False(changed.Contains(new NodeId(2)));
    }

    // ── ⭐⭐ 세대는 Consul 인덱스가 아니다 ───────────────────────────

    [SkippableFact]
    public async Task UnrelatedChange_bumpsConsulIndex_butNotOurGeneration()
    {
        SkipIfUnavailable();

        // ⭐⭐ 이 어댑터의 핵심 판단이다. Consul 인덱스는 우리와 무관한 이유로도 오르고,
        //   그때마다 세대를 올리면 **모든 노드가 헛되이 소유권을 재검토**한다
        //   (ADR-0052 의 WatchAsync 소비자들). 그 비용은 노드 수만큼 곱해진다.
        await RegisterAsync("svc-1", 1, 9001);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        int before = membership.Current.Generation;

        // 어댑터가 읽지 않는 메타만 바꿔 다시 등록한다 → Consul 인덱스는 오르고
        // 구성원 집합은 그대로다.
        for (int i = 0; i < 3; i++)
        {
            await _consul.RegisterAsync(
                _serviceName, "svc-1", 1, 9001, extraMetaValue: $"noise-{i}");

            await Task.Delay(300);
        }

        Assert.Equal(before, membership.Current.Generation);
        Assert.Equal(1, membership.Current.Count);
    }

    [SkippableFact]
    public async Task WaitForChange_returnsImmediately_whenCallerHasNotSeenCurrent()
    {
        SkipIfUnavailable();

        await RegisterAsync("svc-1", 1, 9001);

        await using ConsulClusterMembership membership = await ConsulClusterMembership.CreateAsync(
            Options(), NullServerLogger.Instance, cancellationToken: TestTimeout());

        // 세대 인자가 "확인 직후·대기 직전" 창을 닫는다(ADR-0047) — 아직 못 본 세대를
        // 들고 기다리면 즉시 돌아와야 한다.
        ClusterView view = await membership.WaitForChangeAsync(
            membership.Current.Generation - 1, TestTimeout());

        Assert.Equal(membership.Current.Generation, view.Generation);
    }

    // ── 기동 실패 ────────────────────────────────────────────────────

    [Fact]
    public async Task FirstQueryFailure_preventsConstruction()
    {
        // ⚠⚠ 구성을 모르는 채로 기동하면 이 노드는 전 키스페이스를 자기 것이라 믿는다.
        //   그 상태로 트래픽을 받는 것은 기동 실패보다 나쁘다(ADR-0055).
        //   ⭐ 이 테스트는 Consul 이 없어도 돈다 — 그것이 요점이다.
        ConsulClusterMembershipOptions options = new()
        {
            Address = new Uri("http://127.0.0.1:1"),
            ServiceName = "nowhere",
            SelfId = new NodeId(1),
            SelfName = "self",
            SelfEndPoint = new DnsEndPoint("10.0.0.1", 9001),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ConsulClusterMembership.CreateAsync(
                options, NullServerLogger.Instance, cancellationToken: TestTimeout()));
    }

    [Fact]
    public async Task InvalidOptions_failAtAssembly()
    {
        ConsulClusterMembershipOptions options = new()
        {
            ServiceName = string.Empty,
            SelfName = "self",
            SelfEndPoint = new DnsEndPoint("10.0.0.1", 9001),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ConsulClusterMembership.CreateAsync(
                options, NullServerLogger.Instance, cancellationToken: TestTimeout()));
    }

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
