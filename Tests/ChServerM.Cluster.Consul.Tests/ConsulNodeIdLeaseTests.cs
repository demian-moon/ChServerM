using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Cluster.Consul.Tests;

/// <summary>
/// 노드 번호 임차 — <b>겹친 번호를 조용한 충돌이 아니라 기동 실패로 만든다</b> (ADR-0056).
/// </summary>
/// <remarks>
/// <para>
/// <b>여기서 고정하는 계약.</b> 같은 번호를 두 번 잡을 수 없다 ·
/// <b>충돌 예외가 누가 들고 있는지 알려 준다</b> · 정상 반납하면 즉시 다시 잡을 수 있다 ·
/// 다른 번호는 서로 방해하지 않는다 · <b>세션이 밖에서 무효화되면 <c>Lost</c> 가 완료된다</b> ·
/// 살아 있는 동안에는 완료되지 않는다.
/// </para>
/// <para>
/// <b>⚠ 여기서 검증하지 <i>않는</i> 것 — 상호 배제.</b> 우리 세션이 만료됐는데 우리는 아직
/// 돌고 있는 구간이 존재하고, 그것은 <c>LockDelay</c> 로 좁힐 뿐 없앨 수 없다.
/// <c>Lost</c> 를 노출하는 이유가 그것이며, <b>그때 무엇을 할지는 앱이 정한다</b>.
/// </para>
/// </remarks>
public sealed class ConsulNodeIdLeaseTests : IClassFixture<ConsulFixture>
{
    private readonly ConsulFixture _consul;
    private readonly string _prefix = $"chserverm-test/{Guid.NewGuid():N}";

    public ConsulNodeIdLeaseTests(ConsulFixture consul) => _consul = consul;

    private ConsulNodeIdLeaseOptions Options(ushort nodeId, string holder) => new()
    {
        Address = _consul.Address!,
        NodeId = new NodeId(nodeId),
        HolderName = holder,
        KeyPrefix = _prefix,

        // ⚠ Consul 은 TTL 하한이 10초다. 그보다 짧게 주면 세션 생성이 거부된다.
        SessionTtl = TimeSpan.FromSeconds(10),
        RenewInterval = TimeSpan.FromSeconds(4),

        // 테스트에서는 재획득을 기다리지 않는다. 운영 기본값(15초)의 의미는 문서가 진다.
        LockDelay = TimeSpan.Zero,
    };

    private void SkipIfUnavailable() =>
        Skip.If(_consul.SkipReason is not null, _consul.SkipReason ?? string.Empty);

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    // ── 겹침을 드러낸다 ──────────────────────────────────────────────

    [SkippableFact]
    public async Task SameNodeId_cannotBeLeasedTwice_andTheErrorNamesTheHolder()
    {
        SkipIfUnavailable();

        // ⭐ 이것이 이 타입의 존재 이유다. ClusterView 는 **한 목록 안의** 중복만 잡으므로
        //   서로 다른 목록을 든 두 노드는 아무도 겹침을 보지 못했다(ADR-0048).
        await using ConsulNodeIdLease first = await ConsulNodeIdLease.AcquireAsync(
            Options(7, "node-a"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ConsulNodeIdLease.AcquireAsync(
                Options(7, "node-b"), NullServerLogger.Instance, cancellationToken: TestTimeout()));

        // ⚠ 진단이 없으면 운영자가 할 수 있는 일이 없다 — 어느 프로세스인지 알아야 한다.
        Assert.Contains("node-a", error.Message, StringComparison.Ordinal);
        Assert.Contains("7", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task DifferentNodeIds_doNotInterfere()
    {
        SkipIfUnavailable();

        await using ConsulNodeIdLease one = await ConsulNodeIdLease.AcquireAsync(
            Options(1, "node-1"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        await using ConsulNodeIdLease two = await ConsulNodeIdLease.AcquireAsync(
            Options(2, "node-2"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        Assert.Equal(new NodeId(1), one.NodeId);
        Assert.Equal(new NodeId(2), two.NodeId);
    }

    [SkippableFact]
    public async Task ExplicitRelease_freesTheNumberImmediately()
    {
        SkipIfUnavailable();

        // ⚠ LockDelay 는 **세션 무효화에만** 걸린다 — 정상 종료한 노드의 번호는
        //   곧바로 다시 쓸 수 있어야 롤링 배포가 성립한다.
        ConsulNodeIdLease first = await ConsulNodeIdLease.AcquireAsync(
            Options(9, "node-a"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        await first.DisposeAsync();

        await using ConsulNodeIdLease second = await ConsulNodeIdLease.AcquireAsync(
            Options(9, "node-b"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        Assert.Equal(new NodeId(9), second.NodeId);
    }

    // ── 임차를 잃는 것을 관측할 수 있다 ─────────────────────────────

    [SkippableFact]
    public async Task Lost_doesNotComplete_whileTheLeaseIsHealthy()
    {
        SkipIfUnavailable();

        await using ConsulNodeIdLease lease = await ConsulNodeIdLease.AcquireAsync(
            Options(11, "node-a"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        // 갱신 주기(4초)를 두 번 넘겨도 살아 있어야 한다 — 갱신이 실제로 도는지 본다.
        Task completed = await Task.WhenAny(lease.Lost, Task.Delay(TimeSpan.FromSeconds(9)));

        Assert.NotSame(lease.Lost, completed);
    }

    [SkippableFact]
    public async Task Lost_completes_whenTheSessionIsInvalidatedElsewhere()
    {
        SkipIfUnavailable();

        // ⭐ 세션이 밖에서 사라지는 것 = 우리가 죽었다고 Consul 이 판정한 것.
        //   그 순간 다른 노드가 이 번호를 가져갈 수 있으므로 **관측 가능해야 한다**.
        await using ConsulNodeIdLease lease = await ConsulNodeIdLease.AcquireAsync(
            Options(13, "node-a"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        await DestroyAllSessionsAsync();

        Task completed = await Task.WhenAny(lease.Lost, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(lease.Lost, completed);
    }

    [SkippableFact]
    public async Task LostNumber_canBeTakenByAnotherNode()
    {
        SkipIfUnavailable();

        // 임차를 잃으면 번호가 실제로 풀린다 — Lost 가 거짓말이 아님을 확인한다.
        await using ConsulNodeIdLease lease = await ConsulNodeIdLease.AcquireAsync(
            Options(15, "node-a"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        await DestroyAllSessionsAsync();
        await lease.Lost.WaitAsync(TimeSpan.FromSeconds(20));

        await using ConsulNodeIdLease taken = await ConsulNodeIdLease.AcquireAsync(
            Options(15, "node-b"), NullServerLogger.Instance, cancellationToken: TestTimeout());

        Assert.Equal(new NodeId(15), taken.NodeId);
    }

    // ── 조립 검증 ────────────────────────────────────────────────────

    [Fact]
    public async Task MissingHolderName_failsAtAssembly()
    {
        // 이름이 없으면 충돌 시 누가 들고 있는지 알 수 없다 — 그 상태를 허용하지 않는다.
        ConsulNodeIdLeaseOptions options = new() { NodeId = new NodeId(1), HolderName = string.Empty };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ConsulNodeIdLease.AcquireAsync(
                options, NullServerLogger.Instance, cancellationToken: TestTimeout()));
    }

    [Fact]
    public async Task RenewIntervalNotShorterThanTtl_failsAtAssembly()
    {
        // ⚠ 갱신이 만료보다 느리면 임차는 **반드시** 끊긴다. 조용히 두면
        //   "가끔 노드가 번호를 잃는다" 로 나타난다.
        ConsulNodeIdLeaseOptions options = new()
        {
            NodeId = new NodeId(1),
            HolderName = "node-a",
            SessionTtl = TimeSpan.FromSeconds(10),
            RenewInterval = TimeSpan.FromSeconds(10),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ConsulNodeIdLease.AcquireAsync(
                options, NullServerLogger.Instance, cancellationToken: TestTimeout()));
    }

    [Fact]
    public async Task UnreachableConsul_failsFast()
    {
        ConsulNodeIdLeaseOptions options = new()
        {
            Address = new Uri("http://127.0.0.1:1"),
            NodeId = new NodeId(1),
            HolderName = "node-a",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ConsulNodeIdLease.AcquireAsync(
                options, NullServerLogger.Instance, cancellationToken: TestTimeout()));
    }

    /// <summary>이 에이전트의 세션을 전부 파괴한다 — "노드가 죽었다" 를 흉내 낸다.</summary>
    private async Task DestroyAllSessionsAsync()
    {
        using HttpResponseMessage list = await _consul.Http!.GetAsync(
            new Uri("/v1/session/list", UriKind.Relative), TestTimeout());

        list.EnsureSuccessStatusCode();

        string body = await list.Content.ReadAsStringAsync(TestTimeout());

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
        foreach (System.Text.Json.JsonElement session in document.RootElement.EnumerateArray())
        {
            string? id = session.GetProperty("ID").GetString();
            if (id is null)
            {
                continue;
            }

            using HttpResponseMessage destroy = await _consul.Http.PutAsync(
                new Uri($"/v1/session/destroy/{id}", UriKind.Relative), null, TestTimeout());

            destroy.EnsureSuccessStatusCode();
        }
    }
}
