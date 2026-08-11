using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChServerM.RealTime.Tests;

/// <summary>
/// 조립 검증: 틱 루프가 타이밍 휠의 드라이버가 된다 — 두 프리미티브의 의도된 결합 방식이다.
/// 휠은 스레드를 갖지 않고(수동), 루프의 전용 스레드가 단일 드라이버 계약을 자연히 충족한다.
/// </summary>
public sealed class TickLoopTimerWheelCompositionTests
{
    private sealed class WheelDriver : ITickHandler
    {
        private readonly TimerWheel _wheel;

        public WheelDriver(TimerWheel wheel) => _wheel = wheel;

        public void OnTick(in TickContext context) => _wheel.Advance();
    }

    private sealed class SignalingJob : ITimerJob, IDisposable
    {
        public readonly ManualResetEventSlim Fired = new();

        public void OnTimerExpired() => Fired.Set();

        public void OnTimerCanceled()
        {
        }

        public void Dispose() => Fired.Dispose();
    }

    [Fact]
    public async Task 틱_루프가_모는_휠에서_타이머가_발화한다()
    {
        var wheel = new TimerWheel(new TimerWheelOptions
        {
            TickDuration = TimeSpan.FromMilliseconds(10),
        });
        var loop = new TickLoop(new WheelDriver(wheel), new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        using var job = new SignalingJob();
        Assert.Equal(
            TimerScheduleStatus.Accepted,
            wheel.TrySchedule(job, TimeSpan.FromMilliseconds(50), out _));

        var stopwatch = Stopwatch.StartNew();
        loop.Start();
        try
        {
            Assert.True(job.Fired.Wait(TimeSpan.FromSeconds(5)), "타이머가 발화하지 않았다.");
            // 일찍 발화하지 않는다 — 해상도만큼 늦는 것은 계약이지만 이른 발화는 결함이다.
            Assert.True(stopwatch.ElapsedMilliseconds >= 40, $"너무 일찍 발화했다: {stopwatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            await loop.DisposeAsync();
            wheel.Shutdown();
        }
    }
}
