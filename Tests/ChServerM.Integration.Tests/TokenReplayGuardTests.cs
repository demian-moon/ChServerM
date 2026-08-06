using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Hosting;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 인메모리 리플레이 등록부(T-05)의 불변식 — 1회용 보장, TTL 유계, 포화 거부,
/// 그리고 같은 토큰 동시 클레임에서 정확히 하나만 이긴다는 경쟁 계약을 고정한다.
/// </summary>
public sealed class TokenReplayGuardTests
{
    private static readonly byte[] TokenA = [1, 2, 3, 4];
    private static readonly byte[] TokenB = [5, 6, 7, 8];

    [Fact]
    public void FirstClaim_Succeeds_And_ReplayIsRejected()
    {
        InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());

        Assert.True(guard.TryClaim(TokenA));
        Assert.False(guard.TryClaim(TokenA));

        // 내용이 같으면 다른 배열이라도 같은 토큰이다 — 구조적 동등성.
        Assert.False(guard.TryClaim([1, 2, 3, 4]));
    }

    [Fact]
    public void DistinctTokens_AreIndependent()
    {
        InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());

        Assert.True(guard.TryClaim(TokenA));
        Assert.True(guard.TryClaim(TokenB));
        Assert.Equal(2, guard.Count);
    }

    [Fact]
    public void EmptyToken_IsAlwaysRejected()
    {
        InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());

        Assert.False(guard.TryClaim(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void ExpiredEntry_CanBeReclaimed()
    {
        // TTL 은 만료 검증이 아니라 메모리 유계 장치다 — 죽은 항목은 첫 사용으로 되살아난다.
        // (계약상 만료 토큰은 앱 검증이 먼저 거르므로, 이 경로는 견고성이지 보안 완화가 아니다.)
        ManualTimeProvider time = new();
        InMemoryTokenReplayGuard guard = new(
            new TokenReplayGuardOptions { Ttl = TimeSpan.FromMinutes(1) }, time);

        Assert.True(guard.TryClaim(TokenA));
        Assert.False(guard.TryClaim(TokenA));

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.True(guard.TryClaim(TokenA));
        Assert.False(guard.TryClaim(TokenA));
    }

    [Fact]
    public void Saturated_Guard_RejectsNewClaims_UntilEntriesExpire()
    {
        ManualTimeProvider time = new();
        InMemoryTokenReplayGuard guard = new(
            new TokenReplayGuardOptions { MaxEntries = 2, Ttl = TimeSpan.FromMinutes(1) }, time);

        Assert.True(guard.TryClaim(TokenA));
        Assert.True(guard.TryClaim(TokenB));

        // 포화 — 만료된 것이 없으므로 정리해도 자리가 없다. 거부가 붕괴보다 낫다(9.6).
        Assert.False(guard.TryClaim([9, 9, 9]));

        // TTL 이 지나면 포화 시점 정리가 자리를 만든다.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(guard.TryClaim([9, 9, 9]));
    }

    [Fact]
    public async Task ConcurrentClaims_OfSameToken_ExactlyOneWins()
    {
        // 동시성 버그는 반복 실행으로 잡는다(9.9) — 단발은 경합을 재현하지 않는다.
        const int Iterations = 50;
        const int Claimers = 8;

        for (int i = 0; i < Iterations; i++)
        {
            InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());
            byte[] token = BitConverter.GetBytes(i);

            using CountdownEvent ready = new(Claimers);
            using ManualResetEventSlim go = new();

            Task<bool>[] claims = Enumerable.Range(0, Claimers)
                .Select(_ => Task.Run(() =>
                {
                    ready.Signal();
                    go.Wait();
                    return guard.TryClaim(token);
                }))
                .ToArray();

            ready.Wait();
            go.Set();
            bool[] results = await Task.WhenAll(claims);

            Assert.Equal(1, results.Count(static won => won));
        }
    }

    [Fact]
    public void Invalid_options_are_rejected_at_assembly()
    {
        Assert.Throws<InvalidOperationException>(
            static () => new InMemoryTokenReplayGuard(new TokenReplayGuardOptions { MaxEntries = 0 }));
        Assert.Throws<InvalidOperationException>(
            static () => new InMemoryTokenReplayGuard(new TokenReplayGuardOptions { Ttl = TimeSpan.Zero }));
    }

    /// <summary>손으로 감는 시계 — TTL 경계를 실제 대기 없이 검증한다.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
