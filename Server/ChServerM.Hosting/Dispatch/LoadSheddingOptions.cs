using System;
using System.Collections.Generic;
using ChServerM.Identity;
using ChServerM.Resilience;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 부하 시 무엇을 먼저 버릴지 정하는 정책 — 메시지별 <b>유지 상한</b> (Phase 10 우아한 열화).
/// </summary>
/// <remarks>
/// <para>
/// <b>무엇이 비필수인지는 프레임워크가 모른다.</b> 메시지 42 가 텔레메트리인지 인증인지는
/// 애플리케이션 지식이다. 그래서 프레임워크는 메커니즘만 주고 <b>순서는 앱이 정한다</b> —
/// 이 옵션이 그 선언이다.
/// </para>
/// <para>
/// <b>"유지 상한" 으로 읽는다.</b> <see cref="ShedAbove"/> 로 등록한 값은 <b>이 레벨까지는
/// 처리한다</b>는 뜻이고, 현재 부하가 그보다 높으면 버린다. 예:
/// </para>
/// <list type="bullet">
///   <item><description><c>ShedAbove(telemetryId, LoadLevel.Normal)</c> — 압박이 시작되면(Elevated) 곧바로 버린다</description></item>
///   <item><description><c>ShedAbove(chatId, LoadLevel.Elevated)</c> — 한계(Critical)에서만 버린다</description></item>
///   <item><description>등록하지 않은 메시지 — <b>절대 버리지 않는다</b>(인증·하트비트·종료 같은 필수 경로)</description></item>
/// </list>
/// <para>
/// <b>기본이 "버리지 않음" 인 이유.</b> 설정을 빠뜨린 메시지가 조용히 버려지면, 부하가 높을
/// 때만 재현되는 최악의 버그가 된다. <b>버릴 것을 명시적으로 선언하게</b> 만드는 쪽이 안전하다 —
/// 실수의 방향이 "안 버림"(느려짐)이지 "버림"(기능 상실)이 아니게 된다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 미들웨어 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class LoadSheddingOptions
{
    private readonly Dictionary<ushort, LoadLevel> _rules = [];

    /// <summary>이 메시지를 어느 부하 수준까지 유지할지 등록한다.</summary>
    /// <param name="messageId">대상 메시지.</param>
    /// <param name="keepUpTo">
    /// 이 수준까지는 처리한다. 현재 부하가 이보다 높으면 버린다.
    /// <see cref="LoadLevel.Critical"/> 을 주면 사실상 버리지 않는다(등록하지 않은 것과 같다).
    /// </param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public LoadSheddingOptions ShedAbove(MessageId messageId, LoadLevel keepUpTo)
    {
        _rules[messageId.Value] = keepUpTo;
        return this;
    }

    /// <summary>현재 부하에서 이 메시지를 버려야 하는지 판정한다.</summary>
    /// <param name="messageId">판정할 메시지.</param>
    /// <param name="current">현재 부하 수준.</param>
    /// <returns>버려야 하면 <see langword="true"/>.</returns>
    /// <remarks>등록되지 않은 메시지는 항상 <see langword="false"/> — 필수로 취급한다(타입 문서).</remarks>
    internal bool ShouldShed(MessageId messageId, LoadLevel current) =>
        _rules.TryGetValue(messageId.Value, out LoadLevel keepUpTo) && current > keepUpTo;

    /// <summary>등록된 규칙이 하나도 없는지.</summary>
    internal bool IsEmpty => _rules.Count == 0;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">규칙이 하나도 없을 때.</exception>
    /// <remarks>
    /// 규칙이 없는 열화 미들웨어는 <b>아무것도 하지 않으면서 프레임마다 비용만 낸다</b> —
    /// 조립 실수이므로 시작 시점에 잡는다("조용한 무동작을 만들지 않는다").
    /// </remarks>
    public void Validate()
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException(
                "열화 규칙이 하나도 없다. 버릴 메시지를 ShedAbove 로 선언하거나, " +
                "열화를 조립하지 않는다(규칙 없는 미들웨어는 비용만 낸다).");
        }
    }
}
