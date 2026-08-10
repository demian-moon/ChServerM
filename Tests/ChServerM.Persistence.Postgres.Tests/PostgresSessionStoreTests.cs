using System;
using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;
using Npgsql;
using Xunit;

namespace ChServerM.Persistence.Postgres.Tests;

/// <summary>
/// PostgreSQL 어댑터의 <b>구현 고유</b> 동작을 검증한다.
/// </summary>
/// <remarks>
/// 계약 자체는 <c>SessionStoreConformanceTests</c> 가 검증한다(세 구현이 같은 스위트를
/// 통과한다). 여기 남는 것은 이 구현에만 있는 것 — <b>청소</b>(PostgreSQL 에는 네이티브
/// 만료가 없다)와 <b>식별자 화이트리스트</b>(SQL 에 직접 삽입되는 값이다).
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSessionStoreTests
{
    private readonly PostgresFixture _fixture;

    public PostgresSessionStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
    }

    private NpgsqlDataSource DataSource =>
        _fixture.DataSource ?? throw new InvalidOperationException("PostgreSQL 을 사용할 수 없다.");

    private static SessionId NewId() => new(new ObjectId(Random.Shared.NextInt64(1, long.MaxValue)));

    // ── 청소 (이 구현에만 있다 — Redis 는 서버가 회수한다) ────────────────────

    [SkippableFact]
    public async Task Sweep_deletes_expired_rows()
    {
        // ★ PostgreSQL 에는 네이티브 TTL 이 없다. 지연 판정만으로는 **다시 조회되지 않는
        // 세션**(끊긴 클라이언트의 상태)이 테이블에 영원히 남는다.
        PostgresSessionStore store = new(
            DataSource, new PostgresSessionStoreOptions { SweepInterval = null });

        await using (store.ConfigureAwait(false))
        {
            SessionId[] ids = new SessionId[20];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = NewId();
                await store.TryWriteAsync(ids[i], new byte[] { 1 }, SessionVersion.None, TimeSpan.FromMilliseconds(300));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(600));

            // 계약상으로는 이미 "없는 것" 이다 — 그러나 행은 남아 있다.
            Assert.False((await store.TryReadAsync(ids[0], new ArrayBufferWriter<byte>())).Found);
            Assert.True(await CountRowsAsync(ids) > 0);

            int deleted = await store.SweepAsync();

            Assert.True(deleted >= ids.Length, $"청소가 {deleted} 행만 지웠다 — 만료 행이 남는다.");
            Assert.Equal(0, await CountRowsAsync(ids));
        }
    }

    [SkippableFact]
    public async Task Sweep_respects_the_batch_limit()
    {
        // ⚠ 상한이 없으면 만료 행이 수백만일 때 긴 잠금으로 서비스 쿼리를 막는다.
        PostgresSessionStore store = new(
            DataSource, new PostgresSessionStoreOptions { SweepInterval = null, SweepBatchSize = 3 });

        await using (store.ConfigureAwait(false))
        {
            SessionId[] ids = new SessionId[10];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = NewId();
                await store.TryWriteAsync(ids[i], new byte[] { 1 }, SessionVersion.None, TimeSpan.FromMilliseconds(300));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(600));

            Assert.Equal(3, await store.SweepAsync());
        }
    }

    // ── 조립 검증 ───────────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData("chsm_session; DROP TABLE users")]
    [InlineData("chsm\"session")]
    [InlineData("ChsmSession")] // 대문자도 거부한다 — 화이트리스트가 좁을수록 안전하다
    [InlineData("")]
    public void Unsafe_identifiers_are_rejected(string tableName)
    {
        // ★ 식별자는 매개변수로 바인딩할 수 없어 SQL 에 직접 삽입된다.
        // **이스케이프하는 대신 거부한다** — 화이트리스트가 좁을수록 실수할 여지가 없다.
        Assert.Throws<InvalidOperationException>(() =>
            new PostgresSessionStore(DataSource, new PostgresSessionStoreOptions { TableName = tableName }));
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_sweep_interval_is_rejected(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PostgresSessionStore(
                DataSource,
                new PostgresSessionStoreOptions { SweepInterval = TimeSpan.FromSeconds(seconds) }));
    }

    [SkippableFact]
    public void Non_positive_batch_size_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PostgresSessionStore(DataSource, new PostgresSessionStoreOptions { SweepBatchSize = 0 }));
    }

    [SkippableFact]
    public void Null_data_source_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PostgresSessionStore(null!));
    }

    [SkippableFact]
    public async Task Use_after_dispose_throws()
    {
        PostgresSessionStore store = new(DataSource, new PostgresSessionStoreOptions { SweepInterval = null });
        await store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.TryReadAsync(NewId(), new ArrayBufferWriter<byte>()));
    }

    [SkippableFact]
    public async Task Schema_creation_is_idempotent()
    {
        // 재기동마다 부를 수 있어야 한다 — 그래야 개발 흐름에서 쓸모가 있다.
        PostgresSessionStore store = new(DataSource, new PostgresSessionStoreOptions { SweepInterval = null });

        await using (store.ConfigureAwait(false))
        {
            await store.EnsureSchemaAsync();
            await store.EnsureSchemaAsync();
        }
    }

    private async Task<int> CountRowsAsync(SessionId[] ids)
    {
        long[] raw = new long[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            raw[i] = ids[i].Value.Value;
        }

        NpgsqlCommand command = DataSource.CreateCommand(
            "SELECT count(*) FROM chsm_session WHERE id = ANY(@ids);");

        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("ids", raw);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
