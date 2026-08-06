using System;
using System.Collections.Generic;
using ChServerM.Identity;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 인가 미들웨어(T-21)의 설정 — 정책을 적용할 보호 대상 메시지 목록.
/// </summary>
/// <remarks>
/// <para>
/// <b>보호 목록 방식이다.</b> 목록에 있는 메시지만 정책을 호출하고 나머지는 통과한다.
/// 목록 밖 메시지의 기본 거부는 이 미들웨어의 몫이 아니라 상태 화이트리스트(T-19)의
/// 몫이다 — 기본 거부 장치를 두 곳에 두면 어느 쪽이 진짜 경계인지 흐려진다.
/// 자원 검사가 필요 없는 대다수 메시지에 프레임당 가상 호출을 부과하지 않는 선택이기도
/// 하다(전 메시지 정책 호출 대안의 탈락 이유).
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 미들웨어 생성자가 목록을 동결한다.</para>
/// </remarks>
public sealed class AuthorizationOptions
{
    private readonly HashSet<ushort> _protectedMessages = [];

    /// <summary>보호 대상 메시지 목록. 미들웨어가 조립 시점에 동결한다.</summary>
    internal IReadOnlyCollection<ushort> ProtectedMessages => _protectedMessages;

    /// <summary>메시지를 보호 대상으로 지정한다.</summary>
    /// <param name="messageId">정책 판정을 거칠 메시지 식별자.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">
    /// 센티넬(0)이거나 이미 지정된 식별자일 때 — 중복은 의도가 갈린 조립의 신호다.
    /// </exception>
    public AuthorizationOptions Protect(MessageId messageId)
    {
        if (messageId.IsNone)
        {
            throw new ArgumentException(
                "메시지 식별자 0 은 '설정되지 않음'을 뜻하는 센티넬이다. 보호 대상이 될 수 없다.",
                nameof(messageId));
        }

        if (!_protectedMessages.Add(messageId.Value))
        {
            throw new ArgumentException(
                $"메시지 식별자 {messageId.Value} 는 이미 보호 대상이다.",
                nameof(messageId));
        }

        return this;
    }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">보호 대상이 하나도 없을 때 — 죽은 조립이다.</exception>
    public void Validate()
    {
        if (_protectedMessages.Count == 0)
        {
            throw new InvalidOperationException(
                "보호 대상 메시지가 하나도 없다. 정책이 한 번도 호출되지 않는 죽은 조립이다 — " +
                $"{nameof(Protect)} 로 대상을 지정하거나 미들웨어를 빼라.");
        }
    }
}
