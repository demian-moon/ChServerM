using System.Threading.Tasks;
using ChServerM.Dispatch;

namespace ChServerM.Security;

/// <summary>
/// 자격 메시지의 페이로드를 검증하는 인증 축 (Phase 9, T-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시는 올바른 PBKDF2 검증을 해놓고 <b>호출부가 결과를 버렸다</b>
/// (<c>DoPkLogin</c> 의 <c>WRONG_PW</c> return 주석 처리 — legacy/07-security AuthM #1).
/// 이 계약은 앱 코드가 직접 부르라고 있는 것이 아니라 <c>AuthenticationMiddleware</c> 가
/// 부른다 — 미들웨어는 결과에 따라 <c>DispatchStatus</c> 반환이 강제되므로, 검증 결과를
/// 무시하는 코드가 구조적으로 성립하지 않는다.
/// </para>
/// <para>
/// <b>자격의 형식은 앱 소관이다.</b> 페이로드(<see cref="MessageContext.Payload"/>)는 원시
/// 바이트로 전달된다 — 토큰인지, ID+비밀번호인지, 플랫폼 티켓인지는 워크로드가 정하고,
/// 역직렬화는 앱이 자기 직렬화 축으로 한다(ADR-0004: Core 에 워크로드 전제 금지).
/// </para>
/// <para>
/// <b>⚠ 수명 규약 — 페이로드는 반환 시점에 무효가 된다.</b>
/// <see cref="MessageContext.Payload"/> 는 재사용 버퍼 위의 창이다. 외부 인증
/// 서버·DB 호출로 <c>await</c> 를 넘어야 한다면 자격 바이트를 <b>먼저 복사</b>한다.
/// 레거시가 정확히 이 계약을 주석으로만 적고 위반했다 — 위반하면 이미 반납된
/// 버퍼를 읽는다.
/// </para>
/// <para>
/// <b>실패는 값이다.</b> 오답 자격에 예외를 던지지 않는다 — 로그인 폭주가 예외 비용을
/// 증폭시킨다(T-16). 예외는 구현 자체의 결함(설정 오류 등)에만 쓴다.
/// </para>
/// <para>
/// <b>리플레이 가드와 함께 쓸 때의 순서 규약</b> — 반드시 <b>검증이 전부 통과한 뒤</b>
/// 마지막에 <see cref="ITokenReplayGuard.TryClaim"/> 을 부른다. 순서를 뒤집으면 유효하지
/// 않은 쓰레기 토큰으로 유계 가드를 포화시켜 정상 로그인 전체를 막을 수 있다(로그인 DoS).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션의 디스패치 순차 컨텍스트에서 호출된다 — 같은 커넥션에서
/// 동시 호출은 없지만, 서로 다른 커넥션이 같은 인스턴스를 동시에 부를 수 있으므로
/// 구현의 공유 상태는 스레드 안전해야 한다. 커넥션 피처 접근은 안전하다(순차 컨텍스트).
/// </para>
/// <para>
/// <b>실행 모델 주의.</b> 파티션 배타 실행 모델과 조립하면 이 호출 동안 같은 파티션의
/// 다른 커넥션도 대기한다 — 느린 외부 I/O 인증은 파티션 점유 시간이다.
/// </para>
/// </remarks>
public interface IAuthenticator
{
    /// <summary>자격 메시지를 검증한다.</summary>
    /// <param name="context">자격 메시지의 문맥. 취소 토큰도 여기서 얻는다
    /// (<see cref="MessageContext.CancellationToken"/>).</param>
    /// <returns>
    /// 성공이면 부여할 상태 비트를 담은 결과. 신원 객체를 남기려면 반환 전에
    /// <c>context.Connection.Features</c> 에 앱 정의 피처로 등록한다(순차 컨텍스트라 안전).
    /// </returns>
    ValueTask<AuthenticationResult> AuthenticateAsync(MessageContext context);
}
