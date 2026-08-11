using System;
using Xunit;

namespace ChServerM.RealTime.Tests;

public sealed class RttEstimatorTests
{
    [Fact]
    public void 표본이_없으면_추정하지_않는다()
    {
        var estimator = new RttEstimator();

        Assert.False(estimator.TryGetSmoothedRtt(out TimeSpan smoothed));
        Assert.Equal(TimeSpan.Zero, smoothed);
        Assert.False(estimator.TryGetOneWayDelay(out _));
    }

    [Fact]
    public void 소수_표본은_단순_평균이다()
    {
        var estimator = new RttEstimator();
        estimator.AddSample(TimeSpan.FromMilliseconds(10));
        estimator.AddSample(TimeSpan.FromMilliseconds(20));

        Assert.True(estimator.TryGetSmoothedRtt(out TimeSpan smoothed));
        Assert.Equal(TimeSpan.FromMilliseconds(15), smoothed);
    }

    [Fact]
    public void 스파이크는_IQR로_제거된다()
    {
        // 네트워크 지연의 전형: 안정 10ms 에 500ms 스파이크 하나.
        // 단순 평균이면 ≈54.5ms — 레거시가 IQR 을 도입한 이유이며 그 발상을 승계한다.
        var estimator = new RttEstimator();
        for (int i = 0; i < 10; i++)
        {
            estimator.AddSample(TimeSpan.FromMilliseconds(10));
        }

        estimator.AddSample(TimeSpan.FromMilliseconds(500));

        Assert.True(estimator.TryGetSmoothedRtt(out TimeSpan smoothed));
        Assert.Equal(TimeSpan.FromMilliseconds(10), smoothed);
    }

    [Fact]
    public void 편도는_왕복의_절반이다()
    {
        var estimator = new RttEstimator();
        estimator.AddSample(TimeSpan.FromMilliseconds(30));

        Assert.True(estimator.TryGetOneWayDelay(out TimeSpan delay));
        Assert.Equal(TimeSpan.FromMilliseconds(15), delay);
    }

    [Fact]
    public void 창이_차면_가장_오래된_표본이_밀려난다()
    {
        var estimator = new RttEstimator(windowSize: 4);
        estimator.AddSample(TimeSpan.FromMilliseconds(1000)); // 밀려날 표본
        for (int i = 0; i < 4; i++)
        {
            estimator.AddSample(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(4, estimator.SampleCount);
        Assert.True(estimator.TryGetSmoothedRtt(out TimeSpan smoothed));
        Assert.Equal(TimeSpan.FromMilliseconds(10), smoothed);
    }

    [Fact]
    public void 음수_표본은_측정_버그의_신호다()
    {
        var estimator = new RttEstimator();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => estimator.AddSample(TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4097)]
    public void 창_크기는_범위를_검증한다(int windowSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RttEstimator(windowSize));
    }
}
