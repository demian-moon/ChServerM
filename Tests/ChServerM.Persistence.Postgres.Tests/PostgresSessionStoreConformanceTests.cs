using System;
using System.Threading.Tasks;
using ChServerM.Persistence.Conformance;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Persistence.Postgres.Tests;

/// <summary>
/// PostgreSQL 어댑터가 <b>인메모리·Redis 와 똑같은 계약</b>을 만족하는지 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// 단언은 <see cref="SessionStoreConformanceTests"/> 에 있고 여기서는 <b>대상만 바꾼다</b>.
/// 성격이 가장 다른 세 번째 구현이 같은 스위트를 통과하는 것이 <b>축이 진짜로 교체
/// 가능하다</b>는 가장 강한 증거다 — 인메모리는 참조, Redis 는 원격 KV, 이쪽은 관계형이다.
/// </para>
/// <para>
/// <b>⚠ 시간은 실제로 흐른다.</b> 만료 판정을 데이터베이스 서버의 <c>now()</c> 가 하므로
/// 가짜 시계를 넣을 수 없다. Redis 와 같은 이유·같은 방식이다.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSessionStoreConformanceTests : SessionStoreConformanceTests, IAsyncDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresSessionStore? _store;

    public PostgresSessionStoreConformanceTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;

        // Docker 가 없으면 여기서 건너뛴다. 조용히 통과시키면 "검증됐다" 는 착각을 준다.
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        // 청소 타이머는 끈다 — 적합성은 지연 판정만으로도 만족돼야 한다.
        // (청소가 실제로 회수하는지는 구현 고유 테스트가 따로 본다.)
        _store = new PostgresSessionStore(
            _fixture.DataSource ?? throw new InvalidOperationException("PostgreSQL 을 사용할 수 없다."),
            new PostgresSessionStoreOptions { SweepInterval = null });
    }

    /// <inheritdoc/>
    protected override ISessionStore Store =>
        _store ?? throw new InvalidOperationException("PostgreSQL 을 사용할 수 없다.");

    /// <summary>실제로 기다려야 하므로 짧게 잡는다.</summary>
    protected override TimeSpan ShortTimeToLive => TimeSpan.FromSeconds(1);

    /// <inheritdoc/>
    protected override Task AdvanceAsync(TimeSpan delta) =>
        // 여유를 조금 둔다 — 서버 now() 판정과 왕복이 밀리초 단위로 흔들린다.
        Task.Delay(delta + TimeSpan.FromMilliseconds(200));

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
    }
}
