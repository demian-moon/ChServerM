using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Persistence.InMemory.Tests;

/// <summary>
/// 세션 저장소 축의 참조 구현을 검증한다 — <b>값 의미</b>, <b>CAS</b>, <b>만료</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 테스트는 구현이 아니라 계약을 검증한다.</b> Redis 어댑터가 나오면 같은 단언이
/// 그대로 통과해야 하며, 그때 이 파일이 <b>축의 합격 기준</b> 역할을 한다 —
/// 두 구현이 다르게 동작하면 축 교체가 성립하지 않는다(ADR-0004).
/// </para>
/// <para>
/// 시간은 <see cref="ManualTimeProvider"/> 로 고정한다. 만료 테스트를 <c>Task.Delay</c> 로
/// 쓰면 느리고 플래키하다.
/// </para>
/// </remarks>
public sealed class InMemorySessionStoreTests
{
    private static SessionId Id(int seed) => new(new ObjectId(seed));

    private static byte[] Bytes(params byte[] value) => value;

    private static InMemorySessionStore Create(ManualTimeProvider? time = null, TimeSpan? sweep = null) =>
        new(new InMemorySessionStoreOptions { SweepInterval = sweep }, time ?? new ManualTimeProvider());

    // ── 기본 왕복 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_of_unknown_session_reports_not_found_and_leaves_destination_untouched()
    {
        // 계약: 찾지 못하면 대상을 건드리지 않는다 — 없는 세션 조회가 대상을 오염시키면 안 된다.
        using InMemorySessionStore store = Create();
        ArrayBufferWriter<byte> destination = new();
        destination.Write(Bytes(0xAA, 0xBB));

        SessionReadResult result = await store.TryReadAsync(Id(1), destination);

        Assert.False(result.Found);
        Assert.Equal(SessionVersion.None, result.Version);
        Assert.Equal(0, result.Length);
        Assert.Equal(2, destination.WrittenCount); // 미리 쓴 내용 그대로
    }

    [Fact]
    public async Task Write_then_read_round_trips_the_bytes()
    {
        using InMemorySessionStore store = Create();
        byte[] state = Bytes(1, 2, 3, 4);

        SessionWriteResult write = await store.TryWriteAsync(Id(1), state, SessionVersion.None);
        Assert.True(write.Succeeded);
        Assert.NotEqual(SessionVersion.None, write.Version);

        ArrayBufferWriter<byte> destination = new();
        SessionReadResult read = await store.TryReadAsync(Id(1), destination);

        Assert.True(read.Found);
        Assert.Equal(write.Version, read.Version);
        Assert.Equal(4, read.Length);
        Assert.Equal(state, destination.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task Stored_bytes_are_a_copy_so_caller_can_reuse_its_buffer()
    {
        // ★ 값 의미가 이 축의 핵심이다. 저장소가 호출자의 배열을 붙잡으면 인메모리와 Redis 의
        // 동작이 갈리고, 같은 핸들러 코드가 저장소마다 다르게 동작한다(ISessionStore 문서).
        using InMemorySessionStore store = Create();
        byte[] scratch = Bytes(1, 2, 3);

        await store.TryWriteAsync(Id(1), scratch, SessionVersion.None);

        // 호출자가 버퍼를 재사용한다 — 대여 버퍼를 즉시 반납하는 실제 사용 형태다.
        scratch[0] = 0xFF;
        scratch[1] = 0xFF;

        ArrayBufferWriter<byte> destination = new();
        await store.TryReadAsync(Id(1), destination);

        Assert.Equal(Bytes(1, 2, 3), destination.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task Read_does_not_expose_the_internal_array()
    {
        // 반대 방향의 같은 주장 — 읽은 쪽이 저장소 상태를 바꿀 수 없어야 한다.
        using InMemorySessionStore store = Create();
        await store.TryWriteAsync(Id(1), Bytes(1, 2, 3), SessionVersion.None);

        ArrayBufferWriter<byte> first = new();
        await store.TryReadAsync(Id(1), first);
        first.WrittenSpan.ToArray()[0] = 0xFF; // 사본을 고쳐도

        ArrayBufferWriter<byte> second = new();
        await store.TryReadAsync(Id(1), second);

        Assert.Equal(Bytes(1, 2, 3), second.WrittenSpan.ToArray()); // 저장소는 그대로
    }

    // ── 낙관적 동시성(CAS) ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_requires_none_as_expected_version()
    {
        // "아직 없을 때만 만들어라" — None 이 곧 생성의 조건부 표현이다.
        using InMemorySessionStore store = Create();

        SessionWriteResult wrongExpectation =
            await store.TryWriteAsync(Id(1), Bytes(1), new SessionVersion(42));

        Assert.False(wrongExpectation.Succeeded);
        Assert.Equal(SessionVersion.None, wrongExpectation.Version);
    }

    [Fact]
    public async Task Second_create_with_none_conflicts()
    {
        using InMemorySessionStore store = Create();
        await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None);

        SessionWriteResult second = await store.TryWriteAsync(Id(1), Bytes(2), SessionVersion.None);

        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Stale_version_loses_and_the_state_is_not_overwritten()
    {
        // ★ 이 계약이 없으면 재접속 경로에서 옛 커넥션의 마지막 쓰기가 복구 상태를 덮는다.
        using InMemorySessionStore store = Create();
        SessionWriteResult created = await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None);

        // 두 주체가 같은 버전을 읽었다.
        SessionVersion seenByA = created.Version;
        SessionVersion seenByB = created.Version;

        SessionWriteResult a = await store.TryWriteAsync(Id(1), Bytes(0xAA), seenByA);
        SessionWriteResult b = await store.TryWriteAsync(Id(1), Bytes(0xBB), seenByB);

        Assert.True(a.Succeeded);
        Assert.False(b.Succeeded); // 늦게 온 쪽이 진다

        ArrayBufferWriter<byte> destination = new();
        await store.TryReadAsync(Id(1), destination);
        Assert.Equal(Bytes(0xAA), destination.WrittenSpan.ToArray()); // B 가 덮지 못했다
    }

    [Fact]
    public async Task Version_changes_on_every_successful_write()
    {
        using InMemorySessionStore store = Create();
        SessionWriteResult v1 = await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None);
        SessionWriteResult v2 = await store.TryWriteAsync(Id(1), Bytes(2), v1.Version);
        SessionWriteResult v3 = await store.TryWriteAsync(Id(1), Bytes(3), v2.Version);

        Assert.NotEqual(v1.Version, v2.Version);
        Assert.NotEqual(v2.Version, v3.Version);
        Assert.NotEqual(v1.Version, v3.Version);
    }

    [Fact]
    public async Task Version_is_not_reused_after_removal_so_stale_writers_cannot_aba()
    {
        // ★ ABA 방지 — 삭제 후 재생성된 항목에 옛 버전이 통하면 남의 상태를 덮는다.
        using InMemorySessionStore store = Create();
        SessionWriteResult first = await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None);
        Assert.True(await store.TryRemoveAsync(Id(1), first.Version));

        SessionWriteResult recreated = await store.TryWriteAsync(Id(1), Bytes(2), SessionVersion.None);

        Assert.NotEqual(first.Version, recreated.Version);

        // 옛 버전을 든 쓰기는 실패해야 한다.
        SessionWriteResult stale = await store.TryWriteAsync(Id(1), Bytes(9), first.Version);
        Assert.False(stale.Succeeded);
    }

    [Fact]
    public async Task Remove_requires_the_matching_version()
    {
        using InMemorySessionStore store = Create();
        SessionWriteResult created = await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None);

        Assert.False(await store.TryRemoveAsync(Id(1), new SessionVersion(999)));
        Assert.True(await store.TryRemoveAsync(Id(1), created.Version));
        Assert.False(await store.TryRemoveAsync(Id(1), created.Version)); // 이미 없다
    }

    [Fact]
    public async Task Sessions_are_independent()
    {
        using InMemorySessionStore store = Create();
        await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None);
        await store.TryWriteAsync(Id(2), Bytes(2), SessionVersion.None);

        ArrayBufferWriter<byte> destination = new();
        await store.TryReadAsync(Id(2), destination);

        Assert.Equal(Bytes(2), destination.WrittenSpan.ToArray());
    }

    // ── 만료 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_session_reads_as_missing()
    {
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.True((await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>())).Found);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>())).Found);
    }

    [Fact]
    public async Task Expired_key_can_be_created_again_with_none()
    {
        // 계약: 만료된 항목은 없는 것과 같다 — 그 키의 첫 쓰기는 None 을 기대 버전으로 받는다.
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(11));

        SessionWriteResult recreated = await store.TryWriteAsync(Id(1), Bytes(2), SessionVersion.None);

        Assert.True(recreated.Succeeded);
    }

    [Fact]
    public async Task Write_resets_the_expiry()
    {
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        SessionWriteResult v1 = await store.TryWriteAsync(
            Id(1), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(8));
        SessionWriteResult v2 = await store.TryWriteAsync(
            Id(1), Bytes(2), v1.Version, TimeSpan.FromSeconds(10));
        Assert.True(v2.Succeeded);

        // 첫 만료 시각(10s)을 지났지만 두 번째 쓰기가 만료를 다시 설정했다.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>())).Found);
    }

    [Fact]
    public async Task Renew_extends_expiry_without_changing_the_version()
    {
        // ★ 버전을 올리지 않는 것이 계약이다 — 하트비트가 남의 CAS 를 깨면 안 된다.
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        SessionWriteResult created = await store.TryWriteAsync(
            Id(1), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(8));
        Assert.True(await store.TryRenewAsync(Id(1), created.Version, TimeSpan.FromSeconds(10)));

        time.Advance(TimeSpan.FromSeconds(5));
        ArrayBufferWriter<byte> destination = new();
        SessionReadResult read = await store.TryReadAsync(Id(1), destination);

        Assert.True(read.Found);
        Assert.Equal(created.Version, read.Version); // 버전 불변
        Assert.Equal(Bytes(1), destination.WrittenSpan.ToArray()); // 상태 불변

        // 그러므로 원래 버전을 든 쓰기가 여전히 성공한다.
        Assert.True((await store.TryWriteAsync(Id(1), Bytes(2), created.Version)).Succeeded);
    }

    [Fact]
    public async Task Renew_requires_the_matching_version_and_a_live_entry()
    {
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        SessionWriteResult created = await store.TryWriteAsync(
            Id(1), Bytes(1), SessionVersion.None, TimeSpan.FromSeconds(10));

        Assert.False(await store.TryRenewAsync(Id(1), new SessionVersion(999), TimeSpan.FromSeconds(5)));
        Assert.False(await store.TryRenewAsync(Id(2), created.Version, TimeSpan.FromSeconds(5)));

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(await store.TryRenewAsync(Id(1), created.Version, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Null_ttl_means_no_expiry()
    {
        ManualTimeProvider time = new();
        using InMemorySessionStore store = Create(time);
        await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None, timeToLive: null);

        time.Advance(TimeSpan.FromDays(365));

        Assert.True((await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>())).Found);
    }

    [Fact]
    public async Task Sweep_actually_reclaims_abandoned_sessions()
    {
        // ★★ 지연 만료만으로는 새는 것을 막지 못한다 — 다시 조회되지 않는 세션이 정확히
        // 끊긴 클라이언트의 상태이고, 그것이 쌓이는 것이 OOM 이다.
        ManualTimeProvider time = new(); // 타이머도 이 시간 원천을 따른다
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

    // ── 조립 검증 ───────────────────────────────────────────────────────────

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
    public async Task Non_positive_ttl_is_rejected()
    {
        using InMemorySessionStore store = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.TryWriteAsync(Id(1), Bytes(1), SessionVersion.None, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.TryRenewAsync(Id(1), SessionVersion.None, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task Null_destination_is_rejected()
    {
        using InMemorySessionStore store = Create();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.TryReadAsync(Id(1), null!));
    }

    [Fact]
    public async Task Use_after_dispose_throws()
    {
        InMemorySessionStore store = Create();
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));
    }

    // ── 동시성 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_writers_produce_exactly_one_winner_per_generation()
    {
        // 동시성 버그는 반복 실행으로 잡는다(9.9). 같은 버전을 든 N 명 중 정확히 하나만 이겨야 한다.
        using InMemorySessionStore store = new(
            new InMemorySessionStoreOptions { SweepInterval = null }, TimeProvider.System);

        for (int round = 0; round < 200; round++)
        {
            SessionId id = Id(round);
            SessionWriteResult created = await store.TryWriteAsync(id, Bytes(0), SessionVersion.None);

            const int Writers = 8;
            Task<SessionWriteResult>[] attempts = new Task<SessionWriteResult>[Writers];
            using Barrier barrier = new(Writers);

            for (int i = 0; i < Writers; i++)
            {
                byte value = (byte)i;
                attempts[i] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    return await store.TryWriteAsync(id, Bytes(value), created.Version);
                });
            }

            SessionWriteResult[] results = await Task.WhenAll(attempts);

            int winners = 0;
            foreach (SessionWriteResult result in results)
            {
                if (result.Succeeded)
                {
                    winners++;
                }
            }

            Assert.Equal(1, winners);
        }
    }

    // ── 할당 고정 ───────────────────────────────────────────────────────────

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
