using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Persistence.InMemory.Tests;

/// <summary>
/// 인메모리 저장소의 <b>구현 고유</b> 동작을 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>계약 자체는 여기 없다.</b> 값 의미·CAS·만료는 <c>SessionStoreConformanceTests</c> 가
/// 검증하며 Redis 어댑터도 <b>똑같은 스위트</b>를 통과한다
/// (<see cref="InMemorySessionStoreConformanceTests"/>). 여기에 계약 테스트를 복사해 두면
/// 두 곳이 갈릴 때 어느 쪽이 진실인지 알 수 없게 된다.
/// </para>
/// <para>
/// 여기 남는 것은 <b>이 구현에만 있는 것</b>이다: 청소 타이머, <c>Count</c>, 설정 검증,
/// 수명(Dispose), 그리고 <b>할당 고정</b>(원격 저장소에는 의미가 다른 지표다).
/// </para>
/// </remarks>
public sealed class InMemorySessionStoreTests
{
    private static SessionId Id(int seed) => new(new ObjectId(seed));

    private static byte[] Bytes(params byte[] value) => value;

    private static InMemorySessionStore Create(ManualTimeProvider? time = null, TimeSpan? sweep = null) =>
        new(new InMemorySessionStoreOptions { SweepInterval = sweep }, time ?? new ManualTimeProvider());

    // ── 청소 (이 구현에만 있다 — Redis 는 서버가 회수한다) ────────────────────

    [Fact]
    public async Task Sweep_actually_reclaims_abandoned_sessions()
    {
        // ★★ 지연 만료만으로는 새는 것을 막지 못한다 — 다시 조회되지 않는 세션이 정확히
        // 끊긴 클라이언트의 상태이고, 그것이 쌓이는 것이 OOM 이다.
        // 계약("만료된 항목은 없는 것과 같다")은 지연 판정만으로도 만족되지만, **실제로
        // 회수하는지**는 이 구현의 책임이다.
        ManualTimeProvider time = new();
        using InMemorySessionStore store = new(
            new InMemorySessionStoreOptions { SweepInterval = TimeSpan.FromSeconds(1) }, time);

        for (int i = 0; i < 100; i++)
        {
            await store.TryWriteAsync(Id(i), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(100, store.Count);

        // 만료시키고 청소 주기를 지나게 한다 — 아무도 다시 조회하지 않는다.
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Without_sweeping_expired_entries_linger_in_memory()
    {
        // 위 테스트의 대조군 — 청소를 끄면 항목이 남는다(계약상 "없는 것" 이지만 메모리에는 있다).
        // 이 대비가 청소 타이머의 존재 이유를 증명한다.
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time, sweep: null);

        for (int i = 0; i < 100; i++)
        {
            await store.TryWriteAsync(Id(i), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(5));
        }

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(100, store.Count); // 메모리에는 남아 있다
        Assert.False((await store.TryReadAsync(Id(0), new ArrayBufferWriter<byte>())).Found); // 그러나 없는 것으로 보인다
    }

    // ── 조립·수명 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_sweep_interval_is_rejected_at_assembly(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new InMemorySessionStore(
                new InMemorySessionStoreOptions { SweepInterval = TimeSpan.FromSeconds(seconds) }));
    }

    [Fact]
    public void Negative_initial_capacity_is_rejected_at_assembly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new InMemorySessionStore(new InMemorySessionStoreOptions { InitialCapacity = -1 }));
    }

    [Fact]
    public async Task Use_after_dispose_throws()
    {
        InMemorySessionStore store = Create();
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        InMemorySessionStore store = Create();
        store.Dispose();
        store.Dispose(); // 두 번째 호출이 던지면 안 된다
    }

    // ── 할당 고정 (인메모리에서만 의미가 있는 지표다) ─────────────────────────

    [Fact]
    public async Task Conflicting_write_does_not_copy_the_state()
    {
        // ★ 벤치마크가 잡은 결함의 회귀 방어. 처음 구현은 버전 검사 **전에** state.ToArray()
        // 를 해서, 거부되는 호출도 상태 전체를 복사한 뒤 버렸다(1KB 상태에서 1,048 B).
        // 경합이 심할수록 충돌이 늘어나므로 **정확히 부하가 높을 때** GC 압력이 커진다.
        using InMemorySessionStore store = Create();
        byte[] large = new byte[4096];
        SessionWriteResult created = await store.TryWriteAsync(Id(1), large, SessionVersion.None);

        SessionVersion stale = new(ulong.MaxValue); // 절대 맞지 않는다

        // 워밍업 — 첫 호출의 JIT 할당을 측정에서 뺀다.
        await store.TryWriteAsync(Id(1), large, stale);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            await store.TryWriteAsync(Id(1), large, stale);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 100회 거부에 4KB 복사가 있었다면 400KB 다. 여유를 둬 8KB 로 잡는다.
        Assert.True(allocated < 8_192, $"거부 경로가 할당했다: {allocated} 바이트 / 100회");
        Assert.NotEqual(SessionVersion.None, created.Version);
    }

    [Fact]
    public async Task Renew_does_not_allocate()
    {
        // ★ 같은 부류. 만료를 미루자고 객체를 만드는 것은 이 메서드의 존재 이유
        // (상태를 다시 안 보내려고 만들었다)와 모순이다 — 하트비트는 잦은 경로다.
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        SessionWriteResult created = await store.TryWriteAsync(
            Id(1), Bytes(1, 2, 3), SessionVersion.None, TimeSpan.FromHours(1));

        await store.TryRenewAsync(Id(1), created.Version, TimeSpan.FromHours(1)); // 워밍업

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            await store.TryRenewAsync(Id(1), created.Version, TimeSpan.FromHours(1));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4_096, $"연장이 할당했다: {allocated} 바이트 / 1000회");
    }

    /// <summary>테스트가 시간을 직접 움직인다 — 만료를 <c>Task.Delay</c> 로 재면 느리고 플래키하다.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UnixEpoch.UtcTicks;
        private readonly System.Collections.Generic.List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state, dueTime, period, GetUtcNow());
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        /// <summary>시간을 진행시키고, 그 사이에 도달한 타이머를 실제로 발화시킨다.</summary>
        public void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref _utcTicks, delta.Ticks);
            DateTimeOffset now = GetUtcNow();

            ManualTimer[] snapshot;
            lock (_timers)
            {
                snapshot = [.. _timers];
            }

            foreach (ManualTimer timer in snapshot)
            {
                timer.FireIfDue(now);
            }
        }

        private sealed class ManualTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period, DateTimeOffset createdAt)
            : ITimer
        {
            private DateTimeOffset _nextFire = createdAt + dueTime;
            private bool _disposed;

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || period <= TimeSpan.Zero)
                {
                    return;
                }

                while (!_disposed && now >= _nextFire)
                {
                    callback(state);
                    _nextFire += period;
                }
            }

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod) => true;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
