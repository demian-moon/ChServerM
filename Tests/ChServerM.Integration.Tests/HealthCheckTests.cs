using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 헬스 체크(Phase 11)를 검증한다 — 집계·프로브 필터·예외 격리(유닛)와 생명주기 readiness·
/// 실행 모델 liveness 배선(종단).
/// </summary>
public sealed class HealthCheckTests
{
    private const ushort EchoId = 900;

    private sealed class StubCheck(HealthCheckResult result) : IHealthCheck
    {
        public ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class ThrowingCheck : IHealthCheck
    {
        public ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task WorstStatusWins_DegradedPullsHealthyDown()
    {
        HealthCheckService service = new(
        [
            new HealthCheckRegistration("a", new StubCheck(HealthCheckResult.Healthy())),
            new HealthCheckRegistration("b", new StubCheck(HealthCheckResult.Degraded("느림"))),
        ]);

        HealthReport report = await service.CheckHealthAsync();

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal(2, report.Entries.Count);
    }

    [Fact]
    public async Task WorstStatusWins_UnhealthyBeatsDegraded()
    {
        HealthCheckService service = new(
        [
            new HealthCheckRegistration("a", new StubCheck(HealthCheckResult.Degraded())),
            new HealthCheckRegistration("b", new StubCheck(HealthCheckResult.Unhealthy())),
            new HealthCheckRegistration("c", new StubCheck(HealthCheckResult.Healthy())),
        ]);

        HealthReport report = await service.CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public async Task NoChecksForProbe_IsHealthy()
    {
        // 감시할 것이 없으면 문제도 없다 — 빈 집계는 Healthy.
        HealthCheckService service = new([]);

        HealthReport report = await service.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Empty(report.Entries);
    }

    [Fact]
    public async Task ProbeFilter_RunsOnlyMatchingChecks()
    {
        HealthCheckService service = new(
        [
            new HealthCheckRegistration("live", new StubCheck(HealthCheckResult.Healthy()), HealthProbe.Liveness),
            new HealthCheckRegistration("ready", new StubCheck(HealthCheckResult.Unhealthy()), HealthProbe.Readiness),
        ]);

        HealthReport liveness = await service.CheckHealthAsync(HealthProbe.Liveness);

        // readiness 의 Unhealthy 는 liveness 프로브에 섞이지 않는다.
        HealthReportEntry entry = Assert.Single(liveness.Entries);
        Assert.Equal("live", entry.Name);
        Assert.Equal(HealthStatus.Healthy, liveness.Status);
    }

    [Fact]
    public async Task ThrowingCheck_BecomesUnhealthy_WithoutBreakingReport()
    {
        // 한 체크의 예외가 전체 조회를 깨지 않는다 — 그 항목만 Unhealthy 로 격리된다.
        HealthCheckService service = new(
        [
            new HealthCheckRegistration("boom", new ThrowingCheck()),
            new HealthCheckRegistration("ok", new StubCheck(HealthCheckResult.Healthy())),
        ]);

        HealthReport report = await service.CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(2, report.Entries.Count);
        HealthReportEntry boom = report.Entries[0];
        Assert.Equal("boom", boom.Name);
        Assert.Equal(HealthStatus.Unhealthy, boom.Status);
        Assert.Equal("boom", boom.Description);
    }

    [Fact]
    public async Task Readiness_FollowsServerLifecycle()
    {
        await using ChServerMServer server = BuildServer(withExecutionModel: true);

        // 시작 전: 아직 수용 안 함 → not ready.
        HealthReport beforeStart = await server.Health.CheckHealthAsync(HealthProbe.Readiness);
        Assert.Equal(HealthStatus.Unhealthy, beforeStart.Status);

        await server.StartAsync();

        // 수용 중 → ready.
        HealthReport accepting = await server.Health.CheckHealthAsync(HealthProbe.Readiness);
        Assert.Equal(HealthStatus.Healthy, accepting.Status);

        await server.UnbindAsync();

        // 드레이닝 → not ready(로드밸런서 디레지스터 신호).
        HealthReport draining = await server.Health.CheckHealthAsync(HealthProbe.Readiness);
        Assert.Equal(HealthStatus.Unhealthy, draining.Status);
    }

    [Fact]
    public async Task Liveness_ExecutionModelThreadsAlive_IsHealthy()
    {
        await using ChServerMServer server = BuildServer(withExecutionModel: true);
        await server.StartAsync();

        HealthReport liveness = await server.Health.CheckHealthAsync(HealthProbe.Liveness);

        Assert.Equal(HealthStatus.Healthy, liveness.Status);
        HealthReportEntry entry = Assert.Single(liveness.Entries);
        Assert.Equal("execution-model", entry.Name);
    }

    [Fact]
    public async Task Liveness_WithoutExecutionModel_IsHealthyAndEmpty()
    {
        // 실행 모델이 없으면 liveness 체크가 등록되지 않는다 → 빈 집계 = Healthy.
        await using ChServerMServer server = BuildServer(withExecutionModel: false);
        await server.StartAsync();

        HealthReport liveness = await server.Health.CheckHealthAsync(HealthProbe.Liveness);

        Assert.Equal(HealthStatus.Healthy, liveness.Status);
        Assert.Empty(liveness.Entries);
    }

    [Fact]
    public async Task CustomHealthCheck_IsRegisteredAndAggregated()
    {
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(NewTransport(out _))
            .UseFraming(NewDecoder(), NewEncoder())
            .AddHealthCheck("custom", new StubCheck(HealthCheckResult.Unhealthy("의존성 없음")), HealthProbe.Readiness)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled)))
            .Build();

        await server.StartAsync();

        HealthReport readiness = await server.Health.CheckHealthAsync(HealthProbe.Readiness);

        // 내장 acceptance(Healthy) + 사용자 custom(Unhealthy) → 집계 Unhealthy.
        Assert.Equal(HealthStatus.Unhealthy, readiness.Status);
        Assert.Contains(readiness.Entries, e => e.Name == "custom" && e.Status == HealthStatus.Unhealthy);
        Assert.Contains(readiness.Entries, e => e.Name == "acceptance");
    }

    private static ChServerMServer BuildServer(bool withExecutionModel)
    {
        ServerBuilder builder = new ServerBuilder()
            .UseTransport(NewTransport(out _))
            .UseFraming(NewDecoder(), NewEncoder())
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled)));

        if (withExecutionModel)
        {
            builder.UseExecutionModel(new PartitionedExecutionModel(new PartitionedExecutionOptions { PartitionCount = 2 }));
        }

        return builder.Build();
    }

    private static InMemoryServerTransport NewTransport(out InMemoryEndPoint endPoint)
    {
        InMemoryTransportHub hub = new();
        endPoint = new InMemoryEndPoint($"health-{Guid.NewGuid():N}");
        return new InMemoryServerTransport(hub, endPoint, new InMemoryTransportOptions());
    }

    private static FixedHeaderFrameDecoder NewDecoder() => new(new FramingOptions { MaxPayloadLength = 4096 });

    private static FixedHeaderFrameEncoder NewEncoder() => new(new FramingOptions { MaxPayloadLength = 4096 });
}
