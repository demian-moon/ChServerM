using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// 테스트용 <b>Garnet</b> 컨테이너 — Redis 프로토콜 호환 로컬 KV(Microsoft).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이것은 새 어댑터가 아니라 <i>주장의 시험</i>이다.</b> Phase 13 의 "로컬 KV
/// 검토" 항목을 문헌 조사 대신 <b>실행</b>으로 답한다: Garnet 이 Redis 프로토콜 호환이라면
/// <c>RedisSessionStore</c> 가 <b>코드 한 줄 없이</b> 그대로 동작해야 한다. 동작하면 축이
/// 잘 잘렸다는 증거이고, 동작하지 않으면 그 지점이 곧 호환성의 실제 경계다.
/// </para>
/// <para>
/// <b>⚠ Garnet 은 Lua 를 기본 비활성으로 띄운다.</b> <c>--lua</c> 없이 실행하면
/// <c>ERR This instance has Lua scripting support disabled</c> 로 <b>모든 쓰기가 실패</b>한다
/// (읽기는 <c>GET</c> 이라 통과하므로 <b>부분적으로만 동작하는 상태</b>가 된다 — 가장
/// 헷갈리는 실패 모드다). 이 플래그가 곧 이 조합의 운영 요구사항이다.
/// </para>
/// <para>
/// Testcontainers 에 Garnet 모듈이 없으므로 범용 <see cref="ContainerBuilder"/> 로 띄운다.
/// Docker 가 없으면 <b>건너뛰되 사유를 남긴다</b>(Redis 픽스처와 같은 판단).
/// </para>
/// </remarks>
public sealed class GarnetFixture : IAsyncLifetime
{
    private IContainer? _container;

    /// <summary>연결된 멀티플렉서. Docker 가 없으면 <see langword="null"/>.</summary>
    public IConnectionMultiplexer? Multiplexer { get; private set; }

    /// <summary>건너뛴 사유. 사용 가능하면 <see langword="null"/>.</summary>
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            // 이미지를 생성자로 넘긴다 — 매개변수 없는 ContainerBuilder 는 폐기 예정이다.
            _container = new ContainerBuilder("ghcr.io/microsoft/garnet:latest")

                // ⚠ Lua 를 켜지 않으면 CAS 스크립트가 전부 실패한다.
                .WithCommand("--lua")
                .WithPortBinding(6379, assignRandomHostPort: true)
                .Build();

            await _container.StartAsync();

            string endpoint = $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
            Multiplexer = await ConnectWhenReadyAsync(endpoint);
        }
#pragma warning disable CA1031 // Docker 부재·이미지 pull 실패 등 원인이 다양하다. 결론은 "건너뛴다" 다.
        catch (Exception ex)
        {
            SkipReason = $"Garnet 컨테이너를 띄울 수 없다 (Docker 미실행?): {ex.GetType().Name}: {ex.Message}";
            await DisposeAsync();
        }
#pragma warning restore CA1031
    }

    /// <summary>서버가 응답할 때까지 짧게 재시도하며 연결한다.</summary>
    /// <remarks>
    /// Testcontainers 의 대기 전략 API 대신 <b>직접 준비를 확인</b>한다 — 우리가 필요한 것은
    /// "포트가 열렸다" 가 아니라 <b>"명령에 응답한다"</b> 이고, 그것은 우리가 직접 물어야 안다.
    /// </remarks>
    private static async Task<IConnectionMultiplexer> ConnectWhenReadyAsync(string endpoint)
    {
        ConfigurationOptions configuration = new()
        {
            EndPoints = { endpoint },
            AbortOnConnectFail = false,
            ConnectTimeout = 2000,
            SyncTimeout = 2000,
        };

        ConnectionMultiplexer multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);

        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await multiplexer.GetDatabase().PingAsync();
                return multiplexer;
            }
#pragma warning disable CA1031 // 준비 대기다. 마지막 시도까지 실패하면 아래에서 던진다.
            catch (Exception)
            {
                await Task.Delay(250);
            }
#pragma warning restore CA1031
        }

        throw new TimeoutException($"Garnet 이 제한 시간 안에 응답하지 않았다: {endpoint}");
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

/// <summary>Garnet 테스트 컬렉션 — 컨테이너 하나를 공유한다.</summary>
[CollectionDefinition(Name)]
public sealed class GarnetCollection : ICollectionFixture<GarnetFixture>
{
    /// <summary>컬렉션 이름.</summary>
    public const string Name = "Garnet";
}
