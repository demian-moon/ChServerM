using System;

namespace ChServerM.Dispatch;

/// <summary>
/// 이 핸들러가 받을 메시지 식별자를 선언한다. 디스패치 소스 제너레이터의 발견 지점이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <c>Map&lt;T&gt;</c> 수동 등록은 중복 ID·누락 핸들러가 런타임에야
/// 드러난다. 이 어트리뷰트를 붙이면 <c>ChServerM.SourceGen</c> 이 컴파일 타임에
/// 검증(CHSM1xxx)하고 등록 코드를 생성한다 — "리플렉션 대신 소스 제너레이터" 하드 룰의
/// 디스패치 축 적용이다(ADR-0014).
/// </para>
/// <para>
/// 붙는 타입은 <see cref="IMessageHandler{TMessage}"/> 를 <b>정확히 하나의</b>
/// 메시지 타입으로 구현해야 한다. 위반은 빌드 실패다(CHSM1002·CHSM1004).
/// </para>
/// <para>
/// 이 타입 자체는 아무 동작이 없다 — 제너레이터가 읽는 메타데이터일 뿐이므로
/// Core(무의존)에 있어도 비용이 없고, 핸들러 어셈블리가 제너레이터 없이도 컴파일된다.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MessageHandlerAttribute : Attribute
{
    /// <summary>메시지 식별자를 선언한다.</summary>
    /// <param name="messageId">이 핸들러가 받을 식별자. 0(센티넬)은 빌드 실패,
    /// 40001 이상(프레임워크 예약 대역)은 경고다 — <see cref="Identity.MessageId"/> 대역 규칙.</param>
    public MessageHandlerAttribute(ushort messageId) => MessageId = messageId;

    /// <summary>이 핸들러가 받을 메시지 식별자.</summary>
    public ushort MessageId { get; }
}
