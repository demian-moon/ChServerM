using System;

namespace ChServerM.RealTime;

/// <summary>
/// 틱 루프 통계의 스냅샷. <see cref="TickLoop.Statistics"/>가 만든다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 틱 예산 초과 감지.</b> 고정 타임스텝에서 "한 틱이 예산(간격)을 넘었다"는
/// 서버가 실시간을 따라가지 못한다는 첫 신호다. 이 신호가 관측되지 않으면 증상은
/// 곧바로 "게임이 느려졌다"로 건너뛴다. 초과(<see cref="OverrunTicks"/>)·
/// 건너뜀(<see cref="SkippedTicks"/>)·지터(<see cref="MaxStartDrift"/>)를 상시 노출한다.
/// </para>
/// <para>
/// <b>일관성 규약.</b> 필드 간 원자적 스냅샷은 아니다 — 각 값은 개별로 최신이지만
/// 서로 다른 순간의 값일 수 있다. 통계 용도로는 충분하고, 그 이상은 비용이 아깝다.
/// </para>
/// </remarks>
public readonly struct TickLoopStatistics : IEquatable<TickLoopStatistics>
{
    internal TickLoopStatistics(
        long totalTicks,
        long overrunTicks,
        long skippedTicks,
        long faultedTicks,
        TimeSpan lastTickDuration,
        TimeSpan maxTickDuration,
        TimeSpan maxStartDrift)
    {
        TotalTicks = totalTicks;
        OverrunTicks = overrunTicks;
        SkippedTicks = skippedTicks;
        FaultedTicks = faultedTicks;
        LastTickDuration = lastTickDuration;
        MaxTickDuration = maxTickDuration;
        MaxStartDrift = maxStartDrift;
    }

    /// <summary>실행된 틱 수. 건너뛴 틱은 세지 않는다.</summary>
    public long TotalTicks { get; }

    /// <summary>실행 시간이 예산(틱 간격)을 넘은 틱 수.</summary>
    public long OverrunTicks { get; }

    /// <summary>캐치업 상한을 넘어 실행하지 않고 건너뛴 틱 수. 0이 아니면 서버가 밀렸던 것이다.</summary>
    public long SkippedTicks { get; }

    /// <summary>핸들러 예외로 끝난 틱 수.</summary>
    public long FaultedTicks { get; }

    /// <summary>가장 최근 틱의 실행 시간.</summary>
    public TimeSpan LastTickDuration { get; }

    /// <summary>지금까지 가장 길었던 틱의 실행 시간.</summary>
    public TimeSpan MaxTickDuration { get; }

    /// <summary>지금까지 가장 컸던 시작 지연(예정 대비). 지터의 최악값이다.</summary>
    public TimeSpan MaxStartDrift { get; }

    /// <inheritdoc />
    public bool Equals(TickLoopStatistics other) =>
        TotalTicks == other.TotalTicks &&
        OverrunTicks == other.OverrunTicks &&
        SkippedTicks == other.SkippedTicks &&
        FaultedTicks == other.FaultedTicks &&
        LastTickDuration == other.LastTickDuration &&
        MaxTickDuration == other.MaxTickDuration &&
        MaxStartDrift == other.MaxStartDrift;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TickLoopStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        TotalTicks, OverrunTicks, SkippedTicks, FaultedTicks,
        LastTickDuration, MaxTickDuration, MaxStartDrift);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(TickLoopStatistics left, TickLoopStatistics right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(TickLoopStatistics left, TickLoopStatistics right) => !left.Equals(right);
}
