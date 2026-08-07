using System;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Resilience;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 부하가 높을 때 비필수 메시지를 버리는 미들웨어 — 우아한 열화 (Phase 10, ADR-0029).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 과부하에서 <i>무엇을</i> 포기할지 고르게 한다.</b> 수용 제어(신규 연결 거부)와
/// 속도 제한(클라이언트별 상한)은 <b>무차별</b>이다. 부하가 올라갔을 때 텔레메트리와 인증을
/// 같은 비중으로 버리면, 서버는 살아 있는데 <b>쓸모없는 상태</b>가 된다. 이 미들웨어는 앱이
/// 선언한 순서대로(<see cref="LoadSheddingOptions"/>) 비필수부터 끊어, 남은 자원을 필수 경로에
/// 몰아준다.
/// </para>
/// <para>
/// <b>fast-path — 정상 부하에서는 사실상 사라진다.</b> <see cref="LoadLevel.Normal"/> 이면
/// 규칙을 조회하지 않고 <c>next</c> 를 <b>async 래퍼 없이 그대로</b> 반환한다. 열화는 드물게
/// 발동하는 정책이므로 <b>평상시 비용이 0에 가까워야</b> 한다 — 그러지 않으면 "만일을 위한
/// 보험" 이 상시 세금이 된다(추적 미들웨어의 <c>HasListeners</c> 게이트와 같은 규율, ADR-0022).
/// </para>
/// <para>
/// <b>거부는 커넥션을 닫지 않는다.</b> <see cref="DispatchStatus.RejectedByLoadShedding"/> 는
/// 무-종료로 매핑된다 — 부하가 높을 때 커넥션을 끊으면 그 재접속이 부하를 더 키워
/// <b>열화가 붕괴를 앞당긴다</b>.
/// </para>
/// <para>
/// <b>가장 바깥 근처에 둔다.</b> 버릴 메시지에 인증·역직렬화 비용을 먼저 쓰면 열화의 목적
/// (자원 아끼기)이 반감된다. 다만 <see cref="MetricsMiddleware"/> 보다는 안쪽이어야 버려진
/// 프레임도 처리량·실패 메트릭에 잡힌다.
/// </para>
/// <para><b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다.</para>
/// </remarks>
public sealed class LoadSheddingMiddleware : IServerMiddleware
{
    private readonly ILoadLevelSource _loadLevel;
    private readonly LoadSheddingOptions _options;

    /// <summary>부하 소스와 정책으로 미들웨어를 만든다.</summary>
    /// <param name="loadLevel">현재 부하를 알려주는 소스.</param>
    /// <param name="options">메시지별 유지 상한. 규칙이 하나도 없으면 조립 오류다.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">규칙이 하나도 없을 때.</exception>
    public LoadSheddingMiddleware(ILoadLevelSource loadLevel, LoadSheddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(loadLevel);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _loadLevel = loadLevel;
        _options = options;
    }

    /// <inheritdoc />
    public ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        LoadLevel current = _loadLevel.Current;

        // 평상시 경로 — 규칙 조회조차 하지 않고 그대로 통과시킨다(타입 문서의 fast-path).
        if (current == LoadLevel.Normal)
        {
            return next(context);
        }

        if (_options.ShouldShed(context.Envelope.MessageId, current))
        {
            // 버린다. 커넥션은 닫지 않으며, 상위 MetricsMiddleware 가 DispatchFailures 로 센다.
            return new ValueTask<DispatchStatus>(DispatchStatus.RejectedByLoadShedding);
        }

        return next(context);
    }
}
