using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace ChServerM.RealTime.Tests;

public sealed class TimerWheelTests
{
    /// <summary>경합 테스트용 잡. 임의 스레드에서 불려도 안전하게 센다.</summary>
    private sealed class AtomicJob : ITimerJob
    {
        public int Expired;
        public int Canceled;

        public void OnTimerExpired() => Interlocked.Increment(ref Expired);

        public void OnTimerCanceled() => Interlocked.Increment(ref Canceled);
    }

    [Fact]
    public void 동시_예약_취소_경합에서도_각_타이머는_정확히_한_번_종결된다()
    {
        // 회귀(감사 2026-08-18 X-1): 노드 풀 Treiber 스택 pop 의 ABA 로 활성 노드가 풀에
        // 재진입하면 이중 발화·유실이 생긴다. 경합은 단발로 재현되지 않으므로
        // 반복 실행 테스트(9.9)로 불변식("발화+취소 = 정확히 1")을 고정한다.
        var wheel = new TimerWheel(new TimerWheelOptions
        {
            TickDuration = TimeSpan.FromMilliseconds(1),
            SlotsPerLevel = 32,
            LevelCount = 3,
        });

        const int workerCount = 4;
        const int perWorker = 3000;
        var jobs = new AtomicJob[workerCount * perWorker];
        int rejectedSchedules = 0;
        bool stop = false;

        var driver = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                wheel.Advance();
                Thread.Sleep(0);
            }

            wheel.Advance();
        })
        { IsBackground = true };
        driver.Start();

        var workers = new Thread[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            int workerIndex = w;
            workers[w] = new Thread(() =>
            {
                var random = new Random((workerIndex * 7919) + 17);
                for (int i = 0; i < perWorker; i++)
                {
                    var job = new AtomicJob();
                    jobs[(workerIndex * perWorker) + i] = job;
                    TimerScheduleStatus status = wheel.TrySchedule(
                        job, TimeSpan.FromMilliseconds(random.Next(0, 8)), out TimerHandle handle);
                    if (status != TimerScheduleStatus.Accepted)
                    {
                        Interlocked.Increment(ref rejectedSchedules);
                        continue;
                    }

                    if ((i & 1) == 0)
                    {
                        handle.TryCancel(); // 절반은 즉시 취소 — 발화·재사용과 경합시킨다.
                    }
                }
            })
            { IsBackground = true };
            workers[w].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        // 남은 타이머가 전부 소진될 때까지 드라이버를 계속 돌린다.
        var drain = Stopwatch.StartNew();
        while (wheel.Statistics.PendingTimers > 0 && drain.ElapsedMilliseconds < 10_000)
        {
            Thread.Sleep(5);
        }

        Volatile.Write(ref stop, true);
        driver.Join();

        Assert.Equal(0, rejectedSchedules);
        TimerWheelStatistics stats = wheel.Statistics;
        Assert.Equal(0, stats.PendingTimers);
        Assert.Equal(stats.ScheduledTimers, stats.FiredTimers + stats.CanceledTimers);
        foreach (AtomicJob job in jobs)
        {
            int outcomes = Volatile.Read(ref job.Expired) + Volatile.Read(ref job.Canceled);
            Assert.Equal(1, outcomes);
        }
    }

    /// <summary>콜백 호출을 기록하는 잡. 만료·취소가 각각 몇 번 불렸는지 센다.</summary>
    private sealed class RecordingJob : ITimerJob
    {
        public int Expired;
        public int Canceled;

        public void OnTimerExpired() => Expired++;

        public void OnTimerCanceled() => Canceled++;
    }

    private sealed class ThrowingJob : ITimerJob
    {
        public void OnTimerExpired() => throw new InvalidOperationException("고의 실패");

        public void OnTimerCanceled() => throw new InvalidOperationException("고의 실패");
    }

    private static (TimerWheel Wheel, ManualTimeProvider Provider) CreateWheel(
        Action<TimerWheelOptions>? configure = null)
    {
        var provider = new ManualTimeProvider(frequency: 1_000_000); // 1 raw = 1µs
        var options = new TimerWheelOptions
        {
            TickDuration = TimeSpan.FromMilliseconds(100),
            SlotsPerLevel = 8,
            LevelCount = 3,
            TimeProvider = provider,
        };
        configure?.Invoke(options);
        return (new TimerWheel(options), provider);
    }

    [Fact]
    public void 마감_전에는_발화하지_않는다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();

        Assert.Equal(TimerScheduleStatus.Accepted, wheel.TrySchedule(job, TimeSpan.FromMilliseconds(250), out _));

        provider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, wheel.Advance());
        Assert.Equal(0, job.Expired);
    }

    [Fact]
    public void 마감이_지나면_정확히_한_번_발화한다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.FromMilliseconds(250), out _);

        provider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, wheel.Advance());
        Assert.Equal(1, job.Expired);
        Assert.Equal(0, job.Canceled);

        provider.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, wheel.Advance()); // 재발화 없음
        Assert.Equal(1, job.Expired);
    }

    [Fact]
    public void 지연_0은_다음_진행에서_즉시_발화한다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.Zero, out _);

        provider.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, wheel.Advance());
        Assert.Equal(1, job.Expired);
    }

    [Fact]
    public void 상위_레벨의_타이머가_캐스케이딩되어_같은_패스에서_발화한다()
    {
        // 레벨 0 범위 = 100ms × 8 = 800ms. 3초 지연은 레벨 1 이상에 배치된다.
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.FromSeconds(3), out _);

        provider.Advance(TimeSpan.FromMilliseconds(3050));
        Assert.Equal(1, wheel.Advance());
        Assert.Equal(1, job.Expired);
    }

    [Fact]
    public void 최상위_휠_범위를_넘는_지연도_재순회로_발화한다()
    {
        // 전체 범위 = 100ms × 8³ = 51.2s. 3분 지연은 최상위 휠을 재순회한다.
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.FromMinutes(3), out _);

        provider.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, wheel.Advance());
        Assert.Equal(0, job.Expired);

        provider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, wheel.Advance());
        Assert.Equal(1, job.Expired);
    }

    [Fact]
    public void 같은_슬롯의_타이머_여럿이_전부_발화한다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var jobs = new List<RecordingJob>();
        for (int i = 0; i < 100; i++)
        {
            var job = new RecordingJob();
            jobs.Add(job);
            Assert.Equal(
                TimerScheduleStatus.Accepted,
                wheel.TrySchedule(job, TimeSpan.FromMilliseconds(300), out _));
        }

        provider.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(100, wheel.Advance());
        Assert.All(jobs, job => Assert.Equal(1, job.Expired));
    }

    [Fact]
    public void 취소하면_발화하지_않고_취소_콜백이_한_번_불린다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.FromMilliseconds(300), out TimerHandle handle);

        Assert.True(handle.TryCancel());
        Assert.Equal(1, job.Canceled);

        Assert.False(handle.TryCancel()); // 두 번째 취소는 실패
        Assert.Equal(1, job.Canceled);

        provider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, wheel.Advance());
        Assert.Equal(0, job.Expired); // 만료 콜백은 절대 불리지 않는다 — 만료·취소 분리 계약
    }

    [Fact]
    public void 발화_후의_취소는_실패한다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.FromMilliseconds(100), out TimerHandle handle);

        provider.Advance(TimeSpan.FromMilliseconds(300));
        wheel.Advance();

        Assert.False(handle.TryCancel());
        Assert.Equal(1, job.Expired);
        Assert.Equal(0, job.Canceled);
    }

    [Fact]
    public void 낡은_핸들은_재사용된_노드를_오취소하지_못한다()
    {
        // ABA 시나리오: A 발화 → 노드가 풀로 → B 가 같은 노드를 재사용.
        // A 의 핸들로 취소를 시도해도 세대 불일치로 실패해야 한다.
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var jobA = new RecordingJob();
        wheel.TrySchedule(jobA, TimeSpan.FromMilliseconds(100), out TimerHandle staleHandle);

        provider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, wheel.Advance()); // A 발화, 노드 회수

        var jobB = new RecordingJob();
        wheel.TrySchedule(jobB, TimeSpan.FromMilliseconds(100), out _);

        Assert.False(staleHandle.TryCancel()); // 낡은 핸들은 무력하다

        provider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, wheel.Advance()); // B 는 영향 없이 발화한다
        Assert.Equal(1, jobB.Expired);
        Assert.Equal(0, jobB.Canceled);
    }

    [Fact]
    public void 상한을_넘는_예약은_거부된다()
    {
        (TimerWheel wheel, _) = CreateWheel(o => o.MaxPendingTimers = 2);
        var job = new RecordingJob();

        Assert.Equal(TimerScheduleStatus.Accepted, wheel.TrySchedule(job, TimeSpan.FromSeconds(1), out _));
        Assert.Equal(TimerScheduleStatus.Accepted, wheel.TrySchedule(job, TimeSpan.FromSeconds(1), out _));
        Assert.Equal(
            TimerScheduleStatus.CapacityExceeded,
            wheel.TrySchedule(job, TimeSpan.FromSeconds(1), out TimerHandle rejected));
        Assert.True(rejected.IsNone);
        Assert.Equal(1, wheel.Statistics.RejectedSchedules);
    }

    [Fact]
    public void 취소된_자리는_새_예약에_재사용된다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel(o => o.MaxPendingTimers = 1);
        var job = new RecordingJob();

        wheel.TrySchedule(job, TimeSpan.FromSeconds(1), out TimerHandle handle);
        Assert.True(handle.TryCancel());

        Assert.Equal(TimerScheduleStatus.Accepted, wheel.TrySchedule(job, TimeSpan.FromMilliseconds(100), out _));
        provider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, wheel.Advance());
    }

    [Fact]
    public void 콜백_예외가_같은_틱의_다른_타이머를_죽이지_않는다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var good = new RecordingJob();
        wheel.TrySchedule(new ThrowingJob(), TimeSpan.FromMilliseconds(100), out _);
        wheel.TrySchedule(good, TimeSpan.FromMilliseconds(100), out _);

        provider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(2, wheel.Advance());

        Assert.Equal(1, good.Expired);
        Assert.Equal(1, wheel.Statistics.FaultedCallbacks);
    }

    [Fact]
    public void 셧다운은_남은_타이머_전부에_취소를_통지한다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var shortJob = new RecordingJob();
        var longJob = new RecordingJob();
        wheel.TrySchedule(shortJob, TimeSpan.FromMilliseconds(200), out _);
        wheel.TrySchedule(longJob, TimeSpan.FromHours(1), out _);

        // 한 번 진행시켜 슬롯 배치까지 끝낸 뒤 셧다운한다.
        provider.Advance(TimeSpan.FromMilliseconds(100));
        wheel.Advance();

        Assert.Equal(2, wheel.Shutdown());
        Assert.Equal(1, shortJob.Canceled);
        Assert.Equal(1, longJob.Canceled);
        Assert.Equal(0, shortJob.Expired);

        Assert.Equal(
            TimerScheduleStatus.Stopped,
            wheel.TrySchedule(new RecordingJob(), TimeSpan.FromSeconds(1), out _));
        Assert.Equal(0, wheel.Advance());
        Assert.Equal(0, wheel.Shutdown()); // 두 번째 셧다운은 아무것도 하지 않는다
    }

    [Fact]
    public void 통계가_수락_발화_취소를_추적한다()
    {
        (TimerWheel wheel, ManualTimeProvider provider) = CreateWheel();
        var job = new RecordingJob();
        wheel.TrySchedule(job, TimeSpan.FromMilliseconds(100), out _);
        wheel.TrySchedule(job, TimeSpan.FromSeconds(10), out TimerHandle toCancel);
        wheel.TrySchedule(job, TimeSpan.FromHours(2), out _);

        provider.Advance(TimeSpan.FromMilliseconds(300));
        wheel.Advance();
        toCancel.TryCancel();

        TimerWheelStatistics stats = wheel.Statistics;
        Assert.Equal(3, stats.ScheduledTimers);
        Assert.Equal(1, stats.FiredTimers);
        Assert.Equal(1, stats.CanceledTimers);
        Assert.Equal(1, stats.PendingTimers);
        Assert.Equal(0, stats.RejectedSchedules);
        Assert.Equal(0, stats.FaultedCallbacks);
    }

    [Fact]
    public void 널_잡은_거부한다()
    {
        (TimerWheel wheel, _) = CreateWheel();

        Assert.Throws<ArgumentNullException>(() => wheel.TrySchedule(null!, TimeSpan.Zero, out _));
    }

    [Theory]
    [InlineData(0, 8, 3)]     // TickDuration 0
    [InlineData(100, 7, 3)]   // 슬롯 수가 2의 거듭제곱이 아님
    [InlineData(100, 8, 0)]   // 레벨 0
    [InlineData(100, 8, 9)]   // 레벨 초과
    public void 옵션을_검증한다(int tickMs, int slots, int levels)
    {
        var options = new TimerWheelOptions
        {
            TickDuration = TimeSpan.FromMilliseconds(tickMs),
            SlotsPerLevel = slots,
            LevelCount = levels,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
