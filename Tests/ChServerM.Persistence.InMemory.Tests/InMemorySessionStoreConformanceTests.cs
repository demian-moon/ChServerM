using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Persistence.Conformance;
using ChServerM.Sessions;

namespace ChServerM.Persistence.InMemory.Tests;

/// <summary>
/// 인메모리 참조 구현이 세션 축의 계약을 만족하는지 검증한다.
/// </summary>
/// <remarks>
/// 단언은 <see cref="SessionStoreConformanceTests"/> 에 있다 — Redis 어댑터가 통과하는 것과
/// <b>완전히 같은 스위트</b>다. 두 구현이 같은 테스트를 통과해야 축 교체가 성립한다(ADR-0004).
/// 인메모리 고유 사항(청소 타이머·<c>Count</c>·설정 검증·할당 고정)은
/// <c>InMemorySessionStoreTests</c> 에 있다.
/// </remarks>
public sealed class InMemorySessionStoreConformanceTests : SessionStoreConformanceTests, IDisposable
{
    private readonly ConformanceTimeProvider _time = new();
    private readonly InMemorySessionStore _store;

    public InMemorySessionStoreConformanceTests()
    {
        // 청소 타이머를 끈다 — 만료 판정은 지연 판정만으로도 계약을 만족해야 한다.
        // (청소가 실제로 회수하는지는 구현 고유 테스트가 따로 본다.)
        _store = new InMemorySessionStore(
            new InMemorySessionStoreOptions { SweepInterval = null }, _time);
    }

    /// <inheritdoc/>
    protected override ISessionStore Store => _store;

    /// <inheritdoc/>
    protected override Task AdvanceAsync(TimeSpan delta)
    {
        // 가짜 시계라 즉시 진행한다 — Redis 와 달리 기다릴 이유가 없다.
        _time.Advance(delta);
        return Task.CompletedTask;
    }

    public void Dispose() => _store.Dispose();

    /// <summary>테스트가 시간을 직접 움직인다.</summary>
    private sealed class ConformanceTimeProvider : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
    }
}
