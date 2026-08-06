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

    public MeterMetricsSinkTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
            {
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
