using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace ChServerM.Cluster.Consul.Tests;

/// <summary>테스트용 Consul 에이전트 컨테이너.</summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이 어댑터의 핵심은 <i>블로킹 쿼리</i>이고, 그것은 가짜로 흉내 낼 수 없다.</b>
/// 인덱스가 언제 오르는지·같은 인덱스로 언제 깨어나는지는 Consul 이 정하는 동작이며,
/// 우리가 만든 가짜로 검증하면 <b>우리 가짜만 검증하는 것</b>이 된다
/// (ADR-0052 에서 "가짜가 진짜보다 친절해서" 속 빈 테스트가 나온 것과 같은 자리).
/// </para>
/// <para>
/// Testcontainers 에 Consul 모듈이 없으므로 범용 <see cref="ContainerBuilder"/> 로 띄운다.
/// Docker 가 없으면 <b>건너뛰되 사유를 남긴다</b>(Garnet 픽스처와 같은 판단).
/// </para>
/// </remarks>
public sealed class ConsulFixture : IAsyncLifetime
{
    private IContainer? _container;

    /// <summary>Consul HTTP API 주소. Docker 가 없으면 <see langword="null"/>.</summary>
    public Uri? Address { get; private set; }

    /// <summary>등록·해제에 쓰는 클라이언트. Docker 가 없으면 <see langword="null"/>.</summary>
    public HttpClient? Http { get; private set; }

    /// <summary>건너뛴 사유. 사용 가능하면 <see langword="null"/>.</summary>
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("hashicorp/consul:1.20")

                // dev 모드 = 단일 노드 서버. 클러스터링은 이 테스트의 주제가 아니다.
                // -client=0.0.0.0 이 없으면 컨테이너 밖에서 API 에 닿지 못한다.
                .WithCommand("agent", "-dev", "-client=0.0.0.0")
                .WithPortBinding(8500, assignRandomHostPort: true)
                .Build();

            await _container.StartAsync();

            Address = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8500)}");
            Http = new HttpClient { BaseAddress = Address };

            await WaitUntilReadyAsync();
        }
#pragma warning disable CA1031 // Docker 부재·이미지 pull 실패 등 원인이 다양하다. 결론은 "건너뛴다" 다.
        catch (Exception ex)
        {
            SkipReason = $"Consul 컨테이너를 띄울 수 없다 (Docker 미실행?): {ex.GetType().Name}: {ex.Message}";
            await DisposeAsync();
        }
#pragma warning restore CA1031
    }

    public async Task DisposeAsync()
    {
        Http?.Dispose();
        Http = null;

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    /// <summary>서비스를 등록한다. <paramref name="nodeId"/> 가 <see langword="null"/> 이면 번호 메타를 빼고 등록한다.</summary>
    /// <remarks>
    /// <b>등록은 배포의 몫이지 어댑터의 몫이 아니다</b>(ADR-0055) — 그래서 테스트가
    /// Consul API 를 직접 쓴다. 어댑터가 등록까지 했다면 이 테스트는
    /// <b>어댑터가 쓴 것을 어댑터가 읽는</b> 순환이 됐을 것이다.
    /// </remarks>
    public async Task RegisterAsync(
        string serviceName,
        string serviceId,
        ushort? nodeId,
        int port,
        string? extraMetaValue = null)
    {
        Dictionary<string, string> meta = [];

        if (nodeId is not null)
        {
            meta["chserverm-node-id"] = nodeId.Value.ToString(CultureInfo.InvariantCulture);
        }

        meta["chserverm-peer-port"] = port.ToString(CultureInfo.InvariantCulture);

        if (extraMetaValue is not null)
        {
            // 어댑터가 **읽지 않는** 필드. 인덱스만 올리고 구성원은 그대로인 상황을 만든다.
            meta["unrelated"] = extraMetaValue;
        }

        using HttpResponseMessage response = await Http!.PutAsJsonAsync(
            new Uri("/v1/agent/service/register", UriKind.Relative),
            new
            {
                ID = serviceId,
                Name = serviceName,
                Address = "10.0.0.1",
                Port = port,
                Meta = meta,
            });

        response.EnsureSuccessStatusCode();
    }

    /// <summary>서비스 등록을 해제한다.</summary>
    public async Task DeregisterAsync(string serviceId)
    {
        using HttpResponseMessage response = await Http!.PutAsync(
            new Uri($"/v1/agent/service/deregister/{serviceId}", UriKind.Relative), null);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>API 가 응답할 때까지 짧게 재시도한다.</summary>
    /// <remarks>
    /// 포트가 열린 것과 <b>리더가 선출되어 카탈로그에 답하는 것</b>은 다르다 —
    /// 후자를 직접 확인해야 첫 조회가 빈 목록을 보고 지나가지 않는다.
    /// </remarks>
    private async Task WaitUntilReadyAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        while (true)
        {
            try
            {
                using HttpResponseMessage response = await Http!.GetAsync(
                    new Uri("/v1/status/leader", UriKind.Relative), timeout.Token);

                if (response.IsSuccessStatusCode)
                {
                    string leader = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (leader.Trim('"', ' ', '\n', '\r').Length > 0)
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // 아직 안 떴다.
            }

            await Task.Delay(250, timeout.Token);
        }
    }
}
