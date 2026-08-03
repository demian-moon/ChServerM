using System;

namespace ChServerM.Core.Tests.Time;

/// <summary>
/// 테스트가 시각을 직접 움직이는 <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> 을 쓰지 않는다. 이 테스트가 확인하려는 것은
/// <c>MonotonicTimestamp</c> 가 <see cref="TimeProvider"/> 계약만으로 동작하는가이고,
/// 그러려면 <b>틱 주파수를 임의 값으로 바꿔가며</b> 밀어볼 수 있어야 한다.
/// </para>
/// <para>일부러 벽시계와 단조 시각을 <b>따로</b> 움직일 수 있게 했다 — NTP 역행을 재현하기 위해서다.</para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly long _frequency;
    private long _timestamp;
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public ManualTimeProvider(long frequency = 10_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _frequency = frequency;
    }

    public override long TimestampFrequency => _frequency;

    public override long GetTimestamp() => _timestamp;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>단조 시각만 앞으로 민다. 벽시계는 그대로 둔다.</summary>
    public void AdvanceMonotonic(TimeSpan delta)
    {
        _timestamp += (long)(delta.TotalSeconds * _frequency);
    }

    /// <summary>벽시계만 움직인다. 단조 시각은 그대로 둔다.</summary>
    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
}
