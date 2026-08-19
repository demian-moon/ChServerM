using System;
using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Hosting.Sessions;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 세션 복구·재접속을 검증한다 — <b>끊긴 클라이언트가 상태를 잃지 않고 돌아오는 경로</b>.
/// </summary>
/// <remarks>
/// <para>
/// 보안 단언이 이 파일의 절반이다: 토큰이 저장소에 평문으로 남지 않는가, 회전이 실제로
/// 옛 토큰을 무효화하는가, 실패 사유가 새지 않는가, 그리고 <b>좀비 커넥션이 밀려나는가</b>.
/// </para>
/// </remarks>
public sealed class SessionResumeTests
{
    private static SessionId Id(int seed) => new(new ObjectId(seed));

    private static byte[] Bytes(params byte[] value) => value;

    private static (SessionResumeService Service, InMemorySessionStore Store) Create()
    {
        InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        return (new SessionResumeService(store), store);
    }

    // ── 기본 경로 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_then_resume_restores_the_state()
    {
        // 이 기능의 존재 이유 — 끊겼다 돌아와도 상태가 그대로다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        SessionBinding? created = await service.TryCreateAsync(Id(1), Bytes(1, 2, 3, 4));
        Assert.NotNull(created);

        ArrayBufferWriter<byte> recovered = new();
        SessionResumeResult resume = await service.TryResumeAsync(Id(1), created.Value.ResumeToken, recovered);

        Assert.True(resume.Succeeded);
        Assert.Equal(4, resume.StateLength);
        Assert.Equal(Bytes(1, 2, 3, 4), recovered.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task Create_fails_when_the_session_already_exists()
    {
        // 남의 세션을 덮어쓰지 않는다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        Assert.NotNull(await service.TryCreateAsync(Id(1), Bytes(1)));
        Assert.Null(await service.TryCreateAsync(Id(1), Bytes(2)));
    }

    [Fact]
    public async Task State_round_trips_without_the_envelope_leaking_to_the_app()
    {
        // 앱은 자기 상태만 본다 — 봉투는 서비스가 붙이고 뗀다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        SessionBinding created = (await service.TryCreateAsync(Id(1), Bytes(9, 8, 7)))!.Value;

        ArrayBufferWriter<byte> read = new();
        SessionReadResult result = await service.TryReadStateAsync(Id(1), read);

        Assert.True(result.Found);
        Assert.Equal(3, result.Length);
        Assert.Equal(Bytes(9, 8, 7), read.WrittenSpan.ToArray());
        Assert.Equal(created.Version, result.Version);
    }

    [Fact]
    public async Task Write_state_preserves_the_resume_token()
    {
        // 상태를 갱신해도 재접속 능력이 사라지면 안 된다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        SessionBinding created = (await service.TryCreateAsync(Id(1), Bytes(1)))!.Value;

        SessionWriteResult write = await service.TryWriteStateAsync(Id(1), Bytes(2, 2), created.Version);
        Assert.True(write.Succeeded);

        ArrayBufferWriter<byte> recovered = new();
        SessionResumeResult resume = await service.TryResumeAsync(Id(1), created.ResumeToken, recovered);

        Assert.True(resume.Succeeded);
        Assert.Equal(Bytes(2, 2), recovered.WrittenSpan.ToArray());
    }

    // ── 보안 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Token_is_never_stored_in_the_clear()
    {
        // ★ 저장소가 유출돼도 그 값으로 재접속할 수 없어야 한다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        SessionBinding created = (await service.TryCreateAsync(Id(1), Bytes(1, 2, 3)))!.Value;

        ArrayBufferWriter<byte> stored = new();
        await store.TryReadAsync(Id(1), stored);

        Span<byte> tokenBytes = stackalloc byte[SessionResumeToken.Length];
        created.ResumeToken.CopyTo(tokenBytes);

        Assert.False(
            stored.WrittenSpan.IndexOf(tokenBytes) >= 0,
            "재개 토큰 원본이 저장소에 그대로 남아 있다 — 저장소 유출이 곧 세션 탈취가 된다.");
    }

    [Fact]
    public async Task Resume_rotates_the_token_so_the_old_one_is_single_use()
    {
        // ★ 탈취된 토큰이 1회용이 되는 근거.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        SessionBinding created = (await service.TryCreateAsync(Id(1), Bytes(1)))!.Value;

        SessionResumeResult first = await service.TryResumeAsync(
            Id(1), created.ResumeToken, new ArrayBufferWriter<byte>());
        Assert.True(first.Succeeded);
        Assert.NotEqual(created.ResumeToken, first.RotatedToken);

        // 옛 토큰은 즉시 무효다.
        SessionResumeResult replay = await service.TryResumeAsync(
            Id(1), created.ResumeToken, new ArrayBufferWriter<byte>());
        Assert.False(replay.Succeeded);

        // 새 토큰은 동작한다.
        Assert.True((await service.TryResumeAsync(
            Id(1), first.RotatedToken, new ArrayBufferWriter<byte>())).Succeeded);
    }

    [Fact]
    public async Task Wrong_token_and_missing_session_are_indistinguishable()
    {
        // ★ 사유를 구분해 주면 공격자가 실재하는 SessionId 를 열거할 수 있다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        await service.TryCreateAsync(Id(1), Bytes(1));

        SessionResumeToken bogus = SessionResumeToken.Create();

        SessionResumeResult wrongToken = await service.TryResumeAsync(
            Id(1), bogus, new ArrayBufferWriter<byte>());
        SessionResumeResult missingSession = await service.TryResumeAsync(
            Id(999), bogus, new ArrayBufferWriter<byte>());

        Assert.Equal(wrongToken, missingSession); // 결과가 완전히 동일하다
        Assert.False(wrongToken.Succeeded);
    }

    [Fact]
    public async Task Failed_resume_does_not_touch_the_destination()
    {
        // 실패했는데 대상에 무언가 쓰이면 호출자가 그것을 상태로 오인한다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        await service.TryCreateAsync(Id(1), Bytes(1, 2, 3));

        ArrayBufferWriter<byte> destination = new();
        SessionResumeResult result = await service.TryResumeAsync(
            Id(1), SessionResumeToken.Create(), destination);

        Assert.False(result.Succeeded);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public void Token_does_not_leak_through_ToString()
    {
        SessionResumeToken token = SessionResumeToken.Create();
        Span<byte> bytes = stackalloc byte[SessionResumeToken.Length];
        token.CopyTo(bytes);

        string text = token.ToString();

        Assert.DoesNotContain(Convert.ToHexString(bytes), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tokens_are_unique()
    {
        SessionResumeToken a = SessionResumeToken.Create();
        SessionResumeToken b = SessionResumeToken.Create();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Malformed_token_bytes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SessionResumeToken.FromBytes(new byte[31]));
        Assert.Throws<ArgumentException>(() => SessionResumeToken.FromBytes(new byte[33]));
    }

    [Fact]
    public void GetHashCode_does_not_expose_leading_token_bytes()
    {
        // ★ 이전 구현은 앞 4바이트를 리틀엔디언 int 로 그대로 반환했다 — 해시 코드가
        //   보이는 자리에 노출되면 비밀 원문 4바이트가 그대로 샌다(감사 2026-08-18 H-14).
        //   전체를 섞은 비가역 해시는 원문을 복원할 수 없어야 한다.
        byte[] raw = new byte[SessionResumeToken.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i + 1);
        }

        SessionResumeToken token = SessionResumeToken.FromBytes(raw);
        int leading = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw);

        Assert.NotEqual(leading, token.GetHashCode());
    }

    [Fact]
    public void Equal_tokens_have_equal_hash_codes()
    {
        // GetHashCode 계약 — Equals 가 참이면 해시도 같아야 사전 버킷이 성립한다.
        byte[] raw = new byte[SessionResumeToken.Length];
        Random.Shared.NextBytes(raw);

        SessionResumeToken a = SessionResumeToken.FromBytes(raw);
        SessionResumeToken b = SessionResumeToken.FromBytes(raw);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ── ★ 좀비 커넥션 차단 (CAS 에 얹은 결과) ────────────────────────────────

    [Fact]
    public async Task Resume_fences_out_the_old_connection()
    {
        // ★★ 이 시나리오가 재접속 설계의 핵심이다.
        //
        // 옛 커넥션이 아직 살아 있는데 새 커넥션이 재개했다. 옛 커넥션이 상태를 쓰면
        // **밀려났음을 알아야** 한다 — 모르면 두 커넥션이 같은 세션을 번갈아 덮어쓴다.
        //
        // 별도의 generation 카운터를 두지 않았다: 재개가 토큰 회전이라는 쓰기를 유발하므로
        // 버전이 올라가고, 옛 커넥션의 버전은 자동으로 무효가 된다(ADR-0033 의 CAS).
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        SessionBinding old = (await service.TryCreateAsync(Id(1), Bytes(1)))!.Value;

        // 새 커넥션이 재개한다.
        SessionResumeResult resumed = await service.TryResumeAsync(
            Id(1), old.ResumeToken, new ArrayBufferWriter<byte>());
        Assert.True(resumed.Succeeded);

        // 옛 커넥션이 자기 버전으로 쓰기를 시도한다 → 밀려났다.
        SessionWriteResult zombieWrite = await service.TryWriteStateAsync(Id(1), Bytes(0xDE), old.Version);
        Assert.False(zombieWrite.Succeeded);

        // 새 커넥션은 정상 동작한다.
        Assert.True((await service.TryWriteStateAsync(Id(1), Bytes(0xAD), resumed.Version)).Succeeded);

        ArrayBufferWriter<byte> final = new();
        await service.TryReadStateAsync(Id(1), final);
        Assert.Equal(Bytes(0xAD), final.WrittenSpan.ToArray()); // 좀비가 덮지 못했다
    }

    [Fact]
    public async Task Concurrent_resume_attempts_produce_one_winner()
    {
        // 진짜 주인과 탈취자가 같은 토큰으로 동시에 재개하면 하나만 이긴다 —
        // 진 쪽은 자기가 늦었음을 알게 되고, 그것이 탈취 탐지의 근거다.
        (SessionResumeService service, InMemorySessionStore store) = Create();
        using InMemorySessionStore _ = store;

        for (int round = 0; round < 50; round++)
        {
            SessionId id = Id(1000 + round);
            SessionBinding created = (await service.TryCreateAsync(id, Bytes(1)))!.Value;

            Task<SessionResumeResult> a = Task.Run(async () =>
                await service.TryResumeAsync(id, created.ResumeToken, new ArrayBufferWriter<byte>()));
            Task<SessionResumeResult> b = Task.Run(async () =>
                await service.TryResumeAsync(id, created.ResumeToken, new ArrayBufferWriter<byte>()));

            SessionResumeResult[] results = await Task.WhenAll(a, b);

            int winners = 0;
            foreach (SessionResumeResult result in results)
            {
                if (result.Succeeded)
                {
                    winners++;
                }
            }

            Assert.Equal(1, winners);
        }
    }

    // ── 만료 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Non_positive_ttl_is_rejected_at_assembly()
    {
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });

        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionResumeService(store, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionResumeService(store, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Null_store_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionResumeService(null!));
    }
}
