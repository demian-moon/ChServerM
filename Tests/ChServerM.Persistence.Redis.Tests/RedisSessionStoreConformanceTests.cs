using System;
using System.Threading.Tasks;
using ChServerM.Persistence.Conformance;
using ChServerM.Sessions;
using StackExchange.Redis;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// Redis 어댑터가 <b>인메모리 참조 구현과 똑같은 계약</b>을 만족하는지 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// 단언은 <see cref="SessionStoreConformanceTests"/> 에 있고 여기서는 <b>대상만 바꾼다</b> —
/// 그것이 요점이다. 이 클래스에 고유한 단언을 추가하고 싶어진다면 그것은 계약이 부족하다는
/// 신호이므로 적합성 쪽에 넣어야 하는지 먼저 따진다.
/// </para>
/// <para>
/// <b>⚠ 시간은 실제로 흐른다.</b> Redis 는 서버가 만료를 판정하므로 가짜 시계를 넣을 수 없다.
/// 그래서 TTL 을 <b>1초</b>로 줄이고 <see cref="AdvanceAsync"/> 가 실제로 기다린다.
/// 만료 관련 테스트가 몇 초씩 걸리는 것은 이 어댑터의 불가피한 비용이다.
/// </para>
/// </remarks>
[Collection(RedisCollection.Name)]
public sealed class RedisSessionStoreConformanceTests : SessionStoreConformanceTests
{
    private readonly RedisFixture _fixture;

    public RedisSessionStoreConformanceTests(RedisFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;

        // Docker 가 없으면 여기서 건너뛴다. 조용히 통과시키면 "검증됐다" 는 착각을 준다.
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
    }

    /// <inheritdoc/>
    protected override ISessionStore Store =>
        new RedisSessionStore(
            _fixture.Multiplexer ?? throw new InvalidOperationException("Redis 를 사용할 수 없다."),
            new RedisSessionStoreOptions { KeyPrefix = "chsm:test:" });

    /// <summary>실제로 기다려야 하므로 짧게 잡는다.</summary>
    protected override TimeSpan ShortTimeToLive => TimeSpan.FromSeconds(1);

    /// <inheritdoc/>
    protected override Task AdvanceAsync(TimeSpan delta) =>
        // 여유를 조금 둔다 — Redis 의 만료 판정과 네트워크 왕복이 밀리초 단위로 흔들린다.
        Task.Delay(delta + TimeSpan.FromMilliseconds(200));
}
