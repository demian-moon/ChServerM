using System;
using ChServerM.Buffers;
using ChServerM.Diagnostics;

namespace ChServerM.Observability;

/// <summary>
/// 버퍼 풀 카운터(<see cref="BufferPoolDiagnostics"/>)를 메트릭 싱크에 배선한다 (Phase 11 관측, ADR-0025).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 풀은 자기 카운터를 메트릭으로 내보낼 수 없다.</b>
/// <c>ChServerM.Buffers</c> 는 "Core 조차 참조하지 않는다"가 의도된 결정이라(그 csproj 주석)
/// <see cref="IMetricsSink"/> 를 볼 수 없다. 그래서 <b>관측 배선을 관측 어셈블리가 가져간다</b> —
/// 풀은 카운터를 노출만 하고, 그것을 메트릭 이름 계약에 잇는 책임은 여기다.
/// </para>
/// <para>
/// <b>pull 로 잇는다.</b> 대여·반납은 프레임워크에서 가장 뜨거운 경로 중 하나라, 그때마다
/// 메트릭을 push 하면 관측이 곧 비용이 된다. 카운터는 <b>이미 세어지고 있으므로</b>
/// <see cref="IMetricsSink.ObserveCounter"/> 로 수집 주기에 한 번 읽어간다 — 핫패스 비용 0.
/// </para>
/// <para>
/// <b>등록은 프로세스당 1회면 충분하다.</b> <see cref="BufferPoolDiagnostics"/> 가 정적 전역
/// 카운터이므로 서버 인스턴스마다 부를 필요가 없다. 두 번 등록하면 같은 이름의 계측기가
/// 둘 생겨 값이 중복 보고된다 — 조립 지점에서 한 번만 부른다.
/// </para>
/// <para>
/// <b>무엇을 보게 되는가.</b> <see cref="MetricNames.PoolBuffersLeaked"/> 가 <b>0이 아니면 버그</b>다
/// (반납 누락을 파이널라이저가 회수한 횟수). 대여−반납의 차는 살아 있는 대여 수이며, 부하가
/// 빠진 뒤에도 줄지 않으면 누수를 의심한다.
/// </para>
/// </remarks>
public static class BufferPoolMetrics
{
    /// <summary>풀 카운터 3종을 싱크에 등록한다.</summary>
    /// <param name="sink">메트릭 싱크. <c>UseMetrics</c> 에 넘긴 것과 같은 인스턴스를 쓴다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 싱크가 pull 을 지원하지 않으면(기본 구현) 아무 일도 일어나지 않는다 —
    /// 그 경우 이 메트릭이 나오지 않을 뿐 오류가 아니다(<see cref="IMetricsSink.ObserveCounter"/> 계약).
    /// </remarks>
    public static void Register(IMetricsSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        sink.ObserveCounter(
            MetricNames.PoolBuffersRented,
            static () => BufferPoolDiagnostics.RentedBuffers,
            ReadOnlySpan<MetricTag>.Empty);

        sink.ObserveCounter(
            MetricNames.PoolBuffersReturned,
            static () => BufferPoolDiagnostics.ReturnedBuffers,
            ReadOnlySpan<MetricTag>.Empty);

        sink.ObserveCounter(
            MetricNames.PoolBuffersLeaked,
            static () => BufferPoolDiagnostics.LeakedBuffers,
            ReadOnlySpan<MetricTag>.Empty);
    }
}
