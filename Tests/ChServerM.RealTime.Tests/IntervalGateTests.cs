using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChServerM.RealTime.Tests;

public sealed class IntervalGateTests
{
    [Fact]
    public void 첫_호출은_항상_통과한다()
    {
        var provider = new ManualTimeProvider();
        var gate = new IntervalGate(TimeSpan.FromSeconds(1), provider);

        Assert.True(gate.TryConsume());
    }

    [Fact]
    public void 간격_안의_재호출은_거부된다()
    {
        var provider = new ManualTimeProvider();
        var gate = new IntervalGate(TimeSpan.FromSeconds(1), provider);

        Assert.True(gate.TryConsume());
        provider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(gate.TryConsume());
    }

    [Fact]
    public void 간격이_지나면_다시_통과한다()
    {
        var provider = new ManualTimeProvider();
        var gate = new IntervalGate(TimeSpan.FromSeconds(1), provider);

        Assert.True(gate.TryConsume());
        provider.Advance(TimeSpan.FromSeconds(1));
        Assert.True(gate.TryConsume());
        Assert.False(gate.TryConsume());
    }

    [Fact]
    public async Task 동시에_불러도_인터벌당_하나만_통과한다()
    {
        // 레거시 ElapsedTimeManM 의 결함 재현 조건: 검사와 갱신이 분리돼 있으면
        // 동시 호출이 전부 통과해 중복 실행된다. 원자화된 게이트는 정확히 하나만 통과한다.
        var provider = new ManualTimeProvider();
        var gate = new IntervalGate(TimeSpan.FromSeconds(1), provider);

        const int threads = 16;
        using var barrier = new Barrier(threads);
        int passed = 0;

        var tasks = new Task[threads];
        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                if (gate.TryConsume())
                {
                    Interlocked.Increment(ref passed);
                }
            });
        }

        await Task.WhenAll(tasks);
        Assert.Equal(1, passed);
    }

    [Fact]
    public void 간격은_양수여야_한다()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IntervalGate(TimeSpan.Zero, new ManualTimeProvider()));
    }

    [Fact]
    public void 널_프로바이더는_거부한다()
    {
        Assert.Throws<ArgumentNullException>(() => new IntervalGate(TimeSpan.FromSeconds(1), null!));
    }
}
