using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace ChServerM.Persistence.Redis.Tests;

/// <summary>
/// 테스트용 <b>클러스터 모드</b> Redis 컨테이너 — 슬롯 검사가 켜진 단일 노드.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — ADR-0058 의 회귀 게이트다.</b> 초판 쓰기 스크립트는 세션 키 + 전역 버전
/// 카운터, 키 둘을 만졌고 클러스터에서는 <c>CROSSSLOT</c> 으로 <b>모든 쓰기가 거부</b>됐다.
/// 일반 모드 컨테이너는 슬롯 검사를 하지 않으므로 그 결함을 <b>영원히 잡지 못한다</b> —
/// 클러스터 모드로 띄운 노드 하나가 슬롯 검사를 켜 주며, 스크립트가 다시 두 키를 만지게
/// 되는 순간 적합성 스위트가 여기서 깨진다.
/// </para>
/// <para>
/// <b>노드 하나가 슬롯 16384개를 전부 가진다.</b> 검증 대상은 데이터 분산이 아니라
/// <b>슬롯 제약 아래에서의 스크립트 동작</b>이므로 노드 수는 필요 없다. 다중 노드 구성은
/// 포트 고지(announce) 문제까지 얹혀 테스트 비용만 커진다.
/// </para>
/// <para>
/// <b>⚠ 고지 주소를 호스트 쪽으로 맞춰야 한다.</b> 클러스터 클라이언트는 <c>CLUSTER SLOTS</c>
/// 가 알려주는 주소로 재접속하는데, 기본값은 컨테이너 내부 IP 라 호스트에서 닿지 않는다.
/// 그래서 호스트 포트를 먼저 정해 <c>--cluster-announce-ip/-port</c> 로 알려준다 —
/// 포트 바인딩을 고정해야 하는 이유이기도 하다.
/// </para>
/// <para>
/// Docker 가 없으면 <b>건너뛰되 사유를 남긴다</b>(Redis·Garnet 픽스처와 같은 판단).
/// </para>
/// </remarks>
public sealed class RedisClusterFixture : IAsyncLifetime
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
            int hostPort = FindFreePort();

            _container = new ContainerBuilder("redis:7-alpine")
                .WithCommand(
                    "redis-server",
                    "--cluster-enabled", "yes",
                    "--cluster-announce-ip", "127.0.0.1",
                    "--cluster-announce-port", hostPort.ToString(CultureInfo.InvariantCulture),
                    "--appendonly", "no",
                    "--save", "")
                .WithPortBinding(hostPort, 6379)
                .Build();

            await _container.StartAsync();

            // 슬롯이 배정되기 전의 클러스터는 모든 데이터 명령을 거부한다(cluster_state:fail).
            // 전 슬롯을 이 노드에 배정하고 상태가 ok 로 바뀔 때까지 기다린 뒤에 연결한다.
            await AssignAllSlotsAsync(_container);

            Multiplexer = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { $"127.0.0.1:{hostPort}" },
                AbortOnConnectFail = false,
                ConnectTimeout = 2000,

                // 명령 타임아웃은 관대하게 — 2코어 CI 러너에서 명령이 2초를 넘는 일이
                // 실제로 났다(2026-08-11 CI). 정합성 테스트에서 타임아웃은 소음이다.
                SyncTimeout = 15000,
                AsyncTimeout = 15000,
            });
        }
#pragma warning disable CA1031 // Docker 부재·이미지 pull 실패 등 원인이 다양하다. 결론은 "건너뛴다" 다.
        catch (Exception ex)
        {
            SkipReason = $"Redis 클러스터 컨테이너를 띄울 수 없다 (Docker 미실행?): {ex.GetType().Name}: {ex.Message}";
            await DisposeAsync();
        }
#pragma warning restore CA1031
    }

    /// <summary>전 슬롯(0~16383)을 배정하고 클러스터 상태가 ok 가 될 때까지 기다린다.</summary>
    private static async Task AssignAllSlotsAsync(IContainer container)
    {
        // 서버가 아직 준비 전이면 redis-cli 가 실패한다 — 짧게 재시도한다.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            ExecResult assign = await container.ExecAsync(
                ["redis-cli", "CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);

            if (assign.ExitCode == 0 && assign.Stdout.Contains("OK", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(250);
        }

        for (int attempt = 0; attempt < 40; attempt++)
        {
            ExecResult info = await container.ExecAsync(["redis-cli", "CLUSTER", "INFO"]);
            if (info.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("클러스터 상태가 제한 시간 안에 ok 가 되지 않았다.");
    }

    /// <summary>호스트에서 지금 비어 있는 TCP 포트 하나를 찾는다.</summary>
    /// <remarks>
    /// 고지 포트를 컨테이너 시작 <b>전에</b> 알아야 하므로 동적 배정을 쓸 수 없다.
    /// 반환 직후 다른 프로세스가 가로챌 이론적 여지는 있으나 테스트에서는 감수한다.
    /// </remarks>
    private static int FindFreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
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

/// <summary>클러스터 모드 Redis 테스트 컬렉션 — 컨테이너 하나를 공유한다.</summary>
[CollectionDefinition(Name)]
public sealed class RedisClusterCollection : ICollectionFixture<RedisClusterFixture>
{
    /// <summary>컬렉션 이름.</summary>
    public const string Name = "RedisCluster";
}
