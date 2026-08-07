using System;
using System.Net;
using System.Threading;
using ChServerM.Hosting;
using ChServerM.Resilience;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 주소별 수용 제어(ADR-0026)를 검증한다 — 한 주소의 폭주가 다른 주소를 막지 않는지,
/// 주소 정규화(IPv6 프리픽스·IPv4 매핑)가 같은 주체를 하나로 묶는지, 그리고 상태가
/// 구조적으로 유계인지.
/// </summary>
public sealed class PerAddressAdmissionControlTests
{
    private static IPEndPoint V4(string address) => new(IPAddress.Parse(address), 1234);

    private static IPEndPoint V6(string address) => new(IPAddress.Parse(address), 1234);

    private static PerAddressConnectionRateAdmissionControl Create(
        ManualTimeProvider time,
        double permitsPerSecond = 5,
        int burst = 2,
        int slots = 1024,
        int ipv6Prefix = 64) =>
        new(
            new PerAddressConnectionRateOptions
            {
                PermitsPerSecond = permitsPerSecond,
                BurstCapacity = burst,
                SlotCount = slots,
                IPv6PrefixLength = ipv6Prefix,
            },
            time);

    [Fact]
    public void One_address_exhausts_its_own_budget_only()
    {
        // 이 구현의 존재 이유 — 악성 주소 하나가 전역 예산을 빨아들여 정상 사용자를
        // 함께 거부시키는 것을 막는다.
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, burst: 2);

        IPEndPoint abuser = V4("203.0.113.10");
        IPEndPoint normal = V4("198.51.100.20");

        Assert.True(control.TryAdmit(abuser).IsAdmitted);
        Assert.True(control.TryAdmit(abuser).IsAdmitted);

        // 학대자는 자기 버킷을 소진해 거부된다.
        AdmissionDecision rejected = control.TryAdmit(abuser);
        Assert.False(rejected.IsAdmitted);
        Assert.Equal("per-address connection rate exceeded", rejected.RejectionReason);

        // 다른 주소는 영향을 받지 않는다 — 전역 제한과의 결정적 차이다.
        Assert.True(control.TryAdmit(normal).IsAdmitted);
    }

    [Fact]
    public void Tokens_refill_over_time()
    {
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, permitsPerSecond: 5, burst: 1);

        IPEndPoint address = V4("203.0.113.10");

        Assert.True(control.TryAdmit(address).IsAdmitted);
        Assert.False(control.TryAdmit(address).IsAdmitted);

        // 초당 5개 → 0.2초면 1개가 찬다.
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(control.TryAdmit(address).IsAdmitted);
    }

    [Fact]
    public void Ipv6_addresses_in_same_prefix_share_one_budget()
    {
        // IPv6 는 최종 사용자에게도 /64 가 통째로 할당된다 — 주소 하나 단위로 제한하면
        // 공격자에게 2^64 개의 우회로를 주는 셈이다.
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, burst: 2, ipv6Prefix: 64);

        Assert.True(control.TryAdmit(V6("2001:db8:1:2::1")).IsAdmitted);
        Assert.True(control.TryAdmit(V6("2001:db8:1:2::2")).IsAdmitted);

        // 같은 /64 라 예산을 공유한다 — 주소만 바꾼 우회가 통하지 않는다.
        Assert.False(control.TryAdmit(V6("2001:db8:1:2::ffff")).IsAdmitted);
    }

    [Fact]
    public void Ipv6_addresses_in_different_prefixes_are_independent()
    {
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, burst: 1, ipv6Prefix: 64);

        Assert.True(control.TryAdmit(V6("2001:db8:1:2::1")).IsAdmitted);
        Assert.False(control.TryAdmit(V6("2001:db8:1:2::9")).IsAdmitted);

        // 다른 /64 는 별개 주체다.
        Assert.True(control.TryAdmit(V6("2001:db8:1:3::1")).IsAdmitted);
    }

    [Fact]
    public void Ipv4_mapped_ipv6_counts_as_the_same_ipv4_subject()
    {
        // 듀얼스택 소켓은 IPv4 클라이언트를 ::ffff:a.b.c.d 로 넘긴다. 되돌리지 않으면
        // 같은 클라이언트가 두 주체로 갈려 제한이 두 배로 느슨해진다.
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, burst: 1);

        Assert.True(control.TryAdmit(V4("203.0.113.10")).IsAdmitted);
        Assert.False(control.TryAdmit(V6("::ffff:203.0.113.10")).IsAdmitted);
    }

    [Fact]
    public void NonIpEndPoint_is_admitted_without_judgement()
    {
        // 주소를 모르면 이 규칙은 할 말이 없다 — 거부가 아니라 통과다(총량은 전역 규칙의 몫).
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, burst: 1);

        Assert.True(control.TryAdmit(null).IsAdmitted);
        Assert.True(control.TryAdmit(new TestEndPoint()).IsAdmitted);
        Assert.True(control.TryAdmit(new TestEndPoint()).IsAdmitted);
    }

    [Fact]
    public void State_is_bounded_by_slot_count_regardless_of_address_count()
    {
        // 이 설계의 핵심 — 방어 장치가 OOM 벡터가 되지 않는다. 슬롯 수보다 훨씬 많은
        // 고유 주소를 흘려도 상태가 자라지 않는다(고정 배열이라 커질 수가 없다).
        ManualTimeProvider time = new();
        PerAddressConnectionRateAdmissionControl control = Create(time, permitsPerSecond: 1000, burst: 1000, slots: 64);

        long before = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 20_000; i++)
        {
            control.TryAdmit(new IPEndPoint(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), 1));
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);

        // 주소 2만 개를 흘려도 증가분이 미미하다(엔트리당 할당이 존재하지 않는다).
        // 여유 있게 1MB 로 잡는다 — 맵이 자랐다면 수 MB 단위로 벌어졌을 규모다.
        Assert.True(after - before < 1_000_000, $"주소별 상태가 자랐다: {after - before} 바이트");
    }

    [Fact]
    public void Composite_ands_global_and_per_address_rules()
    {
        // 의도된 사용법 — 전역이 총량을, 주소별이 개별 학대자를 막는다.
        ManualTimeProvider time = new();
        CompositeAdmissionControl composite = new(
            new ConnectionRateAdmissionControl(
                new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 100, BurstCapacity = 100 }, time),
            Create(time, burst: 1));

        Assert.True(composite.TryAdmit(V4("203.0.113.10")).IsAdmitted);

        // 전역 예산은 남았지만 주소별 예산이 없으면 거부된다.
        Assert.False(composite.TryAdmit(V4("203.0.113.10")).IsAdmitted);

        // 다른 주소는 여전히 통과 — 전역이 아직 여유롭기 때문이다.
        Assert.True(composite.TryAdmit(V4("198.51.100.20")).IsAdmitted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PerAddressConnectionRateOptions.AbsoluteMaxSlotCount + 1)]
    public void Invalid_slot_count_throws(int slotCount)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PerAddressConnectionRateAdmissionControl(
                new PerAddressConnectionRateOptions { SlotCount = slotCount }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Invalid_ipv6_prefix_throws(int prefix)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PerAddressConnectionRateAdmissionControl(
                new PerAddressConnectionRateOptions { IPv6PrefixLength = prefix }));
    }

    /// <summary>IP 가 아닌 종단 — 인메모리 전송처럼 주소가 없는 경우를 흉내낸다.</summary>
    private sealed class TestEndPoint : EndPoint
    {
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
