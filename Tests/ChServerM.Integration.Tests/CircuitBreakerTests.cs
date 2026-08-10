using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 서킷 브레이커(ADR-0027 보류 해제)와 세션 저장소 데코레이터를 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>가장 중요한 단언은 "무엇을 실패로 세지 <i>않는가</i>" 다.</b> CAS 충돌과
/// 호출자 버그를 실패로 세면 <b>부하를 견디라고 만든 장치가 부하 때문에 서비스를 끊는다</b> —
/// 정확히 정반대 결과다.
/// </para>
/// </remarks>
public sealed class CircuitBreakerTests
{
    private static SessionId Id(int seed) => new(new ObjectId(seed));

    private static CircuitBreaker Create(
        ManualTime time,
        int threshold = 3,
        int halfOpenSuccesses = 2,
        int probes = 1) =>
        new(
            new CircuitBreakerOptions
            {
                Name = "test",
                FailureThreshold = threshold,
                BreakDuration = TimeSpan.FromSeconds(10),
                HalfOpenSuccessThreshold = halfOpenSuccesses,
                HalfOpenConcurrentProbes = probes,
            },
            time);

    // ── 상태 전이 ───────────────────────────────────────────────────────────

    [Fact]
    public void Opens_after_consecutive_failures()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 3);

        for (int i = 0; i < 2; i++)
        {
            Assert.True(breaker.TryEnter());
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Closed, breaker.State);

        Assert.True(breaker.TryEnter());
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void Success_resets_the_consecutive_counter()
    {
        // "연속" 실패의 정의 — 중간에 성공이 끼면 처음부터 다시 센다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 3);

        breaker.TryEnter();
        breaker.RecordFailure();
        breaker.TryEnter();
        breaker.RecordFailure();
        breaker.TryEnter();
        breaker.RecordSuccess(); // 되돌린다

        breaker.TryEnter();
        breaker.RecordFailure();
        breaker.TryEnter();
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void Stays_open_until_the_break_duration_elapses()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 1);

        breaker.TryEnter();
        breaker.RecordFailure();
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(breaker.TryEnter());
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(breaker.TryEnter()); // 시험이 열린다
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
    }

    [Fact]
    public void HalfOpen_admits_only_the_configured_probe_count()
    {
        // ★ 시험은 한 번에 하나만 — 여럿을 동시에 보내면 아직 아픈 대상에 순간 부하를 준다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 1, probes: 1);

        breaker.TryEnter();
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(breaker.TryEnter());  // 시험 자리 1개 점유
        Assert.False(breaker.TryEnter()); // 두 번째는 거부
    }

    [Fact]
    public void HalfOpen_closes_after_consecutive_successes()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 1, halfOpenSuccesses: 2);

        breaker.TryEnter();
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(breaker.TryEnter());
        breaker.RecordSuccess();
        Assert.Equal(CircuitState.HalfOpen, breaker.State); // 아직 하나 더 필요하다

        Assert.True(breaker.TryEnter());
        breaker.RecordSuccess();
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void HalfOpen_reopens_on_a_single_failure()
    {
        // 시험은 "회복했는가" 를 묻는 것이므로 한 번의 실패가 곧 "아직 아니다" 다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 1);

        breaker.TryEnter();
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(breaker.TryEnter());
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryEnter()); // 차단 시간이 새로 시작됐다
    }

    [Fact]
    public void Probe_slot_is_released_so_the_circuit_can_eventually_close()
    {
        // ★★ 시험 자리를 반납하지 않으면 회로가 **영원히 닫히지 않는다** — 레거시
        // ExecutableTaskDispatcherM 이 try/finally 누락으로 디스패처를 영구 정지시킨 것과
        // 같은 부류다(9.2). 반납이 실제로 되는지 반복으로 확인한다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 1, halfOpenSuccesses: 5, probes: 1);

        breaker.TryEnter();
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));

        for (int i = 0; i < 5; i++)
        {
            Assert.True(breaker.TryEnter(), $"{i}번째 시험 자리를 잡지 못했다 — 반납이 누락됐다.");
            breaker.RecordSuccess();
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_threshold_is_rejected_at_assembly(int threshold)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CircuitBreaker(new CircuitBreakerOptions { FailureThreshold = threshold }));
    }

    // ── 데코레이터: 무엇을 실패로 세는가 ────────────────────────────────────

    [Fact]
    public async Task Conflict_is_not_a_failure()
    {
        // ★★ 이 단언이 이 설계의 핵심이다. CAS 충돌을 실패로 세면 **경합이 심할 때 멀쩡한
        // 저장소를 차단**하게 된다 — 부하를 견디라고 만든 장치가 부하 때문에 서비스를 끊는다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 3);
        StubStore stub = new() { WriteResult = SessionWriteResult.Conflict };
        CircuitBreakingSessionStore store = new(stub, breaker);

        for (int i = 0; i < 20; i++)
        {
            SessionWriteResult result = await store.TryWriteAsync(Id(1), default, SessionVersion.None);
            Assert.False(result.Succeeded);
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task NotFound_is_not_a_failure()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 3);
        StubStore stub = new(); // 기본이 NotFound
        CircuitBreakingSessionStore store = new(stub, breaker);

        for (int i = 0; i < 20; i++)
        {
            Assert.False((await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>())).Found);
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task Caller_bugs_and_cancellation_are_not_failures()
    {
        // ★ 호출자 버그로 저장소가 차단되면, 잘못된 코드 한 줄이 서비스를 끊는다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 2);
        StubStore stub = new() { ThrowOnRead = () => new ArgumentOutOfRangeException("bug") };
        CircuitBreakingSessionStore store = new(stub, breaker);

        for (int i = 0; i < 10; i++)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));
        }

        Assert.Equal(CircuitState.Closed, breaker.State);

        stub.ThrowOnRead = () => new OperationCanceledException();
        for (int i = 0; i < 10; i++)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task Infrastructure_failures_open_the_circuit_and_then_fail_fast()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 3);
        StubStore stub = new() { ThrowOnRead = () => new TimeoutException("Redis 응답 없음") };
        CircuitBreakingSessionStore store = new(stub, breaker);

        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));
        }

        Assert.Equal(CircuitState.Open, breaker.State);

        // ★ 이제 대상을 호출하지 않는다 — 빠른 실패가 목적이다.
        int callsBefore = stub.ReadCalls;
        await Assert.ThrowsAsync<CircuitOpenException>(async () =>
            await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));

        Assert.Equal(callsBefore, stub.ReadCalls);
    }

    [Fact]
    public async Task Open_circuit_throws_rather_than_reporting_a_missing_session()
    {
        // ★★ NotFound 로 접으면 호출자가 "세션이 없다" 로 읽고 새 세션을 만든다 —
        // 그것이 곧 사용자 상태 유실이다. 조용히 잘못된 답보다 시끄러운 실패가 낫다.
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 1);
        StubStore stub = new() { ThrowOnRead = () => new TimeoutException() };
        CircuitBreakingSessionStore store = new(stub, breaker);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));

        CircuitOpenException thrown = await Assert.ThrowsAsync<CircuitOpenException>(async () =>
            await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));

        Assert.Contains("test", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Circuit_recovers_when_the_store_comes_back()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time, threshold: 2, halfOpenSuccesses: 2);
        StubStore stub = new() { ThrowOnRead = () => new TimeoutException() };
        CircuitBreakingSessionStore store = new(stub, breaker);

        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>()));
        }

        Assert.Equal(CircuitState.Open, breaker.State);

        // 저장소가 회복됐다.
        stub.ThrowOnRead = null;
        time.Advance(TimeSpan.FromSeconds(11));

        await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>());
        await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>());

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task Decorator_passes_through_when_closed()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time);
        StubStore stub = new()
        {
            ReadResult = SessionReadResult.Hit(new SessionVersion(7), 0),
            WriteResult = SessionWriteResult.Ok(new SessionVersion(8)),
        };
        CircuitBreakingSessionStore store = new(stub, breaker);

        Assert.Equal(new SessionVersion(7), (await store.TryReadAsync(Id(1), new ArrayBufferWriter<byte>())).Version);
        Assert.Equal(new SessionVersion(8), (await store.TryWriteAsync(Id(1), default, SessionVersion.None)).Version);
        Assert.True(await store.TryRemoveAsync(Id(1), new SessionVersion(8)));
        Assert.True(await store.TryRenewAsync(Id(1), new SessionVersion(8), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        ManualTime time = new();
        CircuitBreaker breaker = Create(time);

        Assert.Throws<ArgumentNullException>(() => new CircuitBreakingSessionStore(null!, breaker));
        Assert.Throws<ArgumentNullException>(() => new CircuitBreakingSessionStore(new StubStore(), null!));
    }

    /// <summary>판정 대상만 흉내내는 최소 저장소.</summary>
    private sealed class StubStore : ISessionStore
    {
        public SessionReadResult ReadResult { get; set; } = SessionReadResult.NotFound;

        public SessionWriteResult WriteResult { get; set; } = SessionWriteResult.Ok(new SessionVersion(1));

        public Func<Exception>? ThrowOnRead { get; set; }

        public int ReadCalls;

        public ValueTask<SessionReadResult> TryReadAsync(
            SessionId id, IBufferWriter<byte> destination, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ReadCalls);
            if (ThrowOnRead is { } factory)
            {
                throw factory();
            }

            return ValueTask.FromResult(ReadResult);
        }

        public ValueTask<SessionWriteResult> TryWriteAsync(
            SessionId id, ReadOnlyMemory<byte> state, SessionVersion expectedVersion,
            TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(WriteResult);

        public ValueTask<bool> TryRemoveAsync(
            SessionId id, SessionVersion expectedVersion, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> TryRenewAsync(
            SessionId id, SessionVersion expectedVersion, TimeSpan timeToLive,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    /// <summary>테스트가 시간을 직접 움직인다.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
    }
}
