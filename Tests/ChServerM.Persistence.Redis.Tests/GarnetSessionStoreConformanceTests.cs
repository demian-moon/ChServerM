using System;
using System.Threading.Tasks;
using ChServerM.Persistence.Conformance;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// <b>Garnet</b> 위에서 <c>RedisSessionStore</c> 가 계약을 만족하는지 검증한다 — 어댑터 코드는 그대로다.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 클래스에 어댑터 코드가 한 줄도 없다는 것이 결과다.</b> 저장소를 Redis 에서 Garnet 으로
/// 바꾸는 데 필요한 것은 <b>연결 문자열뿐</b>이며, 계약 단언은
/// <see cref="SessionStoreConformanceTests"/> 를 그대로 상속한다.
/// </para>
/// <para>
/// Phase 13 의 "로컬 KV 검토" 를 문헌 조사가 아니라 <b>실행</b>으로 답한 것이다 —
/// "호환된다고 한다" 와 "우리 계약을 통과한다" 는 다른 문장이다.
/// </para>
/// </remarks>
[Collection(GarnetCollection.Name)]
public sealed class GarnetSessionStoreConformanceTests : SessionStoreConformanceTests
{
    private readonly GarnetFixture _fixture;

    public GarnetSessionStoreConformanceTests(GarnetFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;

        Skip.If(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
    }

    /// <inheritdoc/>
    protected override ISessionStore Store =>
        new RedisSessionStore(
            _fixture.Multiplexer ?? throw new InvalidOperationException("Garnet 을 사용할 수 없다."),
            new RedisSessionStoreOptions { KeyPrefix = "chsm:garnet:" });

    /// <summary>실제로 기다려야 하므로 짧게 잡는다(서버가 만료를 판정한다).</summary>
    protected override TimeSpan ShortTimeToLive => TimeSpan.FromSeconds(1);

    /// <inheritdoc/>
    protected override Task AdvanceAsync(TimeSpan delta) =>
        Task.Delay(delta + TimeSpan.FromMilliseconds(200));
}
