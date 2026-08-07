using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 크래시 처리(ADR-0028)를 검증한다 — 수락 루프가 조용히 죽지 않고 헬스로 드러나는지,
/// 그리고 프로세스 전역 훅 헬퍼가 되돌릴 수 있는지.
/// </summary>
/// <remarks>
/// 여기서 막는 것은 <b>조용한 죽음</b>이다: 사용자 공급 컴포넌트가 던져 수락 루프가 끝나면
/// 서버는 살아 있지만 신규 연결을 하나도 받지 못하는데, 태스크는 <c>Unbind</c> 때까지
/// 관측되지 않아 <b>종료 시점까지 아무도 모른다</b>.
/// </remarks>
public sealed class CrashHandlingTests : IDisposable
{
    private const ushort EchoId = 950;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    /// <summary>판정 때마다 던지는 수용 제어 — 버그 있는 사용자 구현을 흉내낸다.</summary>
    private sealed class ThrowingAdmissionControl : IAdmissionControl
    {
        public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint) =>
            throw new InvalidOperationException("수용 제어 버그");
    }

    private static ChServerMServer BuildServer(TcpServerTransport transport, FramingOptions framing) =>
        new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled)))
            .Build();

    [Fact]
    public async Task Transport_is_ready_while_accepting()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        TcpServerTransport transport = new(new IPEndPoint(IPAddress.Loopback, 0), new TcpTransportOptions());

        await using ChServerMServer server = BuildServer(transport, framing);
        await server.StartAsync(_timeout.Token);

        HealthReport readiness = await server.Health.CheckHealthAsync(HealthProbe.Readiness);

        Assert.Equal(HealthStatus.Healthy, readiness.Status);
        Assert.Contains(readiness.Entries, e => e.Name == "transport");
    }

    [Fact]
    public async Task Faulting_admission_control_does_not_silently_kill_accept_loop()
    {
        // 수용 제어가 던지면 수락 루프가 끝난다 — 그 사실이 readiness 로 드러나야 한다.
        // 이 방어가 없으면 서버는 "수용 중" 을 보고하면서 아무도 받지 못한다.
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        TcpTransportOptions options = new() { AdmissionControl = new ThrowingAdmissionControl() };
        TcpServerTransport transport = new(new IPEndPoint(IPAddress.Loopback, 0), options);

        await using ChServerMServer server = BuildServer(transport, framing);
        await server.StartAsync(_timeout.Token);

        EndPoint endPoint = server.LocalEndPoint ?? throw new InvalidOperationException("바인드 주소가 없다.");

        // 연결을 시도해 수용 제어를 발화시킨다. 연결 자체는 실패해도 무방하다 —
        // 관심사는 그 뒤 서버의 상태다.
        await using (TcpClientTransport client = new(new TcpTransportOptions()))
        {
            try
            {
                await using ChServerM.Connections.IConnection connection =
                    await client.ConnectAsync(endPoint, _timeout.Token);
            }
#pragma warning disable CA1031 // 연결 성패는 이 테스트의 관심사가 아니다.
            catch (Exception)
#pragma warning restore CA1031
            {
                // 수락 루프가 죽었으므로 연결이 끊기거나 걸릴 수 있다.
            }
        }

        // 수락 루프의 고장이 readiness 로 드러난다.
        await WaitUntilAsync(async () =>
        {
            HealthReport readiness = await server.Health.CheckHealthAsync(HealthProbe.Readiness);
            return readiness.Status == HealthStatus.Unhealthy;
        });

        HealthReport report = await server.Health.CheckHealthAsync(HealthProbe.Readiness);
        HealthReportEntry entry = Assert.Single(report.Entries, e => e.Name == "transport");
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Contains("수락 루프가 중단됐다", entry.Description);
    }

    [Fact]
    public async Task Accept_fault_does_not_surface_as_exception_on_shutdown()
    {
        // 고장을 삼켜 상태로 바꿨으므로, 종료 경로가 그 예외로 터지지 않는다.
        // (예전에는 Unbind 시점에 뒤늦게 튀어나왔다.)
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        TcpTransportOptions options = new() { AdmissionControl = new ThrowingAdmissionControl() };
        TcpServerTransport transport = new(new IPEndPoint(IPAddress.Loopback, 0), options);

        ChServerMServer server = BuildServer(transport, framing);
        await server.StartAsync(_timeout.Token);

        EndPoint endPoint = server.LocalEndPoint ?? throw new InvalidOperationException("바인드 주소가 없다.");

        await using (TcpClientTransport client = new(new TcpTransportOptions()))
        {
            try
            {
                await using ChServerM.Connections.IConnection connection =
                    await client.ConnectAsync(endPoint, _timeout.Token);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        // 던지지 않아야 한다.
        await server.UnbindAsync(_timeout.Token);
        await server.DisposeAsync();
    }

    [Fact]
    public void ProcessFaultHandlers_install_is_reversible()
    {
        // 프로세스 전역 훅은 되돌릴 수 있어야 한다 — 테스트·다중 호스팅에서 필수다.
        IDisposable handle = ProcessFaultHandlers.Install(NullServerLogger.Instance);

        handle.Dispose();

        // 멱등해야 한다.
        handle.Dispose();
    }

    [Fact]
    public void ProcessFaultHandlers_rejects_null_logger()
    {
        Assert.Throws<ArgumentNullException>(() => ProcessFaultHandlers.Install(null!));
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("조건이 제한 시간 안에 만족되지 않았다.");
            }

            await Task.Delay(20, _timeout.Token);
        }
    }
}
