using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 메시지 디스패치를 분산 추적 span 으로 남기는 미들웨어 (Phase 11 관측, ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 횡단 관심사는 데코레이터로.</b> 추적을 읽기 루프나 핸들러에 흩뿌리면
/// 코어 로직이 관측 코드로 오염된다(CLAUDE.md 4). 이 미들웨어 하나가 <c>next</c> 를 감싸
/// 프레임마다 <see cref="ActivityNames.Dispatch"/> span 을 열고 닫는다 — 핸들러도 라우팅도
/// 추적을 알지 않는다. <see cref="MetricsMiddleware"/> 의 추적판이다.
/// </para>
/// <para>
/// <b>왜 <see cref="ActivitySource"/> 직접인가 (ADR-0022).</b> 메트릭은
/// <see cref="IMetricsSink"/> 로 감쌌지만 추적은 감싸지 않는다 — 이유는 추적의 <b>교체
/// 지점이 방출자가 아니라 구독자(<see cref="ActivityListener"/>) 쪽</b>이기 때문이다.
/// OpenTelemetry·Jaeger·Application Insights 는 모두 <see cref="Activity"/> 를 이름으로
/// 구독하지 방출 API 를 바꾸지 않는다. 방출자는 하나뿐이고 대안 구현이 없으므로, Core 에
/// 중립 인터페이스를 두면 프레임당 span 핸들 할당이라는 비용만 생기고 두 번째 구현은
/// 오지 않는다("추상화가 느는 것 자체가 비용", "두 번째 구현 전엔 추상화는 가설"). span 은
/// 이 어셈블리(Hosting) 두 지점에서만 나므로 여기에 <see cref="ActivitySource"/> 를 둔다.
/// <see cref="ActivitySource"/>·<see cref="Activity"/> 는 공유 프레임워크라 Core 무의존을
/// 깨지 않는다(Core 는 이 API 를 참조하지 않는다).
/// </para>
/// <para>
/// <b>fast-path — 리스너 없으면 데코레이터가 사라진다.</b> 구독자가 없으면
/// <see cref="ActivitySource.HasListeners"/> 가 <see langword="false"/> 이고,
/// 이때 <c>next</c> 를 <b>async 상태 머신 없이 그대로</b> 반환한다. 데코레이터의 순수
/// 오버헤드(인터페이스 가상 호출 + async 래퍼)가 계측값보다 비싸다는 것이 관측 게이트에서
/// 반복 확인된 비용인데(Null 싱크에서도 6→43ns), 추적은 <see cref="ActivitySource.HasListeners"/>
/// 라는 값싼 게이트가 있어 리스너 없는 조립을 near-zero 패스스루로 만들 수 있다.
/// </para>
/// <para>
/// <b>부모 span — 크로스 스레드 전파.</b> 디스패치 span 은 <see cref="ActivityNames.Connection"/>
/// span 의 자식이다. 실행 모델(ADR-0008)에서 디스패치는 파티션 스레드에서 돌고
/// <see cref="Activity.Current"/> 는 AsyncLocal 이라 채널 핸드오프를 넘지 못하므로, 부모는
/// <see cref="Activity.Current"/> 가 아니라 <see cref="TracingConnectionHandler"/> 가 커넥션
/// 기능에 실어둔 <see cref="ConnectionTraceFeature.ParentContext"/> 에서 <b>명시적으로</b>
/// 읽어 넘긴다. 커넥션 span 이 없으면(리스너 없음) 부모가 비어 디스패치 span 이 루트가 된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다.
/// <see cref="ActivitySource"/> 는 스레드 안전하다.
/// </para>
/// </remarks>
public sealed class TracingMiddleware : IServerMiddleware
{
    /// <inheritdoc />
    public ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // 구독자가 없으면 span 도 async 상태 머신도 만들지 않고 next 를 그대로 돌려준다.
        // 리스너 없는 조립에서 이 데코레이터를 near-zero 로 만드는 fast-path 다(타입 문서).
        // 커넥션 기능 조회조차 이 게이트 뒤에서만 일어난다.
        if (!ServerTracing.Source.HasListeners())
        {
            return next(context);
        }

        return InvokeTracedAsync(context, next);
    }

    private static async ValueTask<DispatchStatus> InvokeTracedAsync(MessageContext context, MessageDelegate next)
    {
        // 부모는 Activity.Current(파티션 스레드로 안 흐름)가 아니라 커넥션 기능에서 명시적으로
        // 읽는다(타입 문서의 크로스 스레드 전파). 커넥션 span 이 없으면 default → 루트 span.
        ActivityContext parent = context.Connection.Features.Get<ConnectionTraceFeature>()?.ParentContext ?? default;

        // Server kind — 인바운드 요청을 처리하는 span 이다. HasListeners 가 true 여도
        // 리스너의 샘플러가 이 span 을 거부하면 null 이 온다 — 아래 null 검사가 그 경우다.
        using Activity? activity = ServerTracing.Source.StartActivity(
            ActivityNames.Dispatch, ActivityKind.Server, parent);

        if (activity is not null)
        {
            // 고카디널리티 식별자는 메트릭 태그가 아니라 span 속성으로만 남긴다(TagNames 규약).
            // 이 둘이 트레이스를 커넥션·메시지 종류별로 상관시키는 축이다.
            activity.SetTag(TagNames.MessageId, context.Envelope.MessageId.Value);
            activity.SetTag(TagNames.ConnectionId, context.Connection.Id.ToString());
        }

        DispatchStatus status = await next(context).ConfigureAwait(false);

        if (activity is not null && status != DispatchStatus.Handled)
        {
            // 거부·실패를 span 상태로 남긴다 — 추적 백엔드에서 오류 span 만 필터링된다.
            // 상태값은 유한 enum 이라 태그 카디널리티가 안전하고, 이름은 정적 캐시라 실패
            // 프레임마다 문자열을 만들지 않는다(감사 2026-08-18 H-4). 태그 이름은 메트릭 실패
            // 분류와 같은 dispatch_status 다 — error_code 는 ErrorCode 값 계약이라 여기에
            // 상태명을 실으면 두 의미가 한 태그에 섞인다(O-9).
            string statusName = DispatchStatusNames.Get(status);
            activity.SetStatus(ActivityStatusCode.Error, statusName);
            activity.SetTag(TagNames.DispatchStatus, statusName);
        }

        return status;
    }
}
