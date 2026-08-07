using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Diagnostics;
using ChServerM.Execution;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 파티션 정지 감지(ADR-0027)를 검증한다 — 완료하지 않는 작업이 전용 스레드를 붙들 때
/// 그것이 관측되는지, 그리고 그 심각도가 <b>재시작 대상이 아니라 저하</b>인지.
/// </summary>
/// <remarks>
/// 정지는 스레드 생존(<c>IsAlive</c>)으로 잡히지 않는 사각지대다 — 붙들린 스레드도 살아 있다.
/// 임계 0 으로 질의하면 "진행 중인 작업은 전부 정지" 가 되어, 가짜 시계 없이 결정적으로 검증된다.
/// </remarks>
public sealed class PartitionStallDetectionTests
{
    /// <summary>신호를 줄 때까지 스레드를 붙드는 작업 — 완료하지 않는 핸들러를 흉내낸다.</summary>
    private readonly struct BlockingWork(ManualResetEventSlim started, ManualResetEventSlim release) : IPartitionWork
    {
        public void Execute()
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Idle_partitions_are_not_stalled()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 4 });

        // 아무 작업도 없으면 임계 0 이어도 정지가 아니다 — 유휴와 정지를 구분한다.
        Assert.Equal(0, model.CountStalledPartitions(TimeSpan.Zero));
    }

    [Fact]
    public async Task Blocked_partition_is_reported_as_stalled()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 2 });

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();

        try
        {
            Assert.True(model.GetPartition(0).TryPost(new BlockingWork(started, release)));
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "작업이 시작되지 않았다.");

            // 붙들린 파티션 하나가 드러난다 — 다른 파티션은 멀쩡하다.
            Assert.Equal(1, model.CountStalledPartitions(TimeSpan.Zero));

            // 임계가 아주 크면 아직 정지로 보지 않는다(정상적으로 오래 걸리는 작업 구분).
            Assert.Equal(0, model.CountStalledPartitions(TimeSpan.FromHours(1)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Stall_clears_after_work_completes()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();

        Assert.True(model.GetPartition(0).TryPost(new BlockingWork(started, release)));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "작업이 시작되지 않았다.");
        Assert.Equal(1, model.CountStalledPartitions(TimeSpan.Zero));

        release.Set();

        // 표식 해제가 finally 에 있으므로 완료 후에는 반드시 유휴로 돌아온다.
        await WaitUntilAsync(() => model.CountStalledPartitions(TimeSpan.Zero) == 0);
    }

    [Fact]
    public async Task Faulting_work_does_not_leave_a_phantom_stall()
    {
        // 표식 해제를 finally 에 두지 않았다면 예외 하나로 이 파티션이 영원히 "정지 중" 으로
        // 보고돼 진단이 거짓말을 한다(9.2 의 상태 복원 규약).
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });

        Assert.True(model.GetPartition(0).TryPost(new FaultingWork()));

        await WaitUntilAsync(() => model.TotalExecutedCount >= 1);
        Assert.Equal(0, model.CountStalledPartitions(TimeSpan.Zero));
    }

    [Fact]
    public async Task Stalled_partition_reports_degraded_not_unhealthy()
    {
        // 심각도가 핵심이다 — 정지로 liveness 를 실패시키면 일시적 지연에도 오케스트레이터가
        // 프로세스를 재시작해 더 큰 장애가 된다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions
        {
            PartitionCount = 2,
            StallThreshold = TimeSpan.Zero,
        });

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();

        try
        {
            Assert.True(model.GetPartition(0).TryPost(new BlockingWork(started, release)));
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "작업이 시작되지 않았다.");

            HealthCheckResult result = await model.CheckAsync();

            Assert.Equal(HealthStatus.Degraded, result.Status);
            Assert.Contains("붙들려", result.Description);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Healthy_when_nothing_is_stalled()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions
        {
            PartitionCount = 2,
            StallThreshold = TimeSpan.Zero,
        });

        HealthCheckResult result = await model.CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Negative_stall_threshold_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PartitionedExecutionModel(new PartitionedExecutionOptions
            {
                StallThreshold = TimeSpan.FromSeconds(-1),
            }));
    }

    private readonly struct FaultingWork : IPartitionWork
    {
        public void Execute() => throw new InvalidOperationException("의도적 실패");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("조건이 제한 시간 안에 만족되지 않았다.");
            }

            await Task.Delay(5);
        }
    }
}
