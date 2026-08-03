using System;
using ChServerM.Time;
using Xunit;

namespace ChServerM.Core.Tests.Time;

/// <summary>
/// 레거시는 시간 표현이 3종이었고 <c>Frequency / 1000</c> 정수 나눗셈에서 오차가 났다.
/// 여기서는 (1) 주파수가 달라져도 결과가 같은가, (2) 벽시계 역행에 영향을 받지 않는가를 본다.
/// </summary>
public sealed class MonotonicTimestampTests
{
    [Fact]
    public void ElapsedSince_MeasuresAdvancedTime()
    {
        ManualTimeProvider time = new();
        MonotonicTimestamp start = MonotonicTimestamp.Now(time);

        time.AdvanceMonotonic(TimeSpan.FromMilliseconds(250));

        Assert.Equal(TimeSpan.FromMilliseconds(250), start.ElapsedSince(time));
    }

    [Theory]
    [InlineData(1_000L)]           // 저해상도
    [InlineData(1_000_000L)]       // 마이크로초
    [InlineData(10_000_000L)]      // Windows 기본
    [InlineData(1_000_000_000L)]   // 나노초
    public void ElapsedSince_IsFrequencyIndependent(long frequency)
    {
        // 주파수를 바꿔도 같은 TimeSpan 이 나와야 한다.
        // 나눗셈이 public API 로 새어나오지 않는다는 뜻이다.
        ManualTimeProvider time = new(frequency);
        MonotonicTimestamp start = MonotonicTimestamp.Now(time);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), start.ElapsedSince(time));
    }

    [Fact]
    public void ElapsedSince_IsUnaffectedByWallClockGoingBackwards()
    {
        // 이 타입이 존재하는 이유. NTP 보정으로 벽시계가 뒤로 가도
        // 타임아웃 계산은 흔들리면 안 된다.
        ManualTimeProvider time = new();
        MonotonicTimestamp start = MonotonicTimestamp.Now(time);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        time.SetUtcNow(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromSeconds(10), start.ElapsedSince(time));
    }

    [Fact]
    public void ElapsedTo_ReturnsNegative_WhenLaterIsEarlier()
    {
        // 음수를 0 으로 뭉개지 않는다. 단조 시각의 음수 경과는 버그 신호다.
        ManualTimeProvider time = new();
        MonotonicTimestamp first = MonotonicTimestamp.Now(time);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        MonotonicTimestamp second = MonotonicTimestamp.Now(time);

        Assert.True(second.ElapsedTo(time, first) < TimeSpan.Zero);
    }

    [Fact]
    public void Add_ProducesDeadlineThatHasPassedAfterAdvancing()
    {
        ManualTimeProvider time = new();
        MonotonicTimestamp deadline = MonotonicTimestamp.Now(time).Add(time, TimeSpan.FromSeconds(5));

        time.AdvanceMonotonic(TimeSpan.FromSeconds(4));
        Assert.False(MonotonicTimestamp.Now(time).HasPassed(deadline));

        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.True(MonotonicTimestamp.Now(time).HasPassed(deadline));
    }

    [Fact]
    public void Comparison_FollowsChronologicalOrder()
    {
        ManualTimeProvider time = new();
        MonotonicTimestamp earlier = MonotonicTimestamp.Now(time);

        time.AdvanceMonotonic(TimeSpan.FromTicks(1000));
        MonotonicTimestamp later = MonotonicTimestamp.Now(time);

        MonotonicTimestamp sameAsEarlier = MonotonicTimestamp.FromRaw(earlier.Raw);

        Assert.True(earlier < later);
        Assert.True(later > earlier);
        Assert.True(earlier <= sameAsEarlier);
        Assert.True(earlier >= sameAsEarlier);
        Assert.Equal(-1, earlier.CompareTo(later));
    }

    [Fact]
    public void None_IsDefault()
    {
        Assert.True(MonotonicTimestamp.None.IsNone);
        Assert.True(default(MonotonicTimestamp).IsNone);
    }

    [Fact]
    public void FromRaw_RoundTrips()
    {
        Assert.Equal(1234567L, MonotonicTimestamp.FromRaw(1234567L).Raw);
    }

    [Fact]
    public void Now_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MonotonicTimestamp.Now(null!));
    }
}
