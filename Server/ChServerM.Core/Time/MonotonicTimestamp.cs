using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChServerM.Time;

/// <summary>
/// 단조 증가 시각. <b>경과 시간 측정 전용</b>이다.
/// </summary>
/// <remarks>
/// <para>
/// 이 값은 <b>절대 시각이 아니다.</b> 기준점은 임의(대개 부팅 시각)이고 프로세스·머신마다 다르다.
/// 따라서 다음을 하면 안 된다.
/// </para>
/// <list type="bullet">
///   <item><description><b>영속화 금지</b> — 재시작하면 의미를 잃는다</description></item>
///   <item><description><b>노드 간 비교 금지</b> — 머신마다 기준이 다르다</description></item>
///   <item><description><b>사람이 읽는 시각으로 표시 금지</b> — 그 용도는 <see cref="TimeProvider.GetUtcNow"/></description></item>
/// </list>
/// <para>
/// 반대로 <b>경과 측정에는 이것만 쓴다.</b> 벽시계는 NTP 보정으로 <b>뒤로 갈 수 있어</b>
/// 타임아웃·레이턴시 계산에 부적합하다.
/// </para>
/// <para>
/// 두 시각을 타입으로 분리한 이유: 레거시는 <c>Stopwatch</c> 틱, <c>TickTimeM.GTick</c>,
/// <c>DateTime.UtcNow</c> 세 가지를 섞어 썼고 상호 변환에서 오차와 의미 혼동이 생겼다.
/// </para>
/// <para>
/// <see cref="TimeProvider.TimestampFrequency"/> 나눗셈은 이 타입 <b>내부에만</b> 존재한다.
/// 호출자는 <see cref="TimeSpan"/>만 다룬다.
/// </para>
/// </remarks>
public readonly struct MonotonicTimestamp : IEquatable<MonotonicTimestamp>, IComparable<MonotonicTimestamp>
{
    private readonly long _raw;

    private MonotonicTimestamp(long raw) => _raw = raw;

    /// <summary>설정되지 않은 값.</summary>
    public static MonotonicTimestamp None => default;

    /// <summary>현재 시각을 읽는다.</summary>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MonotonicTimestamp Now(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new MonotonicTimestamp(timeProvider.GetTimestamp());
    }

    /// <summary><see cref="TimeProvider.GetTimestamp"/> 원본값을 그대로 감싼다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MonotonicTimestamp FromRaw(long raw) => new(raw);

    /// <summary>감싸고 있는 원본값.</summary>
    /// <remarks>전송·저장하지 않는다. 진단과 상호운용을 위해서만 노출한다.</remarks>
    public long Raw => _raw;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _raw == 0;

    /// <summary>이 시각부터 <paramref name="later"/>까지 경과한 시간.</summary>
    /// <param name="timeProvider">두 값을 만든 것과 같은 시간 원본.</param>
    /// <param name="later">나중 시각.</param>
    /// <returns>
    /// 경과 시간. <paramref name="later"/>가 이 시각보다 이르면 <b>음수</b>를 반환한다.
    /// </returns>
    /// <remarks>
    /// 음수를 0으로 뭉개지 않는다. 단조 시각에서 음수 경과는 <b>버그의 신호</b>이므로
    /// 호출자가 볼 수 있어야 한다. 레거시는 이것을 0으로 뭉개 시계 역행과 버그를 감췄다.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan ElapsedTo(TimeProvider timeProvider, MonotonicTimestamp later)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return timeProvider.GetElapsedTime(_raw, later._raw);
    }

    /// <summary>이 시각부터 지금까지 경과한 시간.</summary>
    /// <param name="timeProvider">이 값을 만든 것과 같은 시간 원본.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan ElapsedSince(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return timeProvider.GetElapsedTime(_raw);
    }

    /// <summary>이 시각에 <paramref name="delta"/>를 더한 시각.</summary>
    /// <param name="timeProvider">이 값을 만든 것과 같은 시간 원본.</param>
    /// <param name="delta">더할 시간. 음수면 과거로 간다.</param>
    /// <remarks>타임아웃 만료 시각을 미리 계산할 때 쓴다.</remarks>
    public MonotonicTimestamp Add(TimeProvider timeProvider, TimeSpan delta)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        long ticks = (long)(delta.TotalSeconds * timeProvider.TimestampFrequency);
        return new MonotonicTimestamp(_raw + ticks);
    }

    /// <summary>이 시각이 <paramref name="deadline"/>을 지났는지 검사한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasPassed(MonotonicTimestamp deadline) => _raw >= deadline._raw;

    /// <inheritdoc />
    public bool Equals(MonotonicTimestamp other) => _raw == other._raw;

    /// <inheritdoc />
    public int CompareTo(MonotonicTimestamp other) => _raw.CompareTo(other._raw);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MonotonicTimestamp other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _raw.GetHashCode();

    /// <summary>두 시각이 같은지 비교한다.</summary>
    public static bool operator ==(MonotonicTimestamp left, MonotonicTimestamp right) => left.Equals(right);

    /// <summary>두 시각이 다른지 비교한다.</summary>
    public static bool operator !=(MonotonicTimestamp left, MonotonicTimestamp right) => !left.Equals(right);

    /// <summary>왼쪽이 더 이른지 비교한다.</summary>
    public static bool operator <(MonotonicTimestamp left, MonotonicTimestamp right) => left._raw < right._raw;

    /// <summary>왼쪽이 더 늦은지 비교한다.</summary>
    public static bool operator >(MonotonicTimestamp left, MonotonicTimestamp right) => left._raw > right._raw;

    /// <summary>왼쪽이 같거나 더 이른지 비교한다.</summary>
    public static bool operator <=(MonotonicTimestamp left, MonotonicTimestamp right) => left._raw <= right._raw;

    /// <summary>왼쪽이 같거나 더 늦은지 비교한다.</summary>
    public static bool operator >=(MonotonicTimestamp left, MonotonicTimestamp right) => left._raw >= right._raw;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"mono:{_raw}");
}
