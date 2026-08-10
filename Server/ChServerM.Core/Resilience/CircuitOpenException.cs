using System;

namespace ChServerM.Resilience;

/// <summary>
/// 서킷이 열려 있어 호출을 시도하지 않고 즉시 실패시켰다.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ 왜 예외인가 — 핫패스에 예외를 쓰지 않는다는 규칙(CLAUDE.md 8절)의 예외다.</b>
/// </para>
/// <para>
/// 세션 계약의 반환 타입에는 "저장소가 대답하지 않았다" 를 표현할 자리가 없다.
/// 억지로 <c>NotFound</c> 나 <c>Conflict</c> 로 접으면 <b>장애가 정상 결과로 위장</b>된다 —
/// 호출자는 "세션이 없다" 로 읽고 새 세션을 만들 것이고, 그것이 곧 <b>사용자 상태 유실</b>이다.
/// 조용히 잘못된 답을 주는 것보다 시끄럽게 실패하는 것이 낫다.
/// </para>
/// <para>
/// 그리고 이것은 <b>진짜로 예외적인 상황</b>이다 — 정상 운영에서는 발생하지 않고,
/// 발생하면 대상이 죽어 있다는 뜻이다. CAS 충돌처럼 <b>흔하게 일어나는 결과</b>는
/// 여전히 반환값으로 표현한다(<c>SessionWriteResult.Conflict</c>).
/// </para>
/// <para>
/// <b>호출자가 할 일.</b> 이 예외는 재시도해도 즉시 다시 던져진다(그것이 빠른 실패의
/// 목적이다). 상위 계층은 이것을 <b>일시적 사용 불가</b>로 해석해 사용자에게 알리거나,
/// 세션이 필수가 아닌 경로라면 축소된 기능으로 진행한다.
/// </para>
/// </remarks>
public sealed class CircuitOpenException : Exception
{
    /// <summary>기본 메시지로 예외를 만든다.</summary>
    public CircuitOpenException()
        : base("서킷이 열려 있어 호출을 차단했다.")
    {
    }

    /// <summary>메시지를 지정해 예외를 만든다.</summary>
    /// <param name="message">메시지.</param>
    public CircuitOpenException(string message)
        : base(message)
    {
    }

    /// <summary>메시지와 내부 예외를 지정해 예외를 만든다.</summary>
    /// <param name="message">메시지.</param>
    /// <param name="innerException">내부 예외.</param>
    public CircuitOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>차단한 브레이커 이름으로 예외를 만든다.</summary>
    /// <param name="circuitName">브레이커 이름.</param>
    /// <returns>이름이 포함된 예외.</returns>
    public static CircuitOpenException ForCircuit(string circuitName) =>
        new($"서킷 '{circuitName}' 이 열려 있어 호출을 차단했다. 대상이 회복될 때까지 즉시 실패한다.");
}
