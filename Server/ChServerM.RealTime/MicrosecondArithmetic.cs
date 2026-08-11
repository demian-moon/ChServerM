using System;

namespace ChServerM.RealTime;

/// <summary>
/// 단조 시각 원본값(raw)과 마이크로초·<see cref="TimeSpan"/> 사이의 정밀 변환.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 이 어셈블리의 시간 단위는 <b>마이크로초 고정 정수</b>다. 레거시는
/// <c>Stopwatch.Frequency</c> 를 그대로 노출해 두 가지 결함을 만들었다 — 이 타입이 둘 다 막는다.
/// </para>
/// <list type="number">
///   <item><description>
///   <b><c>double</c> 정밀도 초과</b> — <c>GetTimestamp() * 1000.0</c> 은 10MHz 머신 1년 가동이면
///   약 3.15×10¹⁷ 로 <c>double</c> 정수 정밀도 한계(2⁵³ ≈ 9×10¹⁵)를 넘어 밀리초가 뭉개진다
///   (레거시 <c>TickTimeM.GTickMs</c>). 여기서는 <c>double</c> 을 쓰지 않는다.
///   </description></item>
///   <item><description>
///   <b>정수 나눗셈 오차 누적</b> — <c>Frequency / 1000</c> 은 주파수가 1000 의 배수가 아니면
///   오차가 누적된다(3,579,545Hz 에서 0.015% → 30일 타이머 약 6.5분 오차, 레거시
///   <c>TimeEventSchedulerM._ticksPerMs</c>). 여기서는 몫·나머지 분해로 정확하게 변환한다.
///   </description></item>
/// </list>
/// <para>
/// <b>스레드 규약.</b> 순수 함수만 있다. 어디서든 호출해도 된다.
/// </para>
/// </remarks>
internal static class MicrosecondArithmetic
{
    /// <summary>1초의 마이크로초 수.</summary>
    internal const long MicrosPerSecond = 1_000_000;

    /// <summary>
    /// <see cref="ToMicros"/>가 오버플로 없이 동작하는 주파수 상한.
    /// 나머지 항 <c>r * 1e6</c> 에서 <c>r &lt; frequency</c> 이므로 이 상한이면 안전하다.
    /// 실제 하드웨어는 10MHz(QPC)~3.5GHz(TSC 직접) 수준이라 여유가 6천 배 이상이다.
    /// </summary>
    internal const long MaxSupportedFrequency = long.MaxValue / MicrosPerSecond;

    /// <summary>주파수가 지원 범위인지 검증한다. 생성자에서 한 번 호출한다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">주파수가 1 미만이거나 상한을 넘을 때.</exception>
    internal static void ValidateFrequency(long frequency)
    {
        if (frequency < 1 || frequency > MaxSupportedFrequency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequency),
                frequency,
                $"TimestampFrequency 는 1 이상 {MaxSupportedFrequency} 이하여야 한다.");
        }
    }

    /// <summary>raw 경과값을 마이크로초로 변환한다. <c>double</c> 없이 정확하다.</summary>
    /// <param name="rawDelta">두 <c>GetTimestamp()</c> 값의 차이. 음수면 0 방향으로 절단된다.</param>
    /// <param name="frequency"><see cref="TimeProvider.TimestampFrequency"/>. 검증은 호출자 책임.</param>
    /// <remarks>
    /// <c>rawDelta * 1e6</c> 은 하루치 경과만으로도 오버플로하므로 몫·나머지로 분해한다.
    /// 오차는 1µs 미만 절단뿐이고 누적되지 않는다 — 항상 전체 경과값에서 다시 계산하기 때문이다.
    /// </remarks>
    internal static long ToMicros(long rawDelta, long frequency)
    {
        long quotient = rawDelta / frequency;
        long remainder = rawDelta % frequency;
        return (quotient * MicrosPerSecond) + (remainder * MicrosPerSecond / frequency);
    }

    /// <summary><see cref="TimeSpan"/>을 raw 틱 수로 변환한다. 간격을 미리 계산해 둘 때 쓴다.</summary>
    /// <param name="value">변환할 시간. 음수 금지는 호출자(옵션 검증)가 보장한다.</param>
    /// <param name="frequency"><see cref="TimeProvider.TimestampFrequency"/>.</param>
    /// <remarks>
    /// <c>Ticks * frequency</c> 는 하루(8.64×10¹¹ 틱) × 10MHz 에서 이미 <c>long</c> 을 넘으므로
    /// 초 단위 몫·나머지로 분해한다. 절단 오차는 raw 1틱(QPC 기준 100ns) 미만이다.
    /// </remarks>
    internal static long ToRawTicks(TimeSpan value, long frequency)
    {
        long ticks = value.Ticks;
        long quotient = ticks / TimeSpan.TicksPerSecond;
        long remainder = ticks % TimeSpan.TicksPerSecond;
        return (quotient * frequency) + (remainder * frequency / TimeSpan.TicksPerSecond);
    }
}
