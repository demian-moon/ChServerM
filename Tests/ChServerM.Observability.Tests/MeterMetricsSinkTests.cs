using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using ChServerM.Diagnostics;
using ChServerM.Observability;
using Xunit;

namespace ChServerM.Observability.Tests;

/// <summary>
/// Meter 어댑터가 <see cref="IMetricsSink"/> 호출을 BCL 계측기로 올바르게 옮기는지,
/// 태그 변환과 계측기 캐시가 동작하는지 <see cref="MeterListener"/> 로 관측해 고정한다.
/// </summary>
public sealed class MeterMetricsSinkTests : IDisposable
{
    private readonly Meter _meter = new("ChServerM.Test." + Guid.NewGuid().ToString("N"));
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _longMeasurements = [];
    private readonly List<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> _doubleMeasurements = [];
    private readonly List<Instrument> _instruments = [];

    public MeterMetricsSinkTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
            {
                lock (_instruments)
                {
                    _instruments.Add(instrument);
                }

                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_longMeasurements)
            {
                _longMeasurements.Add((instrument.Name, value, tags.ToArray()));
            }
        });

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (_doubleMeasurements)
            {
                _doubleMeasurements.Add((instrument.Name, value, tags.ToArray()));
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }

    [Fact]
    public void Count_records_to_counter_with_tags()
    {
        MeterMetricsSink sink = new(_meter);

        Span<MetricTag> tags = [new MetricTag(TagNames.ErrorCode, "2002")];
        sink.Count(MetricNames.FramesDropped, 3, tags);

        (string instrument, long value, KeyValuePair<string, object?>[] recordedTags) =
            Assert.Single(_longMeasurements);
        Assert.Equal(MetricNames.FramesDropped, instrument);
        Assert.Equal(3, value);
        KeyValuePair<string, object?> tag = Assert.Single(recordedTags);
        Assert.Equal(TagNames.ErrorCode, tag.Key);
        Assert.Equal("2002", tag.Value);
    }

    [Fact]
    public void Record_records_to_histogram()
    {
        MeterMetricsSink sink = new(_meter);

        sink.Record(MetricNames.DispatchDuration, 0.0025, ReadOnlySpan<MetricTag>.Empty);

        (string instrument, double value, _) = Assert.Single(_doubleMeasurements);
        Assert.Equal(MetricNames.DispatchDuration, instrument);
        Assert.Equal(0.0025, value, precision: 6);
    }

    [Fact]
    public void AdjustGauge_records_up_and_down()
    {
        MeterMetricsSink sink = new(_meter);

        sink.AdjustGauge(MetricNames.ConnectionsActive, 1, ReadOnlySpan<MetricTag>.Empty);
        sink.AdjustGauge(MetricNames.ConnectionsActive, -1, ReadOnlySpan<MetricTag>.Empty);

        Assert.Equal(2, _longMeasurements.Count);
        Assert.Equal(1, _longMeasurements[0].Value);
        Assert.Equal(-1, _longMeasurements[1].Value);
    }

    [Fact]
    public void Same_name_reuses_one_instrument()
    {
        // 계측기가 이름당 1회 생성되는지 — MeterListener 는 인스트루먼트별로 발행되므로,
        // 같은 이름에 두 번 Count 하면 측정은 2건이지만 인스트루먼트는 1개여야 한다.
        MeterMetricsSink sink = new(_meter);

        sink.Count(MetricNames.FramesReceived, 1, ReadOnlySpan<MetricTag>.Empty);
        sink.Count(MetricNames.FramesReceived, 1, ReadOnlySpan<MetricTag>.Empty);

        Assert.Equal(2, _longMeasurements.Count);
        Assert.All(_longMeasurements, m => Assert.Equal(MetricNames.FramesReceived, m.Instrument));
    }

    [Fact]
    public void DispatchDuration_histogram_declares_seconds_unit_and_bucket_advice()
    {
        // 감사 2026-08-18 O-3 — 단위·버킷 메타데이터가 없으면 OTel 을 Meter 구독으로 얹는
        // 순간(ADR-0020) 기본 명시 버킷(0, 5, 10, 25…)이 초 단위 값(~0.001)을 전부 첫 버킷에
        // 넣어 p50/p99 가 무의미해진다. 어댑터가 알려진 이름에 메타데이터를 붙이는 것을 고정한다.
        MeterMetricsSink sink = new(_meter);

        sink.Record(MetricNames.DispatchDuration, 0.0025, ReadOnlySpan<MetricTag>.Empty);

        Instrument instrument;
        lock (_instruments)
        {
            instrument = Assert.Single(_instruments, i => i.Name == MetricNames.DispatchDuration);
        }

        Assert.Equal("s", instrument.Unit);

        Histogram<double> histogram = Assert.IsType<Histogram<double>>(instrument);
        Assert.NotNull(histogram.Advice);
        System.Collections.Generic.IReadOnlyList<double>? buckets = histogram.Advice!.HistogramBucketBoundaries;
        Assert.NotNull(buckets);

        // 초 스케일 로그 계열(0.5ms~10s) — 첫/끝 경계가 규약이다.
        Assert.Equal(0.0005, buckets![0]);
        Assert.Equal(10.0, buckets[^1]);
    }

    [Fact]
    public void Bytes_counters_declare_ucum_By_unit()
    {
        // 감사 2026-08-18 O-3 — 바이트 카운터의 단위는 UCUM "By" 다.
        MeterMetricsSink sink = new(_meter);

        sink.Count(MetricNames.BytesSent, 128, ReadOnlySpan<MetricTag>.Empty);
        sink.Count(MetricNames.BytesReceived, 64, ReadOnlySpan<MetricTag>.Empty);
        sink.Count(MetricNames.FramesReceived, 1, ReadOnlySpan<MetricTag>.Empty);

        lock (_instruments)
        {
            Assert.Equal("By", Assert.Single(_instruments, i => i.Name == MetricNames.BytesSent).Unit);
            Assert.Equal("By", Assert.Single(_instruments, i => i.Name == MetricNames.BytesReceived).Unit);

            // 바이트가 아닌 카운터에는 단위를 붙이지 않는다 — 이름 기반 매핑의 경계.
            Assert.Null(Assert.Single(_instruments, i => i.Name == MetricNames.FramesReceived).Unit);
        }
    }

    [Fact]
    public void ObserveCounter_duplicate_registration_is_ignored()
    {
        // 감사 2026-08-18 O-10 — Observable 은 등록마다 계측기가 늘어 수집 시점에 값이
        // 중복 보고된다. 이름 캐시가 두 번째 등록을 걸러 첫 콜백만 남는 것을 고정한다.
        MeterMetricsSink sink = new(_meter);

        sink.ObserveCounter(MetricNames.PoolBuffersRented, static () => 1, ReadOnlySpan<MetricTag>.Empty);
        sink.ObserveCounter(MetricNames.PoolBuffersRented, static () => 100, ReadOnlySpan<MetricTag>.Empty);

        List<long> observed = [];
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == MetricNames.PoolBuffersRented)
            {
                lock (observed)
                {
                    observed.Add(value);
                }
            }
        });
        _listener.RecordObservableInstruments();

        long value = Assert.Single(observed);
        Assert.Equal(1, value); // 먼저 등록한 콜백이 이긴다.
    }

    [Fact]
    public void NullSink_is_a_noop()
    {
        // 관측 없는 조립의 기준선 — 던지지 않고 아무것도 기록하지 않는다.
        NullMetricsSink.Instance.Count("x", 1, ReadOnlySpan<MetricTag>.Empty);
        NullMetricsSink.Instance.Record("x", 1.0, ReadOnlySpan<MetricTag>.Empty);
        NullMetricsSink.Instance.AdjustGauge("x", 1, ReadOnlySpan<MetricTag>.Empty);

        Assert.Empty(_longMeasurements);
        Assert.Empty(_doubleMeasurements);
    }
}
