using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ChServerM.Diagnostics;

namespace ChServerM.Observability;

/// <summary>
/// <see cref="IMetricsSink"/>의 <see cref="Meter"/>(BCL) 어댑터 (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <c>System.Diagnostics.Metrics</c> 는 .NET 표준 계측 API 다 —
/// <c>dotnet-counters</c>/<c>dotnet-monitor</c> 가 즉시 읽고, OpenTelemetry 메트릭 SDK 는
/// 이 <see cref="Meter"/> 를 <b>구독</b>하는 얇은 설정 계층이라 나중에 OTel 을 얹어도
/// 재계측이 아니라 배선만 추가하면 된다(ADR-0020). 새 패키지 의존이 없는 것도 이 선택의 몫이다.
/// </para>
/// <para>
/// <b>계측기는 이름당 1회 생성해 캐시한다.</b> <see cref="Meter.CreateCounter{T}(string, string?, string?)"/>
/// 는 호출마다 새 인스턴스를 만들 수 있으므로, 프레임마다 만들면 그 자체가 할당이다.
/// 이름→계측기 맵을 <see cref="ConcurrentDictionary{TKey,TValue}"/> 로 캐시한다 —
/// 계측기 집합은 유한(수십 개)하고 시작 후 안정되므로 맵 성장은 곧 멈춘다.
/// </para>
/// <para>
/// <b>리스너가 없으면 거의 무비용이다.</b> BCL 의 <c>Counter.Add</c>/<c>Histogram.Record</c>
/// 는 구독자가 없을 때 내부 <c>Enabled</c> 검사만 하고 빠진다 — 관측을 켜되 익스포터를
/// 붙이지 않은 조립의 오버헤드가 낮다(Phase 11 게이트 벤치가 이를 확인한다).
/// </para>
/// <para>
/// <b>태그 변환.</b> <see cref="MetricTag"/> 스팬을 BCL <see cref="TagList"/>(struct, 무할당
/// 최대 8태그)로 옮긴다. 프레임워크 메트릭은 태그가 0~2개라 <see cref="TagList"/> 의
/// 인라인 저장 범위 안이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Meter"/> 와 계측기는 스레드 안전하다. 캐시도
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 라 안전하다. <see cref="Dispose"/> 는
/// 조립을 소유한 쪽이 서버 종료 시 1회 호출한다.
/// </para>
/// </remarks>
public sealed class MeterMetricsSink : IMetricsSink, IDisposable
{
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, Counter<long>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UpDownCounter<long>> _gauges = new(StringComparer.Ordinal);

    /// <summary>기본 <see cref="Meter"/> 이름(<see cref="DiagnosticNames.MeterName"/>)으로 만든다.</summary>
    public MeterMetricsSink()
        : this(new Meter(DiagnosticNames.MeterName))
    {
    }

    /// <summary><see cref="Meter"/> 를 지정해 만든다.</summary>
    /// <param name="meter">계측기를 만들 미터. 이 인스턴스가 소유권을 가져간다(<see cref="Dispose"/>가 폐기).</param>
    /// <exception cref="ArgumentNullException"><paramref name="meter"/>가 <see langword="null"/>일 때.</exception>
    public MeterMetricsSink(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
    }

    /// <inheritdoc />
    public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
    {
        Counter<long> counter = _counters.GetOrAdd(name, static (n, m) => m.CreateCounter<long>(n), _meter);
        counter.Add(delta, ToTagList(tags));
    }

    /// <inheritdoc />
    public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
    {
        Histogram<double> histogram = _histograms.GetOrAdd(name, static (n, m) => m.CreateHistogram<double>(n), _meter);
        histogram.Record(value, ToTagList(tags));
    }

    /// <inheritdoc />
    public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
    {
        UpDownCounter<long> gauge = _gauges.GetOrAdd(name, static (n, m) => m.CreateUpDownCounter<long>(n), _meter);
        gauge.Add(delta, ToTagList(tags));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    /// <summary>중립 태그 스팬을 BCL <see cref="TagList"/>(무할당 struct)로 옮긴다.</summary>
    private static TagList ToTagList(ReadOnlySpan<MetricTag> tags)
    {
        TagList list = default;
        foreach (MetricTag tag in tags)
        {
            list.Add(tag.Name, tag.Value);
        }

        return list;
    }
}
