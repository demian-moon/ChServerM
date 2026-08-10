using System;
using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Sessions;
using Npgsql;
using Xunit;

namespace ChServerM.Persistence.Postgres.Tests;

/// <summary>
/// 커넥션 풀이 고갈되면 무슨 일이 나는가 — 그리고 <b>서킷 브레이커가 그것을 어떻게 읽는가</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 풀 고갈은 운영에서 실제로 일어나고, 그때의 동작이 <b>매달림인지
/// 빠른 실패인지</b>가 장애의 크기를 정한다. 문서로 추정하지 않고 <b>실제로 고갈시켜</b>
/// 관찰한다.
/// </para>
/// <para>
/// <b>⚠ 이 테스트가 드러내는 것</b>: 풀 고갈은 <b>저장소가 아픈 것이 아니라 우리가 과다
/// 구독한 것</b>인데, 서킷 브레이커의 기본 분류는 그것을 <b>인프라 장애로 센다</b>.
/// 그 판단이 옳은지 아닌지는 상황에 달렸고, 그래서 <b>구분 가능해야</b> 한다.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPoolTests
{
    private readonly PostgresFixture _fixture;

    public PostgresPoolTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
    }

    /// <summary>풀을 1개로 조인 데이터 원본. 고갈을 재현하기 위한 것이다.</summary>
    private NpgsqlDataSource CreateTinyPool(int maxPoolSize, int timeoutSeconds)
    {
        NpgsqlConnectionStringBuilder builder = new(
            _fixture.ConnectionString ?? throw new InvalidOperationException("PostgreSQL 을 사용할 수 없다."))
        {
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 0,

            // 풀 대기 상한. 이것이 없으면 **무한히 매달린다** — 그것이 최악의 실패 모드다.
            Timeout = timeoutSeconds,
        };

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    [SkippableFact]
    public async Task Pool_exhaustion_fails_fast_rather_than_hanging()
    {
        // ★ 매달림이 아니라 제한 시간 안에 실패해야 한다. 매달리면 호출자의 스레드가
        //   묶여 **장애가 저장소에서 서버로 번진다**(서킷 브레이커의 존재 이유와 같은 논리).
        NpgsqlDataSource pool = CreateTinyPool(maxPoolSize: 1, timeoutSeconds: 1);

        await using (pool.ConfigureAwait(false))
        {
            // 풀의 유일한 커넥션을 붙잡는다.
            NpgsqlConnection held = await pool.OpenConnectionAsync();

            await using (held.ConfigureAwait(false))
            {
                PostgresSessionStore store = new(pool, new PostgresSessionStoreOptions { SweepInterval = null });

                await using (store.ConfigureAwait(false))
                {
                    DateTime started = DateTime.UtcNow;

                    Exception thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
                        await store.TryReadAsync(new SessionId(new ObjectId(1)), new ArrayBufferWriter<byte>()));

                    TimeSpan elapsed = DateTime.UtcNow - started;

                    // 제한 시간 언저리에서 끝나야 한다 — 여유를 크게 둬도 매달림과는 구분된다.
                    Assert.True(elapsed < TimeSpan.FromSeconds(15), $"풀 고갈이 {elapsed} 동안 매달렸다.");
                    Assert.NotNull(thrown);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Pool_exhaustion_is_counted_as_an_infrastructure_failure()
    {
        // ⚠ 이것은 "옳다" 가 아니라 "이렇게 동작한다" 를 고정하는 테스트다.
        //
        // 풀 고갈은 **저장소가 아픈 것이 아니라 우리가 과다 구독한 것**이다. 그런데 기본
        // 분류는 그것을 인프라 장애로 세므로 **회로가 열린다**. 부하를 덜어내는 효과는
        // 있지만, 원인이 "DB 가 느리다" 가 아니라 "풀이 작다" 일 수 있다는 것을 운영자가
        // 알아야 한다. 그래서 이 동작을 문서(docs/CONSISTENCY.md)와 함께 못 박는다.
        NpgsqlDataSource pool = CreateTinyPool(maxPoolSize: 1, timeoutSeconds: 1);

        await using (pool.ConfigureAwait(false))
        {
            NpgsqlConnection held = await pool.OpenConnectionAsync();

            await using (held.ConfigureAwait(false))
            {
                PostgresSessionStore inner = new(pool, new PostgresSessionStoreOptions { SweepInterval = null });

                await using (inner.ConfigureAwait(false))
                {
                    CircuitBreaker breaker = new(new CircuitBreakerOptions
                    {
                        Name = "postgres-pool",
                        FailureThreshold = 2,
                        BreakDuration = TimeSpan.FromMinutes(1),
                    });

                    CircuitBreakingSessionStore store = new(inner, breaker);
                    SessionId id = new(new ObjectId(2));

                    for (int i = 0; i < 2; i++)
                    {
                        await Assert.ThrowsAnyAsync<Exception>(async () =>
                            await store.TryReadAsync(id, new ArrayBufferWriter<byte>()));
                    }

                    Assert.Equal(CircuitState.Open, breaker.State);

                    // 이후에는 DB 를 건드리지도 않고 즉시 실패한다.
                    await Assert.ThrowsAsync<CircuitOpenException>(async () =>
                        await store.TryReadAsync(id, new ArrayBufferWriter<byte>()));
                }
            }
        }
    }

    [SkippableFact]
    public async Task Sized_pool_serves_concurrent_requests_without_failing()
    {
        // 대조군 — 풀이 동시성만큼 있으면 같은 부하가 아무 문제 없이 통과한다.
        // 즉 위 실패는 **저장소의 문제가 아니라 사이징의 문제**다.
        NpgsqlDataSource pool = CreateTinyPool(maxPoolSize: 16, timeoutSeconds: 10);

        await using (pool.ConfigureAwait(false))
        {
            PostgresSessionStore store = new(pool, new PostgresSessionStoreOptions { SweepInterval = null });

            await using (store.ConfigureAwait(false))
            {
                Task[] readers = new Task[16];
                for (int i = 0; i < readers.Length; i++)
                {
                    int index = i;
                    readers[i] = Task.Run(async () =>
                        await store.TryReadAsync(
                            new SessionId(new ObjectId(1000 + index)), new ArrayBufferWriter<byte>()));
                }

                await Task.WhenAll(readers);
            }
        }
    }
}
