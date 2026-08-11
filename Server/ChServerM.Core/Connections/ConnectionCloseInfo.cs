using System;
using System.Diagnostics;
using ChServerM.Diagnostics;

namespace ChServerM.Connections;

/// <summary>
/// 커넥션이 왜 닫혔는지를 담는다.
/// </summary>
/// <remarks>
/// <para>
/// 종료는 <b>정상 경로</b>다. 예외로 표현하지 않는다 — 예외였다면 상위에서 삼켜지고
/// 결국 아무도 이유를 모르게 된다(레거시가 그랬다).
/// </para>
/// <para>
/// <see cref="Reason"/>은 메트릭 태그용 저카디널리티 분류,
/// <see cref="ErrorCode"/>는 기계가 분기할 수 있는 세부 원인,
/// <see cref="Description"/>은 사람이 읽는 보충이다. 셋의 역할이 다르다.
/// </para>
/// <para>
/// <see cref="Description"/>에 <b>상대에게 노출할 수 없는 내용</b>을 담아도 된다.
/// 이 구조체는 서버 내부용이며 와이어로 나가지 않는다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ConnectionCloseInfo : IEquatable<ConnectionCloseInfo>
{
    /// <summary>종료 정보를 만든다.</summary>
    /// <param name="reason">대분류.</param>
    /// <param name="errorCode">세부 원인. 정상 종료면 <see cref="Diagnostics.ErrorCode.None"/>.</param>
    /// <param name="description">사람이 읽는 보충 설명.</param>
    public ConnectionCloseInfo(CloseReason reason, ErrorCode errorCode = ErrorCode.None, string? description = null)
    {
        Reason = reason;
        ErrorCode = errorCode;
        Description = description;
    }

    /// <summary>종료 대분류.</summary>
    public CloseReason Reason { get; }

    /// <summary>세부 원인.</summary>
    public ErrorCode ErrorCode { get; }

    /// <summary>사람이 읽는 보충 설명. 없으면 <see langword="null"/>.</summary>
    public string? Description { get; }

    /// <summary>사고 없이 닫혔는지 여부.</summary>
    /// <remarks>
    /// 알람 규칙의 기준선이다. 이것이 <see langword="false"/>인 종료만 세면
    /// 배포로 인한 대량 종료가 장애로 오인되지 않는다.
    /// </remarks>
    public bool IsGraceful => Reason is CloseReason.ClientClosed or CloseReason.ServerClosed or CloseReason.ShuttingDown;

    /// <summary>상대가 정상적으로 닫았다.</summary>
    public static ConnectionCloseInfo ClientClosed => new(CloseReason.ClientClosed);

    /// <summary>서버 종료 절차로 닫는다.</summary>
    public static ConnectionCloseInfo ShuttingDown => new(CloseReason.ShuttingDown, Diagnostics.ErrorCode.ServerShuttingDown);

    /// <summary>프로토콜 위반으로 닫는다.</summary>
    /// <param name="errorCode">위반의 세부 원인.</param>
    /// <param name="description">사람이 읽는 보충 설명.</param>
    public static ConnectionCloseInfo ProtocolError(ErrorCode errorCode, string? description = null) =>
        new(CloseReason.ProtocolError, errorCode, description);

    /// <inheritdoc />
    public bool Equals(ConnectionCloseInfo other) =>
        Reason == other.Reason
        && ErrorCode == other.ErrorCode
        && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ConnectionCloseInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Reason, ErrorCode, Description);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(ConnectionCloseInfo left, ConnectionCloseInfo right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(ConnectionCloseInfo left, ConnectionCloseInfo right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        Description is null
            ? $"{Reason}({ErrorCode})"
            : $"{Reason}({ErrorCode}): {Description}";
}
