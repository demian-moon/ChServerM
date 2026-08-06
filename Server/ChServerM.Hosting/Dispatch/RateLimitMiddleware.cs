using System;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Resilience;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 메시지 처리 속도를 <see cref="IRateLimiter"/> 로 제한하는 미들웨어 (Phase 10, T-17 보완).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 수용된 커넥션이 메시지로 디스패치 파이프라인을 폭주시키는 것을
/// 핸들러 도달 <b>전</b>에 막는다. 허가를 못 얻으면 <c>next</c> 를 호출하지 않고
/// <see cref="DispatchStatus.RejectedByRateLimit"/> 을 반환한다 — 그 프레임은 버려지되
/// 커넥션은 살아 있다(일시적 제한이라 종료하면 재접속 폭풍).
/// </para>
/// <para>
/// <b>가장 바깥에 둔다(관측 다음).</b> 속도 제한은 인증·인가·상태 필터보다 <b>앞</b>이어야
/// 한다 — 폭주 트래픽을 그 비싼 처리에 들이기 전에 버려야 CPU 를 지킨다. 조립 순서상
/// <c>MetricsMiddleware</c>(관측) 다음, 나머지 앞이다. 등록은
/// <c>ConfigureDispatcher(d =&gt; d.Use(new RateLimitMiddleware(...)))</c> 를 필터·인증보다
/// 먼저 호출한다. (순서 게이트는 필터·인증·인가 3자만 검사하므로 이 미들웨어 위치는
/// 조립하는 쪽 책임이다 — 이 문서가 그 규약이다.)
/// </para>
/// <para>
/// <b>관측은 자동이다.</b> <see cref="DispatchStatus.RejectedByRateLimit"/> 이 위로 전파되면
/// 파이프라인 가장 바깥 <see cref="MetricsMiddleware"/> 가 <c>DispatchFailures</c> 로
/// 상태별 계수한다(전용 메트릭 배선 불필요). 커넥션은 닫히지 않는다
/// (<c>FramedConnectionHandler</c> 가 이 상태를 무-종료로 매핑).
/// </para>
/// <para>
/// <b>핫패스.</b> 프레임당 <see cref="IRateLimiter.TryAcquire"/> 1회 — 구현이 카운터
/// 비교 수준이면 이 미들웨어의 오버헤드는 미미하다. 허가 실패 경로만 상태 태그 스팬을
/// 만든다(허가 성공 경로는 무할당).
/// </para>
/// <para><b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다.</para>
/// </remarks>
public sealed class RateLimitMiddleware : IServerMiddleware
{
    private readonly IRateLimiter _rateLimiter;

    /// <summary>속도 제한기로 미들웨어를 만든다.</summary>
    /// <param name="rateLimiter">속도 제한 구현.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rateLimiter"/>가 <see langword="null"/>일 때.</exception>
    public RateLimitMiddleware(IRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(rateLimiter);
        _rateLimiter = rateLimiter;
    }

    /// <inheritdoc />
    public ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!_rateLimiter.TryAcquire(context))
        {
            // 허가 실패 — next 를 부르지 않는다. 프레임을 버리되 커넥션은 산다.
            return ValueTask.FromResult(DispatchStatus.RejectedByRateLimit);
        }

        return next(context);
    }
}
