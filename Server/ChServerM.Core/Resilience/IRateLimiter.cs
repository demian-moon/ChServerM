using ChServerM.Dispatch;

namespace ChServerM.Resilience;

/// <summary>
/// 메시지 처리 속도를 제한하는 과부하 제어 축 (Phase 10, T-17 보완).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 수용 제어의 메시지 수준 짝.</b> <see cref="IAdmissionControl"/> 이
/// 신규 <b>연결</b>을 막는다면, 이 축은 수용된 커넥션이 <b>메시지</b>로 디스패치 파이프라인을
/// 폭주시키는 것을 막는다(버그·악의 클라이언트가 초당 수만 프레임을 쏘는 경우). 연결은
/// 통과했지만 그 뒤 메시지가 자원을 소모하는 표면을 닫는다 — <b>거부가 붕괴보다 낫다</b>(9.6).
/// </para>
/// <para>
/// <b>동기·비대기 판정이다.</b> <see cref="TryAcquire"/> 는 허가를 즉시 얻거나 못 얻는다 —
/// <b>대기(큐잉)하지 않는다</b>. 과부하에서 요청을 큐에 쌓으면 그것이 곧 지연·메모리 폭발
/// (T-17)이다. 못 얻으면 그 프레임을 버리고, 클라이언트가 스스로 늦춘다.
/// </para>
/// <para>
/// <b>판정 기준은 구현이 정한다.</b> 커넥션별·세션별·메시지 타입별·전역 — 무엇으로 나눌지는
/// <see cref="MessageContext"/>(커넥션 ID·메시지 ID·신원 피처)를 보고 구현이 정한다.
/// 첫 참조 구현은 커넥션별이다(순차 디스패치라 상태를 <c>Connection.Features</c> 에 둬
/// 락 없이 유지).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 서로 다른 커넥션이 한 인스턴스를 동시 호출할 수 있다 — 구현의 공유
/// 상태는 스레드 안전해야 한다. <b>같은</b> 커넥션의 호출은 순차 디스패치 컨텍스트라 겹치지
/// 않으므로, 커넥션별 상태는 동기화 없이 접근할 수 있다(9.1 파티셔닝).
/// </para>
/// <para>
/// <b>핫패스다.</b> 프레임당 호출된다 — 판정은 카운터 비교 수준이어야 하고 할당·IO 를 만들지
/// 않는다. 벤더 타입을 계약에 노출하지 않는다(무의존).
/// </para>
/// </remarks>
public interface IRateLimiter
{
    /// <summary>이 메시지의 처리 허가를 즉시 시도한다(대기하지 않는다).</summary>
    /// <param name="context">메시지 문맥. 판정 기준(커넥션·메시지 타입 등)을 여기서 읽는다.</param>
    /// <returns>허가를 얻으면 <see langword="true"/>. 아니면 프레임이 버려진다.</returns>
    bool TryAcquire(MessageContext context);
}
