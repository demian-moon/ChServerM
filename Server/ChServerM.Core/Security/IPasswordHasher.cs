namespace ChServerM.Security;

/// <summary>
/// 비밀번호를 단방향 해시로 저장·검증하는 계약 (Phase 9, 레거시 `AuthM` 승계).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시 보안 코드에서 유일하게 옳았던 컴포넌트
/// (PBKDF2 + 비밀번호별 랜덤 솔트, legacy/07-security)의 승계 자리다. 계약으로 두는
/// 이유는 다른 축과 같다 — 구현(알고리즘·형식)은 어댑터에 격리하고, Core 는
/// 무의존을 유지한다.
/// </para>
/// <para>
/// <b>비밀번호 해싱은 일부러 느리다.</b> 유출된 해시의 오프라인 대입을 비싸게 만드는
/// 것이 목적이므로, 이 호출을 핫패스 무할당 규약의 대상으로 보지 않는다 —
/// 회원가입·로그인당 1회다.
/// </para>
/// <para>
/// <b><see cref="PasswordVerification.SuccessRehashNeeded"/> 를 무시하지 않는다.</b>
/// 파라미터(반복 횟수 등)를 올린 뒤에도 기존 해시는 옛 파라미터로 남는다 — 검증 성공
/// 시점이 새 파라미터로 재해싱할 유일한 기회다(원문 비밀번호가 그때만 있다).
/// </para>
/// <para><b>스레드 규약.</b> 구현은 스레드 안전해야 한다 — 여러 커넥션의 로그인이 동시에 부른다.</para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>비밀번호를 저장용 해시로 만든다.</summary>
    /// <param name="password">원문 비밀번호.</param>
    /// <returns>솔트·파라미터가 포함된 자기서술적 해시 문자열.</returns>
    string Hash(string password);

    /// <summary>저장된 해시에 대해 비밀번호를 검증한다.</summary>
    /// <param name="hashedPassword">저장돼 있던 해시.</param>
    /// <param name="providedPassword">사용자가 제시한 원문 비밀번호.</param>
    /// <returns>
    /// 판정. <b>반환값을 버리면 레거시 T-20 재발이다</b> — 인증 경로에서는
    /// <c>IAuthenticator</c> 구현 안에서 쓰고 결과를 <c>AuthenticationResult</c> 로 변환한다.
    /// </returns>
    PasswordVerification Verify(string hashedPassword, string providedPassword);
}

/// <summary>비밀번호 검증 판정.</summary>
public enum PasswordVerification
{
    /// <summary>판정 전. 이 값이 관측되면 초기화 누락 버그다(센티넬).</summary>
    None = 0,

    /// <summary>불일치. 인증 실패로 처리한다.</summary>
    Failed,

    /// <summary>일치.</summary>
    Success,

    /// <summary>
    /// 일치하지만 해시가 옛 파라미터다 — <b>지금 재해싱해 저장한다.</b>
    /// 원문 비밀번호가 손에 있는 유일한 시점이다. 레거시는 이 신호 자체를 버렸다.
    /// </summary>
    SuccessRehashNeeded,
}
