using System;
using Microsoft.AspNetCore.Identity;

namespace ChServerM.Security.AspNetIdentity;

/// <summary>
/// <see cref="IPasswordHasher"/>의 ASP.NET Core Identity(PBKDF2) 어댑터 (ADR-0018).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시 `AuthM` 이 쓰던 검증된 구현
/// (<see cref="PasswordHasher{TUser}"/> — PBKDF2-HMAC-SHA256, 비밀번호별 랜덤 솔트,
/// 반복 횟수·버전 태그 내장 형식)을 그대로 위임한다. 직접 해시를 조립하지 않는 것이
/// 1차 완화책이다(ADR-0017 과 같은 원칙). <b>같은 라이브러리·같은 형식이므로 레거시로
/// 저장된 계정 해시가 그대로 검증된다</b> — 계정 이전 경로가 살아 있다.
/// </para>
/// <para>
/// <b>레거시 결함의 역.</b> 레거시는 (1) 호출마다 해셔를 생성했고, (2) 반복 횟수를
/// 명시하지 않았고, (3) <c>internal</c> 이라 재사용이 불가했고, (4) 재해싱 신호가 없었다
/// (legacy/07-security AuthM #2~4). 여기서는 해셔를 <b>1회 생성해 재사용</b>하고,
/// 반복 횟수를 <b>생성자에서 명시</b>하며, <see cref="PasswordVerification.SuccessRehashNeeded"/>
/// 를 값으로 드러낸다.
/// </para>
/// <para>
/// <b>기본 반복 횟수는 OWASP 권장(600,000)이다.</b> 라이브러리 기본(100,000)보다 높다 —
/// 로그인당 수십 ms 급 CPU 비용이며 이것은 버그가 아니라 목적이다(유출 해시의 오프라인
/// 대입 비용). 워크로드에 맞게 낮추려면 근거를 남기고 생성자 인자로 지정한다.
/// 반복 횟수는 해시 문자열에 저장되므로 값을 바꿔도 기존 해시 검증은 깨지지 않는다 —
/// 대신 <see cref="PasswordVerification.SuccessRehashNeeded"/> 가 돌아온다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다. 싱글턴으로 재사용한다.</para>
/// </remarks>
public sealed class AspNetIdentityPasswordHasher : IPasswordHasher
{
    /// <summary>기본 PBKDF2 반복 횟수. OWASP 권장(PBKDF2-HMAC-SHA256 기준 600,000).</summary>
    public const int DefaultIterationCount = 600_000;

    private readonly PasswordHasher<object> _hasher;

    // 해셔 API 가 TUser 인자를 요구하지만 해싱에 쓰지 않는다 — 레거시도 null 을 넘겼다.
    // null 대신 공유 인스턴스를 넘겨 널 인자 계약 논쟁 자체를 피한다.
    private static readonly object User = new();

    /// <summary>반복 횟수를 명시해 해셔를 만든다.</summary>
    /// <param name="iterationCount">PBKDF2 반복 횟수. 기본은 <see cref="DefaultIterationCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="iterationCount"/>가 1 미만일 때.</exception>
    /// <remarks>
    /// 레거시 결함 #4("파라미터가 라이브러리 기본값에 의존, 명시적 정책 없음")의 역 —
    /// 값이 항상 코드에 드러난다.
    /// </remarks>
    public AspNetIdentityPasswordHasher(int iterationCount = DefaultIterationCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterationCount, 1);

        // V3 = PBKDF2-HMAC-SHA256, 128비트 솔트, 256비트 서브키, 형식 버전 태그 포함.
        _hasher = new PasswordHasher<object>(
            Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                IterationCount = iterationCount,
            }));
    }

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return _hasher.HashPassword(User, password);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 저장된 해시가 형식조차 아니면(<see cref="FormatException"/> — base64 가 아님 등)
    /// 예외가 아니라 <see cref="PasswordVerification.Failed"/> 다 — 저장소 오염·조작이
    /// 인증 경로의 예외 비용으로 증폭되지 않게 한다(T-16). 벤더는 이 경우 던지므로
    /// 값 계약으로의 변환은 어댑터의 몫이다.
    /// </remarks>
    public PasswordVerification Verify(string hashedPassword, string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(hashedPassword);
        ArgumentNullException.ThrowIfNull(providedPassword);

        try
        {
            return _hasher.VerifyHashedPassword(User, hashedPassword, providedPassword) switch
            {
                PasswordVerificationResult.Success => PasswordVerification.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
                _ => PasswordVerification.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed;
        }
    }
}
