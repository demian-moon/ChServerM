using System;
using System.Threading;

namespace ChServerM.RealTime;

/// <summary>
/// 인터벌 게이트 — "마지막 통과 후 일정 시간이 지났을 때만 실행"을 원자적으로 판정한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 틱 루프 안에서 하위 작업의 빈도를 개별 조절하거나(매 틱이 아니라 1초에
/// 한 번만), 반복 경고 로그를 표본화할 때 쓴다. 레거시 <c>ElapsedTimeManM</c>의 승계다 —
/// "인터벌을 raw 틱으로 미리 변환해 매 호출 변환을 없앤다"는 설계를 그대로 가져왔다.
/// </para>
/// <para>
/// <b>막는 레거시 결함.</b> 원본은 검사(<c>IsElapsed</c>)와 갱신(<c>RefreshLastUpdateTime</c>)이
/// 분리돼 있어 ① 호출자가 갱신을 잊으면 매번 통과하고 ② 두 스레드가 동시에 검사하면 중복
/// 실행됐다. <see cref="TryConsume"/>는 검사+갱신이 CAS 한 번이라 둘 다 불가능하다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전. 여러 스레드가 동시에 불러도 인터벌당 정확히 하나만
/// <see langword="true"/>를 받는다.
/// </para>
/// </remarks>
public sealed class IntervalGate
{
    // GetTimestamp() 가 어떤 값이든 정상 시각일 수 있으므로 0 이 아니라 MinValue 를 미소비 센티널로 쓴다.
    private const long NeverConsumed = long.MinValue;

    private readonly TimeProvider _timeProvider;
    private readonly long _intervalRaw;
    private long _lastConsumedRaw = NeverConsumed;

    /// <summary>게이트를 만든다. 첫 <see cref="TryConsume"/>는 항상 통과한다.</summary>
    /// <param name="interval">통과 사이의 최소 간격.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/>이 0 이하일 때.</exception>
    public IntervalGate(TimeSpan interval, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), interval, "간격은 0보다 커야 한다. 항상 실행할 거면 게이트를 쓰지 않는다.");
        }

        _timeProvider = timeProvider;
        long frequency = timeProvider.TimestampFrequency;
        MicrosecondArithmetic.ValidateFrequency(frequency);
        _intervalRaw = Math.Max(1, MicrosecondArithmetic.ToRawTicks(interval, frequency));
    }

    /// <summary>간격이 지났으면 소비하고 <see langword="true"/>를 반환한다. 검사와 갱신이 원자적이다.</summary>
    public bool TryConsume()
    {
        long nowRaw = _timeProvider.GetTimestamp();
        while (true)
        {
            long last = Volatile.Read(ref _lastConsumedRaw);
            if (last != NeverConsumed && nowRaw - last < _intervalRaw)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastConsumedRaw, nowRaw, last) == last)
            {
                return true;
            }

            // CAS 실패 = 다른 스레드가 방금 소비했다. 재검사는 대개 false 로 즉시 끝난다 — 스핀 불필요.
        }
    }
}
