using System;
using System.Globalization;
using ChServerM.Identity;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 식별자. 강타입 <c>readonly struct</c> — Core 의 ID 규약(Phase 1)과 같은 계열이다.
/// </summary>
/// <remarks>
/// <para>
/// Core 의 <c>Identity</c>에 두지 않는 이유: 룸은 선택 축이고, Core 는 이 축의 존재를
/// 알면 안 된다(ADR-0004). 대신 같은 패턴(<c>readonly struct</c> + <c>IEquatable</c> +
/// <see cref="ToPartitionKey"/>)을 따른다 — 룸 단위 작업을 파티션 실행 모델에 고정할 때 쓴다.
/// </para>
/// <para><c>0</c> 은 미설정 센티널(<see cref="None"/>)이다. 디렉터리가 등록을 거부한다.</para>
/// </remarks>
public readonly struct RoomId : IEquatable<RoomId>
{
    private readonly ulong _value;

    /// <summary>식별자를 만든다.</summary>
    /// <param name="value">0 이 아닌 값. 0 은 <see cref="None"/> 센티널이다.</param>
    public RoomId(ulong value) => _value = value;

    /// <summary>설정되지 않은 값.</summary>
    public static RoomId None => default;

    /// <summary>감싸고 있는 원본값.</summary>
    public ulong Value => _value;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _value == 0;

    /// <summary>룸 단위 파티셔닝에 쓸 키를 만든다.</summary>
    public PartitionKey ToPartitionKey() => PartitionKey.FromValue(_value);

    /// <inheritdoc />
    public bool Equals(RoomId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RoomId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(RoomId left, RoomId right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(RoomId left, RoomId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"room:{_value}");
}
