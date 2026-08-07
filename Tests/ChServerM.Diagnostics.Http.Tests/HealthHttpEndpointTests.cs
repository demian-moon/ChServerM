using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Diagnostics.Http;
using Xunit;

namespace ChServerM.Diagnostics.Http.Tests;

/// <summary>
/// HTTP 헬스 노출 어댑터(ADR-0024)를 종단으로 검증한다 — 프로브별 라우팅, 상태 → 상태코드
/// 매핑, 본문, 에러 격리. 어댑터는 Core 만 참조하므로 헬스 소스는 스텁 델리게이트로 준다
/// (Hosting 불필요).
/// </summary>
public sealed class HealthHttpEndpointTests
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    // CA2234 회피 — 문자열 대신 Uri 로 호출한다. 테스트 가독성을 위해 감싼다.
    private static Task<HttpResponseMessage> Get(string url) => Client.GetAsync(new Uri(url));

    private static Task<string> GetString(string url) => Client.GetStringAsync(new Uri(url));

    private static int FreePort()
    {
        // 포트 0 으로 바인드해 OS 가 준 포트를 읽고 닫는다 — HttpListener 는 포트 0 을 못 쓰므로
        // 실제 포트를 구해 넘긴다. 닫은 뒤 재바인드까지 짧은 경합이 있으나 테스트에선 무시할 수준.
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static HealthReport Report(HealthStatus status, params HealthReportEntry[] entries) =>
        new(status, entries);

    private static HealthHttpEndpoint Start(
        Func<HealthProbe, CancellationToken, ValueTask<HealthReport>> probe,
        out string baseUrl)
    {
        int port = FreePort();
        baseUrl = $"http://localhost:{port}";
        HealthHttpEndpoint endpoint = new(probe, new HealthHttpOptions { Prefix = $"{baseUrl}/" });
        endpoint.Start();
        return endpoint;
    }

    [Fact]
    public async Task Healthz_RoutesToLivenessProbe_AndReturns200()
    {
        HealthProbe? requested = null;
        await using HealthHttpEndpoint endpoint = Start(
            (probe, _) =>
            {
                requested = probe;
                return ValueTask.FromResult(Report(HealthStatus.Healthy));
            },
            out string baseUrl);

        HttpResponseMessage response = await Get($"{baseUrl}/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HealthProbe.Liveness, requested);
    }

    [Fact]
    public async Task Readyz_RoutesToReadinessProbe_Unhealthy_Returns503()
    {
        HealthProbe? requested = null;
        await using HealthHttpEndpoint endpoint = Start(
            (probe, _) =>
            {
                requested = probe;
                return ValueTask.FromResult(Report(HealthStatus.Unhealthy,
                    new HealthReportEntry("dep", HealthStatus.Unhealthy, "연결 실패")));
            },
            out string baseUrl);

        HttpResponseMessage response = await Get($"{baseUrl}/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HealthProbe.Readiness, requested);
    }

    [Fact]
    public async Task Degraded_Returns200_NotFailure()
    {
        // 저하는 경고이지 실패가 아니다 — 프로브는 통과(200)시킨다.
        await using HealthHttpEndpoint endpoint = Start(
            (_, _) => ValueTask.FromResult(Report(HealthStatus.Degraded)),
            out string baseUrl);

        HttpResponseMessage response = await Get($"{baseUrl}/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        await using HealthHttpEndpoint endpoint = Start(
            (_, _) => ValueTask.FromResult(Report(HealthStatus.Healthy)),
            out string baseUrl);

        HttpResponseMessage response = await Get($"{baseUrl}/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Body_ContainsAggregateStatusAndEntries()
    {
        await using HealthHttpEndpoint endpoint = Start(
            (_, _) => ValueTask.FromResult(Report(HealthStatus.Healthy,
                new HealthReportEntry("acceptance", HealthStatus.Healthy, "수용 중"),
                new HealthReportEntry("execution-model", HealthStatus.Healthy, null))),
            out string baseUrl);

        string body = await GetString($"{baseUrl}/readyz");

        Assert.Contains("Healthy", body);
        Assert.Contains("acceptance", body);
        Assert.Contains("execution-model", body);
    }

    [Fact]
    public async Task ProbeThrows_Returns500_AndLoopSurvives()
    {
        bool first = true;
        await using HealthHttpEndpoint endpoint = Start(
            (_, _) =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException("boom");
                }

                return ValueTask.FromResult(Report(HealthStatus.Healthy));
            },
            out string baseUrl);

        // 첫 요청은 프로브가 던져 500 — accept 루프는 죽지 않는다.
        HttpResponseMessage failed = await Get($"{baseUrl}/healthz");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        // 다음 요청은 정상 처리된다(루프 생존 증거).
        HttpResponseMessage recovered = await Get($"{baseUrl}/healthz");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
}
