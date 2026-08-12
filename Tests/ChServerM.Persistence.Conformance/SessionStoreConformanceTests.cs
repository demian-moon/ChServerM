using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Persistence.Conformance;

/// <summary>
/// <b>세션 저장소 축의 적합성 테스트</b> — 모든 <see cref="ISessionStore"/> 구현이 통과해야 한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — "두 구현이 같은 테스트를 통과해야 축이 성립한다"(ADR-0004)를 코드로 만든 것.</b>
/// 어댑터마다 자기 테스트를 따로 쓰면 각자 자기 구현이 하는 대로 단언하게 되고, 그러면 축을
/// 교체했을 때 동작이 갈리는 것을 아무도 못 잡는다. <b>이 파일이 계약의 실행 가능한 정의다.</b>
/// </para>
/// <para>
/// <b>새 어댑터(Garnet·Tsavorite 등)를 만들 때는 이 클래스를 상속하는 것부터 한다.</b>
/// 여기서 실패하는 항목이 있으면 그것은 테스트가 아니라 어댑터가 계약을 안 지킨 것이다.
/// </para>
/// <para>
/// <b>⚠ 시간을 어떻게 다루는가.</b> 인메모리는 가짜 시계를 넣을 수 있지만 Redis 는 서버가
/// 만료를 판정하므로 <b>실제로 기다려야</b> 한다. 그래서 시간 진행을
/// <see cref="AdvanceAsync"/> 로 추상화하고, 만료 대기 길이도
/// <see cref="ShortTimeToLive"/> 로 열어 뒀다 — 어댑터가 자기 사정에 맞게 고른다.
/// <b>단언 자체는 두 구현이 완전히 동일하다.</b>
/// </para>
/// <para>
/// <b>구현 고유 사항은 여기 넣지 않는다.</b> 인메모리의 청소 타이머, Redis 의 키 접두사처럼
/// 한쪽에만 있는 것은 각 어댑터의 테스트 프로젝트가 검증한다.
/// </para>
/// </remarks>
public abstract class SessionStoreConformanceTests
{
    /// <summary>검증 대상 저장소. 테스트마다 새 인스턴스여도 되고 공유해도 된다(키가 겹치지 않으면).</summary>
    /// <remarks>
    /// 백킹 저장소를 쓸 수 없으면(예: Docker 미실행) <b>여기서 건너뛴다</b>.
    /// 모든 테스트가 이 속성을 지나므로 관문이 하나뿐이고, <b>조용히 통과하는 일이 없다</b> —
    /// 건너뜀은 러너 출력에 사유와 함께 남는다.
    /// </remarks>
    protected abstract ISessionStore Store { get; }

    /// <summary>
    /// 만료 검증에 쓰는 TTL. 인메모리는 가짜 시계라 길게 잡아도 공짜지만,
    /// Redis 는 실제로 기다리므로 짧게 잡는다.
    /// </summary>
    protected virtual TimeSpan ShortTimeToLive => TimeSpan.FromSeconds(10);

    /// <summary>시간을 진행시킨다. 가짜 시계면 즉시, 실제 서버면 그만큼 기다린다.</summary>
    /// <param name="delta">진행시킬 시간.</param>
    protected abstract Task AdvanceAsync(TimeSpan delta);

    /// <summary>
    /// 테스트마다 겹치지 않는 세션 식별자를 만든다.
    /// </summary>
    /// <remarks>
    /// 저장소를 공유하는 어댑터(컨테이너 하나를 재사용하는 Redis)에서 테스트가 서로를
    /// 오염시키지 않게 한다. 구현은 프로세스 안에서 유일한 값을 준다.
    /// </remarks>
    protected static SessionId NewId() => new(new ObjectId(Interlocked.Increment(ref _idSeed)));

    private static long _idSeed = DateTime.UtcNow.Ticks % 1_000_000_000;

    private static byte[] Bytes(params byte[] value) => value;

    // ── 값 의미 ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Read_of_unknown_session_reports_not_found_and_leaves_destination_untouched()
    {
        // 계약: 찾지 못하면 대상을 건드리지 않는다.
        ArrayBufferWriter<byte> destination = new();
        destination.Write(Bytes(0xAA, 0xBB));

        SessionReadResult result = await Store.TryReadAsync(NewId(), destination);

        Assert.False(result.Found);
        Assert.Equal(SessionVersion.None, result.Version);
        Assert.Equal(0, result.Length);
        Assert.Equal(2, destination.WrittenCount);
    }

    [SkippableFact]
    public async Task Write_then_read_round_trips_the_bytes()
    {
        SessionId id = NewId();
        byte[] state = Bytes(1, 2, 3, 4);

        SessionWriteResult write = await Store.TryWriteAsync(id, state, SessionVersion.None);
        Assert.True(write.Succeeded);
        Assert.NotEqual(SessionVersion.None, write.Version);

        ArrayBufferWriter<byte> destination = new();
        SessionReadResult read = await Store.TryReadAsync(id, destination);

        Assert.True(read.Found);
        Assert.Equal(write.Version, read.Version);
        Assert.Equal(4, read.Length);
        Assert.Equal(state, destination.WrittenSpan.ToArray());
    }

    [SkippableFact]
    public async Task Empty_state_round_trips()
    {
        // 빈 상태도 "있음" 이다 — 없는 것과 구분되어야 한다.
        SessionId id = NewId();
        SessionWriteResult write = await Store.TryWriteAsync(id, ReadOnlyMemory<byte>.Empty, SessionVersion.None);
        Assert.True(write.Succeeded);

        ArrayBufferWriter<byte> destination = new();
        SessionReadResult read = await Store.TryReadAsync(id, destination);

        Assert.True(read.Found);
        Assert.Equal(0, read.Length);
    }

    [SkippableFact]
    public async Task Stored_bytes_are_a_copy_so_caller_can_reuse_its_buffer()
    {
        // ★ 값 의미가 이 축의 핵심이다. 저장소가 호출자의 배열을 붙잡으면 인메모리와 원격의
        // 동작이 갈리고, 같은 핸들러 코드가 저장소마다 다르게 동작한다(ADR-0033).
        SessionId id = NewId();
        byte[] scratch = Bytes(1, 2, 3);

        await Store.TryWriteAsync(id, scratch, SessionVersion.None);

        scratch[0] = 0xFF;
        scratch[1] = 0xFF;

        ArrayBufferWriter<byte> destination = new();
        await Store.TryReadAsync(id, destination);

        Assert.Equal(Bytes(1, 2, 3), destination.WrittenSpan.ToArray());
    }

    [SkippableFact]
    public async Task Read_does_not_expose_internal_state()
    {
        SessionId id = NewId();
        await Store.TryWriteAsync(id, Bytes(1, 2, 3), SessionVersion.None);

        ArrayBufferWriter<byte> first = new();
        await Store.TryReadAsync(id, first);
        first.WrittenSpan.ToArray()[0] = 0xFF;

        ArrayBufferWriter<byte> second = new();
        await Store.TryReadAsync(id, second);

        Assert.Equal(Bytes(1, 2, 3), second.WrittenSpan.ToArray());
    }

    [SkippableFact]
    public async Task Sessions_are_independent()
    {
        SessionId a = NewId();
        SessionId b = NewId();
        await Store.TryWriteAsync(a, Bytes(1), SessionVersion.None);
        await Store.TryWriteAsync(b, Bytes(2), SessionVersion.None);

        ArrayBufferWriter<byte> destination = new();
        await Store.TryReadAsync(b, destination);

        Assert.Equal(Bytes(2), destination.WrittenSpan.ToArray());
    }

    // ── 낙관적 동시성(CAS) ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task Create_requires_none_as_expected_version()
    {
        // "아직 없을 때만 만들어라" — None 이 곧 생성의 조건부 표현이다.
        SessionWriteResult wrong = await Store.TryWriteAsync(NewId(), Bytes(1), new SessionVersion(42));

        Assert.False(wrong.Succeeded);
        Assert.Equal(SessionVersion.None, wrong.Version);
    }

    [SkippableFact]
    public async Task Second_create_with_none_conflicts()
    {
        SessionId id = NewId();
        await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None);

        Assert.False((await Store.TryWriteAsync(id, Bytes(2), SessionVersion.None)).Succeeded);
    }

    [SkippableFact]
    public async Task Stale_version_loses_and_the_state_is_not_overwritten()
    {
        // ★ 이 계약이 없으면 재접속 경로에서 옛 커넥션의 마지막 쓰기가 복구 상태를 덮는다.
        SessionId id = NewId();
        SessionWriteResult created = await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None);

        SessionWriteResult a = await Store.TryWriteAsync(id, Bytes(0xAA), created.Version);
        SessionWriteResult b = await Store.TryWriteAsync(id, Bytes(0xBB), created.Version);

        Assert.True(a.Succeeded);
        Assert.False(b.Succeeded);

        ArrayBufferWriter<byte> destination = new();
        await Store.TryReadAsync(id, destination);
        Assert.Equal(Bytes(0xAA), destination.WrittenSpan.ToArray());
    }

    [SkippableFact]
    public async Task Version_changes_on_every_successful_write()
    {
        SessionId id = NewId();
        SessionWriteResult v1 = await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None);
        SessionWriteResult v2 = await Store.TryWriteAsync(id, Bytes(2), v1.Version);
        SessionWriteResult v3 = await Store.TryWriteAsync(id, Bytes(3), v2.Version);

        Assert.NotEqual(v1.Version, v2.Version);
        Assert.NotEqual(v2.Version, v3.Version);
        Assert.NotEqual(v1.Version, v3.Version);
    }

    [SkippableFact]
    public async Task Version_is_not_reused_after_removal_so_stale_writers_cannot_aba()
    {
        // ★ ABA 방지 — 삭제 후 재생성된 항목에 옛 버전이 통하면 남의 상태를 덮는다.
        SessionId id = NewId();
        SessionWriteResult first = await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None);
        Assert.True(await Store.TryRemoveAsync(id, first.Version));

        SessionWriteResult recreated = await Store.TryWriteAsync(id, Bytes(2), SessionVersion.None);
        Assert.NotEqual(first.Version, recreated.Version);

        Assert.False((await Store.TryWriteAsync(id, Bytes(9), first.Version)).Succeeded);
    }

    [SkippableFact]
    public async Task Remove_requires_the_matching_version()
    {
        SessionId id = NewId();
        SessionWriteResult created = await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None);

        Assert.False(await Store.TryRemoveAsync(id, new SessionVersion(ulong.MaxValue)));
        Assert.True(await Store.TryRemoveAsync(id, created.Version));
        Assert.False(await Store.TryRemoveAsync(id, created.Version));
    }

    // ── 만료 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Expired_session_reads_as_missing()
    {
        SessionId id = NewId();
        await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None, ShortTimeToLive);

        Assert.True((await Store.TryReadAsync(id, new ArrayBufferWriter<byte>())).Found);

        await AdvanceAsync(ShortTimeToLive + ShortTimeToLive);

        Assert.False((await Store.TryReadAsync(id, new ArrayBufferWriter<byte>())).Found);
    }

    [SkippableFact]
    public async Task Expired_key_can_be_created_again_with_none()
    {
        // 계약: 만료된 항목은 없는 것과 같다.
        SessionId id = NewId();
        await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None, ShortTimeToLive);
        await AdvanceAsync(ShortTimeToLive + ShortTimeToLive);

        Assert.True((await Store.TryWriteAsync(id, Bytes(2), SessionVersion.None)).Succeeded);
    }

    [SkippableFact]
    public async Task Null_ttl_means_no_expiry()
    {
        SessionId id = NewId();
        await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None, timeToLive: null);

        await AdvanceAsync(ShortTimeToLive + ShortTimeToLive);

        Assert.True((await Store.TryReadAsync(id, new ArrayBufferWriter<byte>())).Found);
    }

    [SkippableFact]
    public async Task Write_resets_the_expiry()
    {
        // 계약: 쓰기가 성공하면 만료 시각이 다시 설정된다.
        //
        // ⚠ 이 테스트(와 아래 Renew 판)는 "만료 전 여유 폭" 안에서 다음 호출이 도달해야
        // 성립한다. 실서버 스토어의 1초 TTL 에서는 여유가 0.4초뿐이라 느린 CI 러너의
        // 멈칫에 잡아먹혔다(2026-08-11, Postgres·Garnet 동시 실패). 이 테스트에서만
        // TTL 을 4배로 키워 여유를 1.6×ShortTimeToLive 로 늘린다 — 만료를 기다리는
        // 다른 테스트들의 대기 시간은 건드리지 않는다.
        //
        // ⚠⚠ 스케일만으로는 이 부류가 닫히지 않는다(2026-08-12, Redis 재발 — 릴리스
        // 리허설 실행 31565501210). 같은 러너에서 단일 스토어 명령이 5초를 넘긴 기록이
        // 있으므로(f3fd254) 어떤 고정 여유든 최악의 멈칫보다 작을 수 있다. 그래서
        // 판정이 깨졌을 때 실측 경과가 여유를 넘겼으면 **판정 불능으로 건너뛴다** —
        // 타이밍이 지켜진 실행의 단언은 그대로다(msquic 미지원과 같은 환경 판정).
        TimeSpan ttl = ShortTimeToLive * 4;

        SessionId id = NewId();
        long firstWrite = System.Diagnostics.Stopwatch.GetTimestamp();
        SessionWriteResult v1 = await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None, ttl);

        await AdvanceAsync(TimeSpan.FromMilliseconds(ttl.TotalMilliseconds * 0.6));

        long secondWrite = System.Diagnostics.Stopwatch.GetTimestamp();
        SessionWriteResult v2 = await Store.TryWriteAsync(id, Bytes(2), v1.Version, ttl);
        SkipIfStallConsumedTheMargin(v2.Succeeded, firstWrite, ttl);
        Assert.True(v2.Succeeded);

        // 첫 만료 시각을 지났지만 두 번째 쓰기가 만료를 다시 설정했다.
        await AdvanceAsync(TimeSpan.FromMilliseconds(ttl.TotalMilliseconds * 0.6));
        SessionReadResult read = await Store.TryReadAsync(id, new ArrayBufferWriter<byte>());
        SkipIfStallConsumedTheMargin(read.Found, secondWrite, ttl);
        Assert.True(read.Found);
    }

    /// <summary>
    /// 만료 판정 테스트의 거짓 빨강 방어 — 판정이 깨졌고 실측 경과가 TTL 의 90% 를
    /// 넘겼으면 "아직 살아 있어야 한다"는 단언이 성립 불능이므로 건너뛴다.
    /// </summary>
    /// <remarks>
    /// 판정이 성공했으면 아무것도 하지 않는다 — 타이밍이 지켜진 실행의 엄격성은 그대로다.
    /// 90% 인 이유: 만료 기준 시각은 서버가 호출을 처리한 순간(호출 시작~반환 사이)이라
    /// 클라이언트 측 계측과 정확히 겹치지 않는다 — 경계에서의 오판을 피할 여유를 남긴다.
    /// </remarks>
    /// <param name="judged">단언하려는 판정 결과. 참이면 검사하지 않는다.</param>
    /// <param name="anchorTimestamp">만료 기준이 잡힌 호출 직전의 <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</param>
    /// <param name="timeToLive">그 호출이 설정한 TTL.</param>
    private static void SkipIfStallConsumedTheMargin(bool judged, long anchorTimestamp, TimeSpan timeToLive)
    {
        if (judged)
        {
            return;
        }

        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(anchorTimestamp);
        Skip.If(
            elapsed >= timeToLive * 0.9,
            $"러너 멈칫이 만료 여유를 소진했다 — 경과 {elapsed.TotalSeconds:F1}s ≥ TTL {timeToLive.TotalSeconds:F1}s 의 90%. 판정 불능.");
    }

    [SkippableFact]
    public async Task Renew_extends_expiry_without_changing_the_version()
    {
        // ★ 버전을 올리지 않는 것이 계약이다 — 하트비트가 남의 CAS 를 깨면 안 된다.
        // TTL 4배 스케일 + 멈칫 시 판정 불능 건너뜀의 이유는 Write_resets_the_expiry 주석 참조.
        TimeSpan ttl = ShortTimeToLive * 4;

        SessionId id = NewId();
        long createdAt = System.Diagnostics.Stopwatch.GetTimestamp();
        SessionWriteResult created = await Store.TryWriteAsync(
            id, Bytes(1), SessionVersion.None, ttl);

        // 만료 전에 연장한다.
        await AdvanceAsync(TimeSpan.FromMilliseconds(ttl.TotalMilliseconds * 0.6));
        long renewedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        bool renewed = await Store.TryRenewAsync(id, created.Version, ttl);
        SkipIfStallConsumedTheMargin(renewed, createdAt, ttl);
        Assert.True(renewed);

        // 원래 만료 시각을 지나도 살아 있어야 한다.
        await AdvanceAsync(TimeSpan.FromMilliseconds(ttl.TotalMilliseconds * 0.6));

        ArrayBufferWriter<byte> destination = new();
        SessionReadResult read = await Store.TryReadAsync(id, destination);

        SkipIfStallConsumedTheMargin(read.Found, renewedAt, ttl);
        Assert.True(read.Found);
        Assert.Equal(created.Version, read.Version);
        Assert.Equal(Bytes(1), destination.WrittenSpan.ToArray());

        // 버전이 그대로이므로 원래 버전을 든 쓰기가 여전히 성공한다.
        Assert.True((await Store.TryWriteAsync(id, Bytes(2), created.Version)).Succeeded);
    }

    [SkippableFact]
    public async Task Renew_requires_the_matching_version_and_a_live_entry()
    {
        SessionId id = NewId();
        SessionWriteResult created = await Store.TryWriteAsync(
            id, Bytes(1), SessionVersion.None, ShortTimeToLive);

        Assert.False(await Store.TryRenewAsync(id, new SessionVersion(ulong.MaxValue), ShortTimeToLive));
        Assert.False(await Store.TryRenewAsync(NewId(), created.Version, ShortTimeToLive));

        await AdvanceAsync(ShortTimeToLive + ShortTimeToLive);
        Assert.False(await Store.TryRenewAsync(id, created.Version, ShortTimeToLive));
    }

    // ── 인자 검증 ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Non_positive_ttl_is_rejected()
    {
        SessionId id = NewId();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Store.TryWriteAsync(id, Bytes(1), SessionVersion.None, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Store.TryRenewAsync(id, SessionVersion.None, TimeSpan.FromSeconds(-1)));
    }

    [SkippableFact]
    public async Task Null_destination_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Store.TryReadAsync(NewId(), null!));
    }

    // ── 동시성 ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Concurrent_writers_produce_exactly_one_winner()
    {
        // 같은 버전을 든 N 명 중 정확히 하나만 이겨야 한다. 이것이 CAS 의 정의다.
        SessionId id = NewId();
        SessionWriteResult created = await Store.TryWriteAsync(id, Bytes(0), SessionVersion.None);

        const int Writers = 8;
        Task<SessionWriteResult>[] attempts = new Task<SessionWriteResult>[Writers];
        using Barrier barrier = new(Writers);

        for (int i = 0; i < Writers; i++)
        {
            byte value = (byte)i;
            attempts[i] = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await Store.TryWriteAsync(id, Bytes(value), created.Version);
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
