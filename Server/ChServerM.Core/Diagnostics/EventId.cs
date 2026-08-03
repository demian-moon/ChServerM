using System;
using System.Globalization;

namespace ChServerM.Diagnostics;

/// <summary>
/// 로그 이벤트를 가리키는 안정 식별자.
/// </summary>
/// <remarks>
/// <para>
/// 숫자가 <b>정본</b>이고 이름은 사람이 읽기 위한 것이다. 메시지 문구를 바꿔도 숫자가 같으면
/// 같은 이벤트다 — 로그 검색·알람 규칙이 문구 변경에 깨지지 않는다.
/// </para>
/// <para>
/// 레거시는 로그를 문자열 보간으로만 남겨 <b>기계가 집계할 수 있는 축이 하나도 없었다.</b>
/// </para>
/// <para>번호 대역은 <see cref="DiagnosticNames"/>에 정리한다.</para>
/// </remarks>
public readonly struct EventId : IEquatable<EventId>
{
    private readonly int _id;
    private readonly string? _name;

    /// <summary>번호와 이름으로 이벤트 식별자를 만든다.</summary>
    /// <param name="id">안정 번호. 한 번 배정하면 바꾸지 않는다.</param>
    /// <param name="name">사람이 읽는 이름. 로그 문구와 별개다.</param>
    public EventId(int id, string? name = null)
    {
        _id = id;
        _name = name;
    }

    /// <summary>안정 번호.</summary>
    public int Id => _id;

    /// <summary>사람이 읽는 이름. 지정하지 않았으면 <see langword="null"/>.</summary>
    public string? Name => _name;

    /// <inheritdoc />
    /// <remarks>이름은 비교하지 않는다. 정본은 번호다.</remarks>
    public bool Equals(EventId other) => _id == other._id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EventId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _id;

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(EventId left, EventId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(EventId left, EventId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        _name ?? _id.ToString(CultureInfo.InvariantCulture);
}
