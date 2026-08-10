using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using ChServerM.Buffers;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Persistence.Postgres;
using ChServerM.Persistence.Redis;
using ChServerM.Sessions;
using Npgsql;
using StackExchange.Redis;

namespace ChServerM.Bench.Sessions;

/// <summary>
/// <b>원격 세션 저장소의 왕복 비용</b> — 캐시가 필요한지 판단하기 위한 입력.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "세션 조회 앞에 캐시를 둘 것인가" 는 <b>원격 왕복이 얼마나 비싼지</b>를
/// 모르면 답할 수 없다. 이 벤치마크가 그 숫자를 만든다 — 측정 없는 최적화 금지(CLAUDE.md 2절)는
/// 캐시 같은 <b>구조적 추가</b>에 특히 강하게 적용된다. 캐시는 한 번 들어가면 일관성 문제를
/// 영구히 데려오기 때문이다.
/// </para>
/// <para>
/// <b>⚠ 외부 종단을 환경 변수로 받는다.</b> BenchmarkDotNet 은 벤치마크를 <b>별도 프로세스로</b>
/// 띄우므로 Testcontainers 로 컨테이너 수명을 관리하기 어렵다(컨테이너가 부모에 묶인다).
/// 컨테이너는 밖에서 띄우고 종단만 넘긴다. 종단이 없으면 그 팔은 <b>건너뛴다</b> —
/// 조용히 0 을 보고하면 "원격이 공짜" 라는 거짓을 만든다.
/// </para>
/// <code>
///   docker run -d -p 16380:6379 redis:7-alpine
///   docker run -d -p 15432:5432 -e POSTGRES_PASSWORD=bench -e POSTGRES_DB=bench postgres:17-alpine
///   set CHSM_BENCH_REDIS=127.0.0.1:16380
///   set CHSM_BENCH_POSTGRES=Host=127.0.0.1;Port=15432;Username=postgres;Password=bench;Database=bench
/// </code>
/// <para>
/// <b>인메모리를 기준선으로 함께 잰다.</b> 원격의 절대 시간만 보면 "느리다" 밖에 말할 수 없다 —
/// <b>몇 배</b>인지가 캐시 판단의 근거다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
#pragma warning disable CA2012 // 아래 저장소들은 조회 하나가 한 왕복이라 동기 대기가 곧 그 왕복의 비용이다.
public class RemoteSessionStoreBenchmarks
{
    /// <summary>Redis 종단(<c>host:port</c>). 없으면 Redis 팔을 건너뛴다.</summary>
    public const string RedisEndpointVariable = "CHSM_BENCH_REDIS";

    /// <summary>PostgreSQL 연결 문자열. 없으면 PostgreSQL 팔을 건너뛴다.</summary>
    public const string PostgresConnectionVariable = "CHSM_BENCH_POSTGRES";

    // CA1859 억제 — 분석기는 구체 타입을 권하지만, 이 벤치마크가 재려는 것은 **프레임워크가
    // 실제로 쓰는 경로**다. 구체 타입으로 바꾸면 JIT 이 devirtualize 해서 프로덕션과 다른
    // 것을 재게 된다. 인터페이스 디스패치 비용을 포함하는 것이 이 측정의 요점이다.
#pragma warning disable CA1859
    private InMemorySessionStore _inMemory = null!;
    private ISessionStore? _redis;
    private ISessionStore? _postgres;

    private IConnectionMultiplexer? _multiplexer;
    private NpgsqlDataSource? _dataSource;
    private PostgresSessionStore? _postgresStore;

    private PooledBufferWriter _destination = null!;
    private byte[] _state = null!;

    private SessionId _inMemoryId;
    private SessionId _redisId;
    private SessionId _postgresId;

    private SessionVersion _inMemoryVersion;
    private SessionVersion _redisVersion;
    private SessionVersion _postgresVersion;
#pragma warning restore CA1859

    /// <summary>세션 상태 크기.</summary>
    [Params(256)]
    public int StateLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _state = new byte[StateLength];
#pragma warning disable CA5394 // 측정용 페이로드 — 보안 난수가 필요 없다.
        Random.Shared.NextBytes(_state);
#pragma warning restore CA5394
        _destination = new PooledBufferWriter(StateLength);

        _inMemory = new InMemorySessionStore(new InMemorySessionStoreOptions { SweepInterval = null });
        _inMemoryId = new SessionId(new ObjectId(1));
        _inMemoryVersion = Seed(_inMemory, _inMemoryId);

        string? redisEndpoint = Environment.GetEnvironmentVariable(RedisEndpointVariable);
        if (!string.IsNullOrEmpty(redisEndpoint))
        {
            _multiplexer = ConnectionMultiplexer.Connect(redisEndpoint);
            _redis = new RedisSessionStore(_multiplexer, new RedisSessionStoreOptions { KeyPrefix = "chsm:bench:" });
            _redisId = new SessionId(new ObjectId(2));
            _redisVersion = Seed(_redis, _redisId);
        }

        string? postgresConnection = Environment.GetEnvironmentVariable(PostgresConnectionVariable);
        if (!string.IsNullOrEmpty(postgresConnection))
        {
            _dataSource = NpgsqlDataSource.Create(postgresConnection);
            _postgresStore = new PostgresSessionStore(
                _dataSource, new PostgresSessionStoreOptions { SweepInterval = null });
            _postgresStore.EnsureSchemaAsync().GetAwaiter().GetResult();

            _postgres = _postgresStore;
            _postgresId = new SessionId(new ObjectId(3));
            _postgresVersion = Seed(_postgres, _postgresId);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _inMemory.Dispose();
        _destination.Dispose();
        _postgresStore?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dataSource?.Dispose();
        _multiplexer?.Dispose();
    }

    // ── 읽기 (캐시 판단의 핵심 — 요청마다 일어나는 경로다) ────────────────────

    [Benchmark(Baseline = true, Description = "인메모리 읽기")]
    public int InMemoryRead() => Read(_inMemory, _inMemoryId);

    [Benchmark(Description = "Redis 읽기 (1 왕복)")]
    public int RedisRead() => _redis is null ? 0 : Read(_redis, _redisId);

    [Benchmark(Description = "PostgreSQL 읽기 (1 왕복)")]
    public int PostgresRead() => _postgres is null ? 0 : Read(_postgres, _postgresId);

    // ── 쓰기 ────────────────────────────────────────────────────────────────

    [Benchmark(Description = "인메모리 CAS 쓰기")]
    public bool InMemoryWrite()
    {
        SessionWriteResult result = _inMemory
            .TryWriteAsync(_inMemoryId, _state, _inMemoryVersion).GetAwaiter().GetResult();
        _inMemoryVersion = result.Version;
        return result.Succeeded;
    }

    [Benchmark(Description = "Redis CAS 쓰기 (1 왕복)")]
    public bool RedisWrite()
    {
        if (_redis is null)
        {
            return false;
        }

        SessionWriteResult result = _redis
            .TryWriteAsync(_redisId, _state, _redisVersion).GetAwaiter().GetResult();
        _redisVersion = result.Version;
        return result.Succeeded;
    }

    [Benchmark(Description = "PostgreSQL CAS 쓰기 (1 왕복)")]
    public bool PostgresWrite()
    {
        if (_postgres is null)
        {
            return false;
        }

        SessionWriteResult result = _postgres
            .TryWriteAsync(_postgresId, _state, _postgresVersion).GetAwaiter().GetResult();
        _postgresVersion = result.Version;
        return result.Succeeded;
    }

    private int Read(ISessionStore store, SessionId id)
    {
        _destination.Clear();
        return store.TryReadAsync(id, _destination).GetAwaiter().GetResult().Length;
    }

    private SessionVersion Seed(ISessionStore store, SessionId id)
    {
        // 이미 있으면 지우고 새로 만든다 — 반복 실행에서 상태가 남아 있을 수 있다.
        SessionReadResult existing = store
            .TryReadAsync(id, new ArrayBufferWriter<byte>()).GetAwaiter().GetResult();

        if (existing.Found)
        {
            store.TryRemoveAsync(id, existing.Version).GetAwaiter().GetResult();
        }

        return store.TryWriteAsync(id, _state, SessionVersion.None).GetAwaiter().GetResult().Version;
    }
}
#pragma warning restore CA2012
