using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using ChServerM.Buffers;
using ChServerM.Diagnostics;
using ChServerM.Observability;
using Xunit;

namespace ChServerM.Observability.Tests;

/// <summary>
/// 풀 카운터의 pull 배선(ADR-0025)을 검증한다 — 등록된 관측 콜백이 수집 시점에
/// <see cref="BufferPoolDiagnostics"/> 의 현재 값을 읽어가는지.
/// </summary>
/// <remarks>
/// <see cref="BufferPoolDiagnostics"/> 는 <b>프로세스 전역 정적</b>이라 절대값을 단언하지 않는다
/// (다른 테스트의 대여가 섞인다). 대신 "관측값이 그 시점의 정적 카운터와 같다"와
/// "대여 후 다시 수집하면 증가분이 반영된다"를 본다 — pull 의 정의 그대로다.
/// </remarks>
public sealed class BufferPoolMetricsTests
{
    private static Dictionary<string, long> Collect(MeterListener listener)
    {
        Dictionary<string, long> measured = new(StringComparer.Ordinal);
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measured[instrument.Name] = value);
        listener.RecordObservableInstruments();
        return measured;
    }

    [Fact]
    public void Register_ObservesCurrentPoolCounters()
    {
        using Meter meter = new("ChServerM.PoolTest." + Guid.NewGuid().ToString("N"));
        using MeterMetricsSink sink = new(meter);

        BufferPoolMetrics.Register(sink);

        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        long rentedBefore = BufferPoolDiagnostics.RentedBuffers;
        Dictionary<string, long> measured = Collect(listener);

        // 세 카운터가 전부 발행되고, 관측값이 그 시점의 정적 카운터와 일치한다.
        Assert.True(measured.ContainsKey(MetricNames.PoolBuffersRented), "대여 카운터가 발행되지 않았다.");
        Assert.True(measured.ContainsKey(MetricNames.PoolBuffersReturned), "반납 카운터가 발행되지 않았다.");
        Assert.True(measured.ContainsKey(MetricNames.PoolBuffersLeaked), "누수 카운터가 발행되지 않았다.");
        Assert.True(measured[MetricNames.PoolBuffersRented] >= rentedBefore);
    }

    [Fact]
    public void Observation_ReflectsRentsMadeAfterRegistration()
    {
        // pull 의 핵심 — 등록 이후에 일어난 대여가 다음 수집에 반영된다(push 였다면 등록
        // 시점 스냅샷에 머물렀을 것이다).
        using Meter meter = new("ChServerM.PoolTest." + Guid.NewGuid().ToString("N"));
        using MeterMetricsSink sink = new(meter);

        BufferPoolMetrics.Register(sink);

        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        long before = Collect(listener)[MetricNames.PoolBuffersRented];

        // 실제로 풀에서 빌리고 반납한다.
        using (PooledBufferWriter writer = new(256))
        {
            writer.GetSpan(64);
        }

        long after = Collect(listener)[MetricNames.PoolBuffersRented];

        Assert.True(after > before, $"대여 후 관측값이 늘지 않았다: {before} → {after}");
    }

    [Fact]
    public void SinkWithoutPullSupport_IgnoresRegistration()
    {
        // 기본 구현(무동작)을 쓰는 싱크는 등록을 조용히 무시한다 — 오류가 아니다.
        BufferPoolMetrics.Register(NullMetricsSink.Instance);
    }
}
