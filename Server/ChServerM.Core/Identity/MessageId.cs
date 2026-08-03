using System;
using System.Globalization;

namespace ChServerM.Identity;

/// <summary>
/// 메시지(패킷) 타입을 가리키는 강타입 식별자.
/// </summary>
/// <remarks>
/// <para>ID 공간을 앱과 프레임워크로 나눈다.</para>
/// <list type="table">
///   <item><term>0</term><description><see cref="None"/> — 사용 금지. 초기화 누락을 잡는 센티넬</description></item>
///   <item><term>1 ~ 40000</term><description>앱이 자유롭게 정의</description></item>
///   <item><term>40001 ~ 65535</term><description>프레임워크 예약</description></item>
/// </list>
/// <para>
/// 레거시는 FlatBuffers가 기본값을 직렬화하지 않아 <c>0</c>을 쓰면 헤더 길이가 달라졌다.
/// 고정 헤더로 바꾸면서 그 제약은 사라졌지만, <c>0</c>은 여전히 <b>설정하지 않은 값</b>을 뜻하는
/// 센티넬로 남긴다.
/// </para>
/// </remarks>
public readonly struct MessageId : IEquatable<MessageId>, IComparable<MessageId>
{
    /// <summary>앱이 쓸 수 있는 첫 번째 값.</summary>
    public const ushort AppRangeStart = 1;

    /// <summary>앱이 쓸 수 있는 마지막 값.</summary>
    public const ushort AppRangeEnd = 40000;

    /// <summary>프레임워크가 예약한 첫 번째 값.</summary>
    public const ushort FrameworkRangeStart = 40001;

    private readonly ushort _value;

    /// <summary>수치로 메시지 식별자를 만든다.</summary>
    public MessageId(ushort value) => _value = value;

    /// <summary>설정되지 않은 값.</summary>
    public static MessageId None => default;

    /// <summary>원본 수치.</summary>
    public ushort Value => _value;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _value == 0;

    /// <summary>앱 예약 범위에 속하는지 여부.</summary>
    public bool IsAppRange => _value is >= AppRangeStart and <= AppRangeEnd;

    /// <summary>프레임워크 예약 범위에 속하는지 여부.</summary>
    public bool IsFrameworkRange => _value >= FrameworkRangeStart;

    /// <inheritdoc />
    public bool Equals(MessageId other) => _value == other._value;

    /// <inheritdoc />
    public int CompareTo(MessageId other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MessageId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value;

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(MessageId left, MessageId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(MessageId left, MessageId right) => !left.Equals(right);

    /// <summary>왼쪽 번호가 더 작은지 비교한다.</summary>
    public static bool operator <(MessageId left, MessageId right) => left._value < right._value;

    /// <summary>왼쪽 번호가 더 큰지 비교한다.</summary>
    public static bool operator >(MessageId left, MessageId right) => left._value > right._value;

    /// <summary>왼쪽 번호가 같거나 더 작은지 비교한다.</summary>
    public static bool operator <=(MessageId left, MessageId right) => left._value <= right._value;

    /// <summary>왼쪽 번호가 같거나 더 큰지 비교한다.</summary>
    public static bool operator >=(MessageId left, MessageId right) => left._value >= right._value;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"msg:{_value}");
}

/// <summary>프레임워크가 예약한 메시지 식별자.</summary>
/// <remarks>앱은 <see cref="MessageId.AppRangeStart"/>~<see cref="MessageId.AppRangeEnd"/>를 쓴다.</remarks>
public static class FrameworkMessageIds
{
    /// <summary>연결 유지 확인 요청.</summary>
    public static MessageId Heartbeat => new(40001);

    /// <summary>연결 유지 확인 응답.</summary>
    public static MessageId HeartbeatAck => new(40002);

    /// <summary>정상 종료 요청.</summary>
    public static MessageId DisconnectRequest => new(40003);
}
