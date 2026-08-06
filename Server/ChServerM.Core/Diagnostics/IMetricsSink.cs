using System;

namespace ChServerM.Diagnostics;

/// <summary>
/// 프레임워크가 발행하는 메트릭의 수집 축 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시는 메트릭이 <b>하나도 없어</b> 조용한 실패(압축 미실행·재시도
/// 무효·만료 미동작)를 아무도 몰랐다([09-observability](../../docs/legacy/09-observability.md)).
/// 이 계약은 "조용한 실패가 가능한 지점마다 카운터를 두고 0이 아니면 경보한다"를 실행하는
/// 축이다 — 프레임워크가 계측 지점을 정하고, 그 신호를 어디로 보낼지(BCL <c>Meter</c>·
/// OpenTelemetry·없음)는 어댑터가 정한다.
/// </para>
/// <para>
/// <b>이름은 계약이다.</b> 메트릭 이름에는 <see cref="MetricNames"/> 상수를,
/// 태그 이름에는 <see cref="TagNames"/> 상수를 쓴다 — 문자열 리터럴이 흩어지면 오타 하나가
/// 조용히 메트릭을 사라지게 한다(<see cref="DiagnosticNames"/> 규약).
/// </para>
/// <para>
/// <b>핫패스 무할당.</b> 태그는 <see cref="ReadOnlySpan{T}"/> 로 받는다 — 호출자가
/// <c>stackalloc</c>/인라인 배열로 넘기면 할당이 없다. 구현은 리스너가 없을 때 거의
/// 무비용이어야 한다(BCL <c>Counter.Add</c> 가 그렇다). 관측이 성능을 먹으면 프로덕션에서
/// 꺼지고, 꺼진 관측은 없는 것과 같다 — 이 오버헤드는 벤치로 방어한다(Phase 11 게이트).
/// </para>
/// <para>
/// <b>기본은 <c>NullMetricsSink</c>다.</b> 싱크를 주입받지 못한 경로가 null 검사로
/// 지저분해지지 않게 한다(<c>NullServerLogger</c> 와 같은 원칙).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 모든 커넥션·파티션이 한 인스턴스를 동시 호출한다 — 구현은 스레드
/// 안전해야 한다(BCL 계측 타입은 그렇다).
/// </para>
/// </remarks>
public interface IMetricsSink
{
    /// <summary>누적 카운터를 증가시킨다(수신 프레임·거부 수 등).</summary>
    /// <param name="name">메트릭 이름(<see cref="MetricNames"/>).</param>
    /// <param name="delta">증가량. 음수를 쓰지 않는다 — 감소하는 값은 <see cref="AdjustGauge"/>다.</param>
    /// <param name="tags">분류 태그. 없으면 빈 스팬.</param>
    void Count(string name, long delta, ReadOnlySpan<MetricTag> tags);

    /// <summary>히스토그램에 관측값을 기록한다(디스패치 지연 등 — p50/p99를 어댑터가 계산).</summary>
    /// <param name="name">메트릭 이름(<see cref="MetricNames"/>).</param>
    /// <param name="value">관측값(초 등).</param>
    /// <param name="tags">분류 태그. 없으면 빈 스팬.</param>
    void Record(string name, double value, ReadOnlySpan<MetricTag> tags);

    /// <summary>오르내리는 게이지를 조정한다(활성 커넥션 수 등).</summary>
    /// <param name="name">메트릭 이름(<see cref="MetricNames"/>).</param>
    /// <param name="delta">증감량. 커넥션 수립 <c>+1</c>, 종료 <c>-1</c>.</param>
    /// <param name="tags">분류 태그. 없으면 빈 스팬.</param>
    void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags);
}

/// <summary>아무것도 기록하지 않는 <see cref="IMetricsSink"/>.</summary>
/// <remarks>
/// 관측을 조립하지 않은 경로의 기본값이다. 세 메서드 모두 즉시 반환하므로 JIT 이
/// 호출을 접을 수 있다 — 관측 없는 조립의 핫패스 비용을 0에 가깝게 만든다(게이트 벤치의 기준선).
/// </remarks>
public sealed class NullMetricsSink : IMetricsSink
{
    /// <summary>공유 인스턴스.</summary>
    public static NullMetricsSink Instance { get; } = new();

    private NullMetricsSink()
    {
    }

    /// <inheritdoc />
    public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
    {
        // 의도적으로 비어 있다.
    }

    /// <inheritdoc />
    public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
    {
        // 의도적으로 비어 있다.
    }

    /// <inheritdoc />
    public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
    {
        // 의도적으로 비어 있다.
    }
}
