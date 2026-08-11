using System;
using Xunit;

namespace ChServerM.RealTime.Tests;

public sealed class TimeSyncExchangeTests
{
    [Fact]
    public void 상대_처리_시간이_왕복에서_빠진다()
    {
        // t1=0 송신, t2=150 수신, (처리 100), t3=250 응답, t4=200 수신.
        // 왕복 = (200-0) - (250-150) = 100. 처리 시간이 섞이면 200이 나온다 — 레거시 2-타임스탬프의 오차.
        var result = TimeSyncExchange.Compute(0, 150, 250, 200);

        Assert.Equal(100, result.RoundTripMicros);
    }

    [Fact]
    public void 대칭_경로에서_오프셋이_정확하다()
    {
        // 실제 시계 차 +100, 편도 50/50 대칭: t1=0 → t2=150, t3=250 → t4=200.
        var result = TimeSyncExchange.Compute(0, 150, 250, 200);

        Assert.Equal(100, result.OffsetMicros);
    }

    [Fact]
    public void 시계_차가_없으면_오프셋은_0이다()
    {
        var result = TimeSyncExchange.Compute(0, 50, 60, 110);

        Assert.Equal(0, result.OffsetMicros);
        Assert.Equal(100, result.RoundTripMicros);
    }

    [Fact]
    public void 요청자_시계_역행은_인자_오류다()
    {
        Assert.Throws<ArgumentException>(() => TimeSyncExchange.Compute(200, 150, 250, 100));
    }

    [Fact]
    public void 응답자_시계_역행은_인자_오류다()
    {
        Assert.Throws<ArgumentException>(() => TimeSyncExchange.Compute(0, 250, 150, 200));
    }
}
