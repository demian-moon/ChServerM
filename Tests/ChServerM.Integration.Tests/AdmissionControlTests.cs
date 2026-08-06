using System;
using System.Net;
using System.Threading;
using ChServerM.Hosting;
using ChServerM.Resilience;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 수용 제어 구현의 단위 계약 — 토큰 버킷의 버스트·리필·거부와 컴포지트의 AND·단락을
/// 가짜 시계로 결정적으로 고정한다.
/// </summary>
public sealed class AdmissionControlTests
{
    [Fact]
    public void TokenBucket_admits_burst_then_rejects_when_empty()
    {
        ManualTimeProvider time = new();
        ConnectionRateAdmissionControl control = new(
            new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 10, BurstCapacity = 3 }, time);

        // 버킷은 가득 차서 시작한다 — 버스트 용량(3)만큼 즉시 수용.
        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.True(control.TryAdmit(null).IsAdmitted);

        // 4번째는 토큰이 없어 거부.
        AdmissionDecision rejected = control.TryAdmit(null);
        Assert.False(rejected.IsAdmitted);
        Assert.NotNull(rejected.RejectionReason);
    }

    [Fact]
    public void TokenBucket_refills_over_time()
    {
        ManualTimeProvider time = new();
        ConnectionRateAdmissionControl control = new(
            new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 10, BurstCapacity = 2 }, time);

        // 버스트 소진.
        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.False(control.TryAdmit(null).IsAdmitted);

        // 0.1초 경과 = 10 permits/s × 0.1 = 1 토큰 충전 → 정확히 하나 수용.
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.False(control.TryAdmit(null).IsAdmitted);
    }

    [Fact]
    public void TokenBucket_refill_is_capped_at_burst()
    {
        ManualTimeProvider time = new();
        ConnectionRateAdmissionControl control = new(
            new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 10, BurstCapacity = 2 }, time);

        // 오래 놀아도 버킷은 버스트 용량을 넘지 않는다 — 유휴 후 무제한 폭주를 막는다.
        time.Advance(TimeSpan.FromHours(1));

        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.False(control.TryAdmit(null).IsAdmitted); // 2개까지만
    }

    [Fact]
    public void TokenBucket_rejects_invalid_options()
    {
        Assert.Throws<InvalidOperationException>(
            static () => new ConnectionRateAdmissionControl(
                new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 0 }));
        Assert.Throws<InvalidOperationException>(
            static () => new ConnectionRateAdmissionControl(
                new ConnectionRateAdmissionControlOptions { BurstCapacity = 0 }));
    }

    [Fact]
    public void Composite_rejects_if_any_rejects_and_short_circuits()
    {
        CountingAdmission first = new(admit: true);
        CountingAdmission gate = new(admit: false);
        CountingAdmission last = new(admit: true);
        CompositeAdmissionControl composite = new(first, gate, last);

        AdmissionDecision decision = composite.TryAdmit(null);

        Assert.False(decision.IsAdmitted);
        // 첫 거부(gate)에서 멈춘다 — last 는 호출되지 않아 소비형 부수효과가 없다.
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, gate.CallCount);
        Assert.Equal(0, last.CallCount);
    }

    [Fact]
    public void Composite_admits_when_all_admit()
    {
        CountingAdmission a = new(admit: true);
        CountingAdmission b = new(admit: true);
        CompositeAdmissionControl composite = new(a, b);

        Assert.True(composite.TryAdmit(null).IsAdmitted);
        Assert.Equal(1, a.CallCount);
        Assert.Equal(1, b.CallCount);
    }

    [Fact]
    public void Composite_rejects_empty_rule_set()
    {
        Assert.Throws<ArgumentException>(static () => new CompositeAdmissionControl());
    }

    private sealed class CountingAdmission(bool admit) : IAdmissionControl
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint)
        {
            Interlocked.Increment(ref _callCount);
            return admit ? AdmissionDecision.Admit() : AdmissionDecision.Reject("test");
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
