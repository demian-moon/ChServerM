using System;
using System.Threading.Tasks;
using ChServerM.Persistence.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ChServerM.Persistence.Postgres.Tests;

/// <summary>
/// 테스트용 PostgreSQL 컨테이너를 한 번 띄워 클래스들이 공유한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ Docker 가 없으면 실패가 아니라 건너뛴다.</b> Redis 픽스처와 같은 판단이다 —
/// 개발 머신마다 Docker 를 강제하면 사람들이 테스트를 통째로 끄는 쪽을 고르고,
/// 조용히 통과시키면 "검증됐다" 는 착각을 준다. <b>건너뛰되 사유를 남긴다.</b>
/// </para>
/// <para>
/// 스키마는 여기서 한 번 만든다 — 어댑터는 <b>자동으로 DDL 을 실행하지 않는다</b>
/// (운영자가 스키마 변경 시점을 통제해야 하므로).
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>연결된 데이터 원본. Docker 가 없으면 <see langword="null"/>.</summary>
    public NpgsqlDataSource? DataSource { get; private set; }

    /// <summary>건너뛴 사유. 사용 가능하면 <see langword="null"/>.</summary>
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();

            DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());

            // 스키마를 한 번 만든다. 어댑터는 이것을 자동으로 하지 않는다.
            PostgresSessionStore schema = new(DataSource);
            await using (schema.ConfigureAwait(false))
            {
                await schema.EnsureSchemaAsync();
            }
        }
#pragma warning disable CA1031 // Docker 부재·이미지 pull 실패 등 원인이 다양하다. 결론은 "건너뛴다" 다.
        catch (Exception ex)
        {
            SkipReason = $"PostgreSQL 컨테이너를 띄울 수 없다 (Docker 미실행?): {ex.GetType().Name}: {ex.Message}";
            await DisposeAsync();
        }
#pragma warning restore CA1031
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
            DataSource = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

/// <summary>PostgreSQL 테스트 컬렉션 — 컨테이너 하나를 공유한다.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>컬렉션 이름.</summary>
    public const string Name = "Postgres";
}
