using System;
using ChServerM.Identity;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 인증 미들웨어(T-20)의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 인증에서 "정책"인 값은 하나뿐이다 — 자격 증명이 실려 오는 메시지가
/// 무엇인가. 검증 방법은 <c>IAuthenticator</c> 구현이, 실패 처리(무조건 종료)는 계약이
/// 정하므로 설정이 아니다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 미들웨어 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class AuthenticationOptions
{
    /// <summary>자격 증명이 실려 오는 메시지 식별자.</summary>
    /// <remarks>
    /// 이 메시지만 <c>IAuthenticator</c> 로 보낸다. 나머지 메시지의 인증 전 차단은
    /// <see cref="MessageStateFilterMiddleware"/>(T-19)의 몫이다 — 화이트리스트 없이
    /// 인증만 조립하면 다른 메시지는 인증 없이 핸들러에 닿는다는 뜻이다(게스트 플레이
    /// 같은 선택 인증 워크로드가 정당하므로 조립을 막지는 않는다).
    /// </remarks>
    public MessageId CredentialMessageId { get; set; }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (CredentialMessageId.IsNone)
        {
            throw new InvalidOperationException(
                $"{nameof(CredentialMessageId)} 가 설정되지 않았다(센티넬 0). " +
                "자격 메시지 식별자를 지정한다.");
        }
    }
}
