using System;
using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Sessions;
using StackExchange.Redis;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// 서킷 브레이커가 <b>실제 Redis 장애</b>에서 동작하는지 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 스텁으로는 이 주장을 증명할 수 없다.</b> 데코레이터 테스트
/// (<c>CircuitBreakerTests</c>)는 내가 만든 예외를 던지는 스텁을 쓰므로, 실제
/// StackExchange.Redis 가 던지는 예외가 <b>기본 분류에 걸리는지</b>는 확인하지 못한다.
/// 분류가 틀리면(예: 벤더 예외가 <see cref="ArgumentException"/> 을 상속한다면) 회로가
/// 영원히 열리지 않고, 그 사실을 장애 때 알게 된다.
/// </para>
/// <para>
/// <b>Docker 가 필요 없다.</b> 죽은 종단으로 연결하는 것이 곧 장애 재현이므로 컨테이너를
/// 띄우지 않는다 — 그래서 이 검증은 <b>모든 환경에서 항상 돈다</b>.
/// </para>
/// </remarks>
public sealed class RedisCircuitBreakerTests
{
    /// <summary>아무도 듣지 않는 포트. 연결 시도는 반드시 실패한다.</summary>
    private const string DeadEndpoint = "127.0.0.1:6399";

    private static ConfigurationOptions DeadConfiguration() => new()
    {
        EndPoints = { DeadEndpoint },

        // 연결 실패를 예외가 아니라 "끊긴 상태" 로 받아 명령 시점에 던지게 한다.
        // 이것이 실제 운영에서 Redis 가 죽었을 때의 모습이다(멀티플렉서는 살아 있고 명령이 실패한다).
        AbortOnConnectFail = false,
        ConnectTimeout = 300,
        SyncTimeout = 300,
        ConnectRetry = 1,
    };

    [Fact]
    public async Task Real_redis_connection_failures_open_the_circuit_and_then_fail_fast()
    {
        using ConnectionMultiplexer multiplexer =
            await ConnectionMultiplexer.ConnectAsync(DeadConfiguration());

        RedisSessionStore inner = new(multiplexer, new RedisSessionStoreOptions { KeyPrefix = "chsm:cb:" });
        CircuitBreaker breaker = new(new CircuitBreakerOptions
        {
            Name = "redis-session",
            FailureThreshold = 2,
            BreakDuration = TimeSpan.FromMinutes(1),
        });

        CircuitBreakingSessionStore store = new(inner, breaker);
        SessionId id = new(new ObjectId(1));

        // ① 실제 벤더 예외가 인프라 장애로 분류되어 회로를 연다.
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await store.TryReadAsync(id, new ArrayBufferWriter<byte>()));
        }

        Assert.Equal(CircuitState.Open, breaker.State);

        // ② 이제 Redis 를 호출하지 않고 즉시 실패한다 — 그것이 빠른 실패의 목적이다.
        //    (호출했다면 SyncTimeout 만큼 기다렸을 것이다.)
        await Assert.ThrowsAsync<CircuitOpenException>(async () =>
            await store.TryReadAsync(id, new ArrayBufferWriter<byte>()));

        await Assert.ThrowsAsync<CircuitOpenException>(async () =>
            await store.TryWriteAsync(id, default, SessionVersion.None));
    }

    [Fact]
    public async Task Vendor_exceptions_are_classified_as_infrastructure_failures()
    {
        // ★ 기본 분류가 실제 벤더 예외를 통과시키는지 직접 확인한다. 여기가 틀리면
        // 위 테스트가 통과해도 그것은 우연이다.
        using ConnectionMultiplexer multiplexer =
            await ConnectionMultiplexer.ConnectAsync(DeadConfiguration());

        RedisSessionStore store = new(multiplexer, new RedisSessionStoreOptions { KeyPrefix = "chsm:cb2:" });

        Exception captured = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await store.TryReadAsync(new SessionId(new ObjectId(2)), new ArrayBufferWriter<byte>()));

        Assert.True(
            CircuitBreakingSessionStore.IsInfrastructureFailure(captured),
            $"벤더 예외가 인프라 장애로 분류되지 않았다: {captured.GetType().FullName}. " +
            "이대로면 Redis 가 죽어도 회로가 열리지 않는다.");
    }
}
