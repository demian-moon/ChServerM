using System;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Time;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 디스패치 지연·처리량·실패를 메트릭으로 남기는 미들웨어 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 횡단 관심사는 데코레이터로.</b> 메트릭 수집을 읽기 루프나 핸들러에
/// 흩뿌리면 코어 로직이 관측 코드로 오염된다(CLAUDE.md 4). 디스패치 파이프라인의
/// 지연·처리량·실패는 이 미들웨어 하나가 <c>next</c> 를 감싸 측정하므로, 핸들러도
/// 라우팅도 메트릭을 알지 않는다.
/// </para>
/// <para>
/// <b>가장 바깥에 등록한다.</b> 이 미들웨어가 재는 지연은 "파이프라인 전체"여야 의미가
/// 있다 — 인증·인가·상태 필터를 포함한 처리 시간이다. <c>ConfigureDispatcher</c> 의
/// 첫 <c>Use</c> 로 둔다. (필터·인증·인가 순서 게이트는 그 셋 사이만 검사하므로 이
/// 미들웨어를 맨 앞에 둬도 걸리지 않는다.)
/// </para>
/// <para>
/// <b>남기는 것</b>: 처리한 프레임 수(<see cref="MetricNames.FramesReceived"/> — 디스패처에
/// 도달한 프레임), 디스패치 지연 히스토그램(<see cref="MetricNames.DispatchDuration"/>,
/// 초), 그리고 <see cref="DispatchStatus.Handled"/> 가 아닌 결과의 실패 카운터
/// (<see cref="MetricNames.DispatchFailures"/>, <see cref="TagNames.ErrorCode"/> 없이 상태명
/// 태그로 분류). <b>조용한 거부가 메트릭에 남는 것</b>이 이 계층의 목적이다 — 거부는
/// 정상 동작이지만 관측되지 않으면 레거시의 병이 된다(<c>IServerMiddleware</c> 문서).
/// </para>
/// <para>
/// <b>지연 측정은 단조 시각으로.</b> 벽시계는 NTP 보정으로 뒤로 갈 수 있어 지연이 음수가
/// 된다(<see cref="MonotonicTimestamp"/>). 음수 경과는 0으로 뭉개지 않고 그대로 기록해
/// 시계 이상이 관측되게 둔다 — 다만 히스토그램에는 초 단위 실수로 넣는다.
/// </para>
/// <para>
/// <b>오버헤드.</b> <see cref="NullMetricsSink"/> 조립에서는 세 호출이 즉시 반환되어 JIT 이
/// 접는다. <see cref="Diagnostics.IMetricsSink"/> 어댑터가 붙어도 태그는 <c>stackalloc</c>
/// 스팬이라 할당이 없다. 켠 상태 비용은 Phase 11 게이트 벤치가 방어한다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다.
/// </para>
/// </remarks>
public sealed class MetricsMiddleware : IServerMiddleware
{
    private readonly IMetricsSink _sink;
    private readonly TimeProvider _timeProvider;

    /// <summary>싱크와 시간 원본으로 미들웨어를 만든다.</summary>
    /// <param name="sink">메트릭 싱크.</param>
    /// <param name="timeProvider">지연 측정용 시간 원본. 생략하면 시스템 시계.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/>가 <see langword="null"/>일 때.</exception>
    public MetricsMiddleware(IMetricsSink sink, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        _sink.Count(MetricNames.FramesReceived, 1, ReadOnlySpan<MetricTag>.Empty);

        MonotonicTimestamp start = MonotonicTimestamp.Now(_timeProvider);
        DispatchStatus status = await next(context).ConfigureAwait(false);
        double elapsedSeconds = start.ElapsedSince(_timeProvider).TotalSeconds;

        _sink.Record(MetricNames.DispatchDuration, elapsedSeconds, ReadOnlySpan<MetricTag>.Empty);

        if (status != DispatchStatus.Handled)
        {
            // 실패·거부를 상태명으로 분류한다 — 어느 관문에서 얼마나 떨어지는지 대시보드에서 갈린다.
            // 상태값은 유한 enum 이라 카디널리티가 안전하다.
            Span<MetricTag> tags = [new MetricTag(TagNames.ErrorCode, status.ToString())];
            _sink.Count(MetricNames.DispatchFailures, 1, tags);
        }

        return status;
    }
}
