using System;

namespace ChServerM.RealTime;

/// <summary>
/// 타이머 휠 통계의 스냅샷. <see cref="TimerWheel.Statistics"/>가 만든다.
/// </summary>
/// <remarks>
/// 거부(<see cref="RejectedSchedules"/>)와 콜백 실패(<see cref="FaultedCallbacks"/>)가
/// 여기 있는 이유: 조용한 유실은 관측되지 않으면 존재하지 않는 것과 같다(CLAUDE.md 9.6).
/// 필드 간 원자적 스냅샷은 아니다 — <see cref="TickLoopStatistics"/>와 같은 규약이다.
/// </remarks>
public readonly struct TimerWheelStatistics : IEquatable<TimerWheelStatistics>
{
    internal TimerWheelStatistics(
        long scheduledTimers,
        long firedTimers,
        long canceledTimers,
        long rejectedSchedules,
        long faultedCallbacks,
        long pendingTimers)
    {
        ScheduledTimers = scheduledTimers;
        FiredTimers = firedTimers;
        CanceledTimers = canceledTimers;
        RejectedSchedules = rejectedSchedules;
        FaultedCallbacks = faultedCallbacks;
        PendingTimers = pendingTimers;
    }

    /// <summary>수락된 예약 수(누적).</summary>
    public long ScheduledTimers { get; }

    /// <summary>만료로 발화한 타이머 수(누적).</summary>
    public long FiredTimers { get; }

    /// <summary>발화 전에 취소된 타이머 수(누적, 셧다운 드레인 포함).</summary>
    public long CanceledTimers { get; }

    /// <summary>상한 초과로 거부된 예약 수(누적).</summary>
    public long RejectedSchedules { get; }

    /// <summary>예외로 끝난 콜백 수(누적, 만료·취소 콜백 합산).</summary>
    public long FaultedCallbacks { get; }

    /// <summary>살아 있는(아직 발화·취소되지 않은) 타이머 수(현재값).</summary>
    public long PendingTimers { get; }

    /// <inheritdoc />
    public bool Equals(TimerWheelStatistics other) =>
        ScheduledTimers == other.ScheduledTimers &&
        FiredTimers == other.FiredTimers &&
        CanceledTimers == other.CanceledTimers &&
        RejectedSchedules == other.RejectedSchedules &&
        FaultedCallbacks == other.FaultedCallbacks &&
        PendingTimers == other.PendingTimers;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TimerWheelStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        ScheduledTimers, FiredTimers, CanceledTimers, RejectedSchedules, FaultedCallbacks, PendingTimers);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(TimerWheelStatistics left, TimerWheelStatistics right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(TimerWheelStatistics left, TimerWheelStatistics right) => !left.Equals(right);
}
