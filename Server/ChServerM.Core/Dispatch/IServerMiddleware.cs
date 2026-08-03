using System.Threading.Tasks;

namespace ChServerM.Dispatch;

/// <summary>
/// 메시지 처리 전후에 끼어드는 단계.
/// </summary>
/// <remarks>
/// <para>
/// 인증·속도 제한·추적·상태 검증이 전부 여기로 들어온다. <b>이것들이 미들웨어여야 하는
/// 이유는 워크로드마다 필요한 조합이 다르기 때문</b>이다 — 실시간 상태 유지 프로필과
/// 무상태 웹 프로필이 같은 핸들러를 쓰되 다른 파이프라인을 갖는다(ADR-0004).
/// </para>
/// <para>
/// <b>다음 단계를 부르지 않으면 요청이 거기서 끝난다.</b> 거부는 정상 동작이다.
/// 다만 그때 <see cref="DispatchStatus.Handled"/>가 아닌 값을 반환해야 한다 —
/// 그래야 거부가 메트릭에 남는다. 조용한 거부가 레거시의 주된 병이었다.
/// </para>
/// <para>
/// 레거시의 상태 기반 패킷 화이트리스트는 좋은 자산이지만 O(n) 선형 탐색에
/// 가상 호출이 n번 붙었다. 미들웨어로 옮기면서 조립 시점 비트맵으로 바꾼다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 모든 커넥션이 같은 인스턴스를 공유하므로 <b>스레드 안전해야 한다.</b>
/// 요청별 상태는 <see cref="MessageContext.Features"/>에 담는다.
/// </para>
/// </remarks>
public interface IServerMiddleware
{
    /// <summary>메시지를 처리하고 필요하면 다음 단계로 넘긴다.</summary>
    /// <param name="context">메시지 문맥.</param>
    /// <param name="next">다음 단계.</param>
    /// <returns>처리 결과. 통과시켰다면 대개 <paramref name="next"/>의 결과를 그대로 돌려준다.</returns>
    /// <remarks>
    /// <paramref name="next"/>를 부를지 말지가 이 계약의 전부다.
    /// 부른 뒤 후처리를 하려면 <c>await</c>한 다음에 쓴다.
    /// </remarks>
    ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next);
}
