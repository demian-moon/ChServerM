using System;
using Xunit;

namespace ChServerM.RealTime.Tests;

public sealed class MicrosecondClockTests
{
    [Fact]
    public void 생성_직후에는_0이다()
    {
        var provider = new ManualTimeProvider();
        var clock = new MicrosecondClock(provider);

        Assert.Equal(0, clock.CurrentMicros());
    }

    [Fact]
    public void 경과가_마이크로초로_정확히_변환된다()
    {
        var provider = new ManualTimeProvider(frequency: 1_000_000); // 1 raw = 1µs
        var clock = new MicrosecondClock(provider);

        provider.AdvanceRaw(123_456_789);

        Assert.Equal(123_456_789, clock.CurrentMicros());
    }

    [Fact]
    public void 천의_배수가_아닌_주파수에서도_오차가_없다()
    {
        // 레거시 결함 재현 조건: Frequency/1000 정수 나눗셈은 3,579,545Hz 에서
        // 0.015% 편차(30일 타이머 6.5분 오차)를 만들었다.
        const long frequency = 3_579_545;
        var provider = new ManualTimeProvider(frequency);
        var clock = new MicrosecondClock(provider);

        provider.AdvanceRaw(frequency * 60 * 60 * 24 * 30); // 정확히 30일

        Assert.Equal(30L * 24 * 60 * 60 * 1_000_000, clock.CurrentMicros());
    }

    [Fact]
    public void 일년_가동에도_밀리초가_뭉개지지_않는다()
    {
        // 레거시 GTickMs 는 double 경유라 10MHz × 1년 ≈ 3.15×10¹⁴ 에서 이미 위험 구간이었고
        // 수년 가동이면 2⁵³ 을 넘는다. 정수 경로는 정확해야 한다.
        const long frequency = 10_000_000;
        var provider = new ManualTimeProvider(frequency);
        var clock = new MicrosecondClock(provider);

        const long oneYearSeconds = 365L * 24 * 60 * 60;
        provider.AdvanceRaw(oneYearSeconds * frequency);

        Assert.Equal(oneYearSeconds * 1_000_000, clock.CurrentMicros());
    }

    [Fact]
    public void 널_프로바이더는_거부한다()
    {
        Assert.Throws<ArgumentNullException>(() => new MicrosecondClock(null!));
    }
}
