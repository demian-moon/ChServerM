using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChServerM.RealTime.Tests;

/// <summary>
/// 틱 루프 테스트. 루프는 실제 스레드·실제 슬립으로 돌므로 가짜 시계로 결정화할 수 없다 —
/// 대신 짧은 간격 + 느슨한 불변식(개수 하한·단조성)으로 검증한다. CI 의 스케줄링 지연에도
/// 깨지지 않도록 정확한 시간 일치는 단정하지 않는다.
/// </summary>
public sealed class TickLoopTests
{
    private sealed class CountingHandler : ITickHandler
    {
        private readonly Action<TickContext>? _onTick;
        public readonly List<long> TickNumbers = [];
        public readonly List<long> ScheduledRaw = [];

        public CountingHandler(Action<TickContext>? onTick = null) => _onTick = onTick;

        public void OnTick(in TickContext context)
        {
            TickNumbers.Add(context.TickNumber);
            ScheduledRaw.Add(context.ScheduledAt.Raw);
            _onTick?.Invoke(context);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.ElapsedMilliseconds < timeoutMs, "조건이 제한 시간 안에 충족되지 않았다.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 틱이_돌고_정지가_깨끗하게_끝난다()
    {
        var handler = new CountingHandler();
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        loop.Start();
        Assert.True(loop.IsRunning);
        await WaitUntilAsync(() => loop.Statistics.TotalTicks >= 5);
        await loop.DisposeAsync();

        Assert.False(loop.IsRunning);
        long total = loop.Statistics.TotalTicks;
        Assert.True(total >= 5);

        // 정지 후에는 더 돌지 않는다.
        await Task.Delay(50);
        Assert.Equal(total, loop.Statistics.TotalTicks);
    }

    [Fact]
    public async Task 틱_번호와_예정_시각은_단조_증가한다()
    {
        var handler = new CountingHandler();
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        loop.Start();
        await WaitUntilAsync(() => loop.Statistics.TotalTicks >= 10);
        await loop.DisposeAsync();

        for (int i = 1; i < handler.TickNumbers.Count; i++)
        {
            Assert.True(handler.TickNumbers[i] > handler.TickNumbers[i - 1], "틱 번호가 역행했다.");
            Assert.True(handler.ScheduledRaw[i] > handler.ScheduledRaw[i - 1], "예정 시각이 역행했다.");
        }
    }

    [Fact]
    public async Task 핸들러_예외는_틱_단위로_격리된다()
    {
        int calls = 0;
        var handler = new CountingHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                throw new InvalidOperationException("고의 실패");
            }
        });
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        loop.Start();
        await WaitUntilAsync(() => loop.Statistics.TotalTicks >= 5);
        await loop.DisposeAsync();

        TickLoopStatistics stats = loop.Statistics;
        Assert.Equal(2, stats.FaultedTicks);
        Assert.True(stats.TotalTicks >= 5, "예외 이후에도 루프가 계속 돌아야 한다.");
    }

    [Fact]
    public async Task 예산_초과와_건너뜀이_관측된다()
    {
        // 첫 틱이 간격의 수십 배를 소모한다 → 초과 1회 이상 + 캐치업 상한(1) 너머는 건너뜀.
        int calls = 0;
        var handler = new CountingHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Thread.Sleep(200);
            }
        });
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            MaxCatchUpTicks = 1,
        });

        loop.Start();
        await WaitUntilAsync(() => loop.Statistics.TotalTicks >= 5);
        await loop.DisposeAsync();

        TickLoopStatistics stats = loop.Statistics;
        Assert.True(stats.OverrunTicks >= 1, "예산 초과가 관측되지 않았다.");
        Assert.True(stats.SkippedTicks >= 1, "건너뜀이 관측되지 않았다.");
        Assert.True(stats.MaxTickDuration >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task 건너뛴_뒤의_예정_시각은_현재로_재정렬된다()
    {
        // 캐치업 0: 밀린 틱은 전부 건너뛴다. 다음 틱 번호가 점프해야 한다.
        int calls = 0;
        var handler = new CountingHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Thread.Sleep(100);
            }
        });
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            MaxCatchUpTicks = 0,
        });

        loop.Start();
        await WaitUntilAsync(() => loop.Statistics.TotalTicks >= 3);
        await loop.DisposeAsync();

        Assert.True(handler.TickNumbers[1] - handler.TickNumbers[0] > 1, "밀린 틱 번호가 소비되지 않았다.");
    }

    [Fact]
    public async Task 두_번_시작할_수_없다()
    {
        var loop = new TickLoop(new CountingHandler(), new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        loop.Start();
        Assert.Throws<InvalidOperationException>(loop.Start);
        await loop.DisposeAsync();
        Assert.Throws<InvalidOperationException>(loop.Start);
    }

    [Fact]
    public async Task 시작하지_않고_폐기해도_된다()
    {
        var loop = new TickLoop(new CountingHandler(), new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        await loop.DisposeAsync();
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public async Task 폐기는_여러_번_불러도_된다()
    {
        var loop = new TickLoop(new CountingHandler(), new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        loop.Start();
        await loop.DisposeAsync();
        await loop.DisposeAsync();
    }

    [Fact]
    public async Task 정시_루프는_캐치업_0에서도_틱을_건너뛰지_않는다()
    {
        // 회귀(감사 2026-08-18 R-1): 음수 밀림이 정수 절단 +1 로 1이 되어
        // MaxCatchUpTicks=0 구성에서 정시 루프가 매 반복 틱 하나를 건너뛰었다(실효 주파수 절반).
        var handler = new CountingHandler();
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(20),
            MaxCatchUpTicks = 0,
        });

        loop.Start();
        await WaitUntilAsync(() => loop.Statistics.TotalTicks >= 5);
        await loop.DisposeAsync();

        // CI 스케줄링 지연으로 진짜 밀림이 몇 번 생길 수는 있다. 그러나 버그에서는
        // 연속 실행(번호 차이 1)이 단 하나도 없고, 건너뜀 수가 실행 수와 같은 규모가 된다.
        var deltas = new List<long>();
        for (int i = 1; i < handler.TickNumbers.Count; i++)
        {
            deltas.Add(handler.TickNumbers[i] - handler.TickNumbers[i - 1]);
        }

        Assert.Contains(1L, deltas);
        Assert.True(
            loop.Statistics.SkippedTicks < loop.Statistics.TotalTicks,
            "정시 루프에서 건너뜀이 실행 수만큼 발생했다 — 밀림 계산이 잘못됐다.");
    }

    [Fact]
    public async Task 동시_폐기도_실행_중인_틱의_완주를_기다린다()
    {
        // 회귀(감사 2026-08-18 R-2): 두 번째 DisposeAsync 가 "시작한 적 없음" 분기로 빠져
        // _stopped 를 조기 완료시키면, 첫 번째 대기자가 마지막 틱 실행 중에 반환된다.
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        bool tickCompleted = false;
        var handler = new CountingHandler(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            Volatile.Write(ref tickCompleted, true);
        });
        var loop = new TickLoop(handler, new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
        });

        loop.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "틱이 시작되지 않았다.");

        Task first = loop.DisposeAsync().AsTask();
        Task second = loop.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.False(first.IsCompleted, "틱이 실행 중인데 첫 번째 폐기가 끝났다.");
        Assert.False(second.IsCompleted, "틱이 실행 중인데 두 번째 폐기가 끝났다.");

        release.Set();
        await Task.WhenAll(first, second);
        Assert.True(Volatile.Read(ref tickCompleted), "폐기가 틱 완주 전에 반환됐다.");
    }

    [Theory]
    [InlineData(0)]     // 간격 0
    [InlineData(-10)]   // 음수
    public void 간격을_검증한다(int intervalMs)
    {
        var options = new TickLoopOptions { TickInterval = TimeSpan.FromMilliseconds(intervalMs) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void 스핀_구간이_간격보다_길면_거부한다()
    {
        var options = new TickLoopOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            SpinWaitWindow = TimeSpan.FromMilliseconds(20),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void 음수_캐치업은_거부한다()
    {
        var options = new TickLoopOptions { MaxCatchUpTicks = -1 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
