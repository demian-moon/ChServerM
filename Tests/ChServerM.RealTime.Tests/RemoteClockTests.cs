using System;
using Xunit;

namespace ChServerM.RealTime.Tests;

public sealed class RemoteClockTests
{
    [Fact]
    public void 표본이_없으면_시각을_내지_않는다()
    {
        var clock = new RemoteClock(new ManualTimeProvider());

        Assert.False(clock.HasSample);
        Assert.False(clock.TryGetNowMicros(out long micros));
        Assert.Equal(0, micros);
    }

    [Fact]
    public void 표본_이후_로컬_경과만큼_외삽한다()
    {
        var provider = new ManualTimeProvider(frequency: 1_000_000);
        var clock = new RemoteClock(provider);

        clock.Update(remoteMicros: 5_000_000);
        provider.AdvanceRaw(100_000); // 100ms

        Assert.True(clock.TryGetNowMicros(out long micros));
        Assert.Equal(5_100_000, micros);
    }

    [Fact]
    public void 순서가_뒤바뀐_과거_표본은_무시한다()
    {
        var provider = new ManualTimeProvider(frequency: 1_000_000);
        var clock = new RemoteClock(provider);

        clock.Update(remoteMicros: 5_000_000);
        clock.Update(remoteMicros: 4_000_000); // 지연 도착한 옛 패킷

        Assert.True(clock.TryGetNowMicros(out long micros));
        Assert.Equal(5_000_000, micros);
    }

    [Fact]
    public void 출력은_역행하지_않고_멈췄다가_따라잡는다()
    {
        var provider = new ManualTimeProvider(frequency: 1_000_000);
        var clock = new RemoteClock(provider);

        clock.Update(remoteMicros: 1_000_000);
        provider.AdvanceRaw(100_000);
        Assert.True(clock.TryGetNowMicros(out long first));
        Assert.Equal(1_100_000, first);

        // 오프셋 재추정이 시각을 50ms 뒤로 당겼다.
        clock.Update(remoteMicros: 1_050_000);
        Assert.True(clock.TryGetNowMicros(out long clamped));
        Assert.Equal(1_100_000, clamped); // 역행 대신 제자리

        provider.AdvanceRaw(60_000); // 실제 시각이 따라잡는다
        Assert.True(clock.TryGetNowMicros(out long resumed));
        Assert.Equal(1_110_000, resumed);
    }

    [Fact]
    public void 널_프로바이더는_거부한다()
    {
        Assert.Throws<ArgumentNullException>(() => new RemoteClock(null!));
    }
}
