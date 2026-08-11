using System;

namespace ChServerM.RealTime.Tests;

/// <summary>
/// 테스트가 시각을 직접 움직이는 <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <c>ChServerM.Core.Tests</c> 의 동명 타입과 같은 패턴이다 — 틱 주파수를 임의 값으로
/// 바꿔가며(레거시의 3,579,545Hz 오차 사례 재현 등) 시각을 결정적으로 밀기 위해서다.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly long _frequency;
    private long _timestamp;

    public ManualTimeProvider(long frequency = 10_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _frequency = frequency;
    }

    public override long TimestampFrequency => _frequency;

    public override long GetTimestamp() => _timestamp;

    /// <summary>단조 시각을 앞으로 민다.</summary>
    public void Advance(TimeSpan delta)
    {
        _timestamp += (long)(delta.TotalSeconds * _frequency);
    }

    /// <summary>단조 시각을 raw 틱 단위로 민다. 반올림 없는 정밀 제어용.</summary>
    public void AdvanceRaw(long rawTicks)
    {
        _timestamp += rawTicks;
    }
}
