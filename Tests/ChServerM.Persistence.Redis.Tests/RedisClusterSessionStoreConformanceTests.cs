using System;
using System.Threading.Tasks;
using ChServerM.Persistence.Conformance;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// <b>Redis Cluster(슬롯 검사 활성)에서</b> 어댑터가 같은 계약을 만족하는지 검증한다 (ADR-0058).
/// </summary>
/// <remarks>
/// <para>
/// 단언은 <see cref="SessionStoreConformanceTests"/> 에 있고 여기서는 <b>서버 모드만 바꾼다</b>.
/// 초판 쓰기 스크립트(전역 버전 카운터 = 두 번째 키)는 이 모드에서 <c>CROSSSLOT</c> 으로
/// 전멸했다 — 스크립트가 다시 여러 키를 만지게 되는 회귀는 이 클래스가 잡는다.
/// </para>
/// <para>
/// TTL 대기 규약은 일반 모드 적합성 테스트(<see cref="RedisSessionStoreConformanceTests"/>)와
/// 같다 — 만료 판정은 Redis 서버의 시계이므로 실제로 기다린다.
/// </para>
/// </remarks>
[Collection(RedisClusterCollection.Name)]
public sealed class RedisClusterSessionStoreConformanceTests : SessionStoreConformanceTests
{
    private readonly RedisClusterFixture _fixture;

    public RedisClusterSessionStoreConformanceTests(RedisClusterFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;

        // Docker 가 없으면 여기서 건너뛴다. 조용히 통과시키면 "검증됐다" 는 착각을 준다.
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
    }

    /// <inheritdoc/>
    protected override ISessionStore Store =>
        new RedisSessionStore(
            _fixture.Multiplexer ?? throw new InvalidOperationException("Redis 클러스터를 사용할 수 없다."),
            new RedisSessionStoreOptions { KeyPrefix = "chsm:test:" });

    /// <summary>실제로 기다려야 하므로 짧게 잡는다.</summary>
    protected override TimeSpan ShortTimeToLive => TimeSpan.FromSeconds(1);

    /// <inheritdoc/>
    protected override Task AdvanceAsync(TimeSpan delta) =>
        // 여유를 조금 둔다 — Redis 의 만료 판정과 네트워크 왕복이 밀리초 단위로 흔들린다.
        Task.Delay(delta + TimeSpan.FromMilliseconds(200));
}
