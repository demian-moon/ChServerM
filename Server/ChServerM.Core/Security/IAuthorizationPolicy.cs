using System.Threading.Tasks;
using ChServerM.Dispatch;

namespace ChServerM.Security;

/// <summary>
/// 보호 대상 메시지의 인가를 판정하는 계약 (Phase 9, T-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 상태 비트로 표현할 수 없는 판정.</b> 메시지 수준의 거친 인가
/// ("관리자만 이 메시지를 보낼 수 있다")는 이 계약의 몫이 <b>아니다</b> — 그것은
/// 상태 화이트리스트(T-19) + 인증의 <c>GrantedStates</c> 조합이 이미 기본 거부로
/// 담당한다. 이 계약은 그 위의 <b>자원 수준 판정</b>을 위한 것이다: "자기 소유
/// 오브젝트만 수정 가능"처럼 페이로드 내용과 신원을 함께 봐야 하는 것,
/// 그리고 세션 중 권한 회수 같은 동적 정책.
/// </para>
/// <para>
/// <b>신원은 커넥션 피처에서 읽는다.</b> 인증기가 등록한 앱 정의 신원 피처
/// (<c>context.Connection.Features</c>)가 입력이다 — 그래서 조립 순서상 인가는
/// 인증 <b>뒤</b>여야 하며, <c>MessageDispatcherBuilder.Build()</c> 가 순서를 검증한다.
/// </para>
/// <para>
/// <b>⚠ 수명 규약.</b> <see cref="MessageContext.Payload"/> 는 반환 시점에 무효가 된다 —
/// 외부 조회로 <c>await</c> 를 넘어야 하면 필요한 바이트를 먼저 복사한다
/// (<see cref="IAuthenticator"/> 와 동일).
/// </para>
/// <para>
/// <b>실패는 값이다</b>(T-16). 예외는 구현 자체의 결함에만 쓴다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션의 디스패치 순차 컨텍스트에서 호출된다 — 같은 커넥션의
/// 동시 호출은 없지만, 서로 다른 커넥션이 같은 인스턴스를 동시에 부르므로 공유 상태는
/// 스레드 안전해야 한다. 실행 모델(파티션 배타)과 조립하면 이 호출 동안 같은 파티션의
/// 다른 커넥션도 대기한다 — 느린 외부 조회는 파티션 점유 시간이다.
/// </para>
/// </remarks>
public interface IAuthorizationPolicy
{
    /// <summary>보호 대상 메시지의 인가를 판정한다.</summary>
    /// <param name="context">메시지 문맥. 취소 토큰도 여기서 얻는다.</param>
    /// <returns>허용 또는 거부. 거부 처리(무시/종료)는 조립 정책이 정한다 —
    /// 인증(무조건 종료)과 달리 인가 거부는 정당한 세션의 정상 흐름일 수 있다.</returns>
    ValueTask<AuthorizationDecision> AuthorizeAsync(MessageContext context);
}
