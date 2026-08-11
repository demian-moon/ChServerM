using System;
using ChServerM.Time;

namespace ChServerM.RealTime;

/// <summary>
/// 틱 하나의 실행 문맥. <see cref="ITickHandler.OnTick"/>이 받는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 고정 타임스텝의 핵심 계약은 "시뮬레이션 시간은 예정 시각
/// (<see cref="ScheduledAt"/>) 기준으로 흐른다"이다. 실제 시작 시각(<see cref="StartedAt"/>)을
/// 쓰면 OS 스케줄링 지터가 시뮬레이션에 그대로 스며든다 — 둘을 분리해 노출하는 이유다.
/// </para>
/// <para>
/// <b>수명 규약.</b> <c>readonly struct</c> 값 복사라 핸들러 밖으로 가져가도 안전하다.
/// 할당은 없다.
/// </para>
/// </remarks>
public readonly struct TickContext : IEquatable<TickContext>
{
    internal TickContext(
        long tickNumber,
        MonotonicTimestamp scheduledAt,
        MonotonicTimestamp startedAt,
        TimeSpan interval,
        TimeSpan startDrift)
    {
        TickNumber = tickNumber;
        ScheduledAt = scheduledAt;
        StartedAt = startedAt;
        Interval = interval;
        StartDrift = startDrift;
    }

    /// <summary>0부터 시작하는 틱 순번. 건너뛴 틱은 순번을 소비하므로 연속이 아닐 수 있다.</summary>
    public long TickNumber { get; }

    /// <summary>이 틱이 실행됐어야 할 예정 시각. 시뮬레이션 시간의 기준이다.</summary>
    public MonotonicTimestamp ScheduledAt { get; }

    /// <summary>핸들러가 실제로 호출된 시각.</summary>
    public MonotonicTimestamp StartedAt { get; }

    /// <summary>고정 타임스텝 간격. 이 틱의 실행 예산이기도 하다.</summary>
    public TimeSpan Interval { get; }

    /// <summary>예정 대비 시작 지연(<see cref="StartedAt"/> − <see cref="ScheduledAt"/>). 지터의 관측값이다.</summary>
    public TimeSpan StartDrift { get; }

    /// <inheritdoc />
    public bool Equals(TickContext other) =>
        TickNumber == other.TickNumber &&
        ScheduledAt == other.ScheduledAt &&
        StartedAt == other.StartedAt &&
        Interval == other.Interval &&
        StartDrift == other.StartDrift;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TickContext other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(TickNumber, ScheduledAt, StartedAt, Interval, StartDrift);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(TickContext left, TickContext right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(TickContext left, TickContext right) => !left.Equals(right);
}
