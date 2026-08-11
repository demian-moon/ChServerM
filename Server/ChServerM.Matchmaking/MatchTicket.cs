using System;
using System.Diagnostics;
using System.Globalization;
using ChServerM.Time;

namespace ChServerM.Matchmaking;

/// <summary>
/// 매치 티켓 식별자.
/// </summary>
/// <remarks>
/// 강타입 ID 규약(Phase 1)을 따른다 — 원시 <see cref="ulong"/> 이 티켓·룸·커넥션 사이를
/// 오가며 뒤섞이는 것을 컴파일 타임에 막는다. 값의 발급은 호출자 몫이다(커넥션당 1티켓이면
/// <c>ConnectionId</c> 파생, 파티면 파티 ID — 큐는 유일성만 검사한다).
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct MatchTicketId : IEquatable<MatchTicketId>
{
    private readonly ulong _value;

    /// <summary>원시 값으로 ID 를 만든다.</summary>
    public MatchTicketId(ulong value) => _value = value;

    /// <summary>원시 값.</summary>
    public ulong Value => _value;

    /// <inheritdoc />
    public bool Equals(MatchTicketId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MatchTicketId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>같은 티켓인가.</summary>
    public static bool operator ==(MatchTicketId left, MatchTicketId right) => left.Equals(right);

    /// <summary>다른 티켓인가.</summary>
    public static bool operator !=(MatchTicketId left, MatchTicketId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ticket:{_value}");
}

/// <summary>
/// 매치 대기열의 항목 하나 — 파티(1~팀 정원 명)가 원자 단위다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 큐가 매칭에 필요한 최소 정보만 나르게 한다: 식별자·레이팅·인원·등록
/// 시각. 플레이어 목록·세션 같은 도메인 정보는 싣지 않는다 — 호출자가 티켓 ID 로 자기
/// 상태를 찾는다. 프레임워크가 도메인을 알기 시작하면 축 교체 가능성이 죽는다(ADR-0004).
/// </para>
/// <para>
/// <b>파티는 쪼개지지 않는다</b>(ADR-0068 결정 4). <see cref="PartySize"/> 가 팀 정원을
/// 넘으면 등록이 거부된다.
/// </para>
/// <para>
/// <see cref="Rating"/> 은 값 하나다 — Glicko 등 공식과 결과 반영은 프레임워크 밖이다
/// (ADR-0068 결정 5). 파티의 대표 레이팅(평균·최대 등)을 정하는 것도 호출자 몫이다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct MatchTicket : IEquatable<MatchTicket>
{
    /// <summary>티켓을 만든다.</summary>
    /// <param name="id">티켓 식별자. 큐 안에서 유일해야 한다.</param>
    /// <param name="rating">매칭 기준 레이팅. NaN·무한대는 등록이 거부된다.</param>
    /// <param name="partySize">파티 인원(1 이상, 팀 정원 이하).</param>
    /// <param name="enqueuedAt">등록 시각(단조 시각). 창 확장과 만료 판정의 기준이 된다.</param>
    public MatchTicket(MatchTicketId id, double rating, int partySize, MonotonicTimestamp enqueuedAt)
    {
        Id = id;
        Rating = rating;
        PartySize = partySize;
        EnqueuedAt = enqueuedAt;
    }

    /// <summary>티켓 식별자.</summary>
    public MatchTicketId Id { get; }

    /// <summary>매칭 기준 레이팅.</summary>
    public double Rating { get; }

    /// <summary>파티 인원.</summary>
    public int PartySize { get; }

    /// <summary>등록 시각(단조 시각).</summary>
    public MonotonicTimestamp EnqueuedAt { get; }

    /// <inheritdoc />
    public bool Equals(MatchTicket other) =>
        Id == other.Id
        && Rating.Equals(other.Rating)
        && PartySize == other.PartySize
        && EnqueuedAt == other.EnqueuedAt;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MatchTicket other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Rating, PartySize, EnqueuedAt);

    /// <summary>같은 티켓 값인가.</summary>
    public static bool operator ==(MatchTicket left, MatchTicket right) => left.Equals(right);

    /// <summary>다른 티켓 값인가.</summary>
    public static bool operator !=(MatchTicket left, MatchTicket right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Id} r={Rating} x{PartySize}");
}
