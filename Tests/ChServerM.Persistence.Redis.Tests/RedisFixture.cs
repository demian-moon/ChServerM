using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// 테스트용 Redis 컨테이너를 한 번 띄워 클래스들이 공유한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ Docker 가 없으면 실패가 아니라 건너뛴다.</b> 개발 머신마다 Docker 를 강제하지 않는
/// 것이 이 결정의 요점이다 — 강제하면 사람들이 테스트를 통째로 끄는 쪽을 고른다. CI 의
/// ubuntu 러너에는 Docker 가 있으므로 <b>거기서는 반드시 돈다</b>.
/// </para>
/// <para>
/// <b>그래서 건너뛴 사실이 보여야 한다.</b> 조용히 통과하면 "Redis 어댑터가 검증됐다" 는
/// 착각을 준다 — <see cref="SkipReason"/> 이 그 착각을 막는다(테스트 러너 출력에 사유가 찍힌다).
/// </para>
/// <para>
/// <b>컨테이너 하나를 모든 테스트가 공유한다.</b> 테스트마다 띄우면 한 번에 몇 초씩 든다.
/// 대신 적합성 테스트가 <c>NewId()</c> 로 겹치지 않는 키를 쓰므로 서로 오염되지 않는다.
/// </para>
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    /// <summary>연결된 멀티플렉서. Docker 가 없으면 <see langword="null"/>.</summary>
    public IConnectionMultiplexer? Multiplexer { get; private set; }

    /// <summary>건너뛴 사유. 사용 가능하면 <see langword="null"/>.</summary>
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            // RedisBuilder 가 자체 대기 전략(PING 응답)을 갖고 있다 — 직접 지정하지 않는다.
            // 이미지는 생성자로 넘긴다(매개변수 없는 생성자는 obsolete).
            _container = new RedisBuilder("redis:7-alpine").Build();

            await _container.StartAsync();
            Multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        }
#pragma warning disable CA1031 // Docker 부재·이미지 pull 실패 등 원인이 다양하다. 어느 쪽이든 결론은 "건너뛴다" 다.
        catch (Exception ex)
        {
            SkipReason = $"Redis 컨테이너를 띄울 수 없다 (Docker 미실행?): {ex.GetType().Name}: {ex.Message}";
            await DisposeAsync();
        }
#pragma warning restore CA1031
    }

    public async Task DisposeAsync()
    {
        if (Multiplexer is not null)
        {
            await Multiplexer.CloseAsync();
            Multiplexer.Dispose();
            Multiplexer = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

/// <summary>
/// Redis 테스트 컬렉션 — 컨테이너 하나를 공유한다.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    /// <summary>컬렉션 이름.</summary>
    public const string Name = "Redis";
}
