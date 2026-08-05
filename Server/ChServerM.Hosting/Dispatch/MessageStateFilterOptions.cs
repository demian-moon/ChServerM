using System;
using System.Collections.Generic;
using ChServerM.Identity;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// <see cref="MessageStateFilterMiddleware"/>의 화이트리스트 규칙.
/// </summary>
/// <remarks>
/// <para>
/// <b>기본 거부는 옵션이 아니다.</b> "등록되지 않은 메시지는 어떤 상태에서도 거부"를
/// 끄는 스위치를 두지 않는다 — 기본값이 넓으면 아무도 좁히지 않고, 그것이 레거시
/// <c>AllowedPkState</c>(기본 전부 허용)의 결함 그 자체였다.
/// </para>
/// <para>
/// 잘못된 규칙은 등록 시점에 즉시 실패한다 — 조립 시점 검증 원칙(CLAUDE.md 2절).
/// </para>
/// </remarks>
public sealed class MessageStateFilterOptions
{
    private readonly Dictionary<ushort, uint> _rules = [];

    /// <summary>커넥션의 시작 상태 집합. 기본값은 비트0 하나다.</summary>
    /// <remarks>
    /// 0(빈 집합)은 <see cref="MessageStateFilterMiddleware"/> 생성 시점에 거부된다 —
    /// 어떤 메시지도 통과하지 못해 상태 전이 자체가 불가능한 죽은 서버가 된다.
    /// </remarks>
    public uint InitialStates { get; set; } = 1;

    /// <summary>등록된 규칙. 미들웨어가 조립 시점에 읽는다.</summary>
    internal IReadOnlyDictionary<ushort, uint> Rules => _rules;

    /// <summary>메시지를 지정 상태 집합에서 허용한다(any-of — 교집합이 있으면 통과).</summary>
    /// <param name="messageId">허용할 메시지 식별자.</param>
    /// <param name="states">허용 상태 마스크. 모든 상태에서 허용하려면 <c>uint.MaxValue</c>.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">
    /// 식별자가 센티넬(0)이거나, 마스크가 0(등록 안 한 것과 같은 규칙 — 실수다)이거나,
    /// 같은 식별자가 이미 등록돼 있을 때.
    /// </exception>
    public MessageStateFilterOptions Allow(MessageId messageId, uint states)
    {
        if (messageId.IsNone)
        {
            throw new ArgumentException(
                "메시지 식별자 0 은 '설정되지 않음'을 뜻하는 센티넬이다. 규칙을 붙일 수 없다.",
                nameof(messageId));
        }

        if (states == 0)
        {
            throw new ArgumentException(
                "허용 상태 마스크가 0이다 — 어떤 상태에서도 통과하지 못하는 규칙은 등록하지 않은 것과 " +
                "같으므로 등록 실수로 취급한다.", nameof(states));
        }

        if (!_rules.TryAdd(messageId.Value, states))
        {
            throw new ArgumentException(
                $"메시지 식별자 {messageId.Value} 에 이미 허용 규칙이 있다. 상태를 합치려면 마스크를 OR 해서 한 번에 등록한다.",
                nameof(messageId));
        }

        return this;
    }
}
