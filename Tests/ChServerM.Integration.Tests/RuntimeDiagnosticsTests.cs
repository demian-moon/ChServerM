using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 런타임 진단(Phase 11)을 검증한다 — 커넥션·스레드·풀 상태가 운영 중에 조회되고,
/// 한 구역의 실패가 전체 스냅샷을 깨지 않으며, <b>커넥션 목록이 상한을 넘지 않는지</b>.
/// </summary>
/// <remarks>
/// 상한이 핵심 단언이다 — 전체 덤프는 응답이 MB 급이 되고 무인증 admin 엔드포인트에
/// 클라이언트 주소를 통째로 노출한다.
/// </remarks>
public sealed class RuntimeDiagnosticsTests : IDisposable
{
    private const ushort EchoId = 995;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    private sealed class ThrowingSource : IDiagnosticsSource
    {
        public string Name => "boom";

        public void Collect(IDiagnosticsWriter writer) => throw new InvalidOperationException("의도적 실패");
    }

    private sealed class StubSource(string name, string key, long value) : IDiagnosticsSource
    {
        public string Name => name;

        public void Collect(IDiagnosticsWriter writer) => writer.Write(key, value);
    }

    [Fact]
    public void Sections_are_collected_in_order()
    {
        DiagnosticsService service = new([new StubSource("a", "x", 1), new StubSource("b", "y", 2)]);

        string text = service.Collect();

        Assert.Contains("[a]", text);
        Assert.Contains("x=1", text);
        Assert.Contains("[b]", text);
        Assert.Contains("y=2", text);
        Assert.True(text.IndexOf("[a]", StringComparison.Ordinal) < text.IndexOf("[b]", StringComparison.Ordinal));
    }

    [Fact]
    public void Faulting_source_does_not_break_the_snapshot()
    {
        // 진단은 장애 중에 부르는 경로다 — 한 구역이 던져서 아무것도 못 보면 진단이 장애를 키운다.
        DiagnosticsService service = new([new ThrowingSource(), new StubSource("ok", "value", 42)]);

        string text = service.Collect();

        Assert.Contains("!error=의도적 실패", text);
        Assert.Contains("value=42", text);
    }

    [Fact]
    public async Task Transport_and_execution_model_are_auto_registered()
    {
        // 옵트인 배선 — 축이 IDiagnosticsSource 를 구현하면 자동으로 수집된다(ADR-0023 규율).
        await using ChServerMServer server = BuildServer(out _);
        await server.StartAsync(_timeout.Token);

        string text = server.Diagnostics.Collect();

        Assert.Contains("[transport.tcp]", text);
        Assert.Contains("[execution-model.partitioned]", text);
        Assert.Equal(2, server.Diagnostics.SourceCount);
    }

    [Fact]
    public async Task Transport_section_reports_connection_aggregate()
    {
        await using ChServerMServer server = BuildServer(out _);
        await server.StartAsync(_timeout.Token);

        string text = server.Diagnostics.Collect();

        Assert.Contains("connections.active=0", text);
        Assert.Contains("bound=yes", text);
        Assert.Contains("accept_fault=none", text);
    }

    [Fact]
    public async Task Connection_sample_is_capped_regardless_of_connection_count()
    {
        // ★ 상한이 이 설계의 핵심 — 전체 덤프는 응답 크기와 주소 노출을 통제할 수 없다.
        await using ChServerMServer server = BuildServer(out _);
        await server.StartAsync(_timeout.Token);
        EndPoint endPoint = server.LocalEndPoint ?? throw new InvalidOperationException("바인드 주소가 없다.");

        await using TcpClientTransport client = new(new TcpTransportOptions());
        ChServerM.Connections.IConnection[] connections = new ChServerM.Connections.IConnection[25];

        try
        {
            for (int i = 0; i < connections.Length; i++)
            {
                connections[i] = await client.ConnectAsync(endPoint, _timeout.Token);
            }

            await WaitUntilAsync(() => server.Diagnostics.Collect().Contains("connections.active=25", StringComparison.Ordinal));

            string text = server.Diagnostics.Collect();

            // 25개가 붙었지만 표본은 상한(20)까지만 나온다.
            Assert.Contains("connections.active=25", text);
            Assert.Contains("connections.sampled=20", text);

            int sampled = text.Split('\n').Count(line => line.StartsWith("connection.", StringComparison.Ordinal)
                && line.Contains(".id=", StringComparison.Ordinal));
            Assert.Equal(20, sampled);
        }
        finally
        {
            foreach (ChServerM.Connections.IConnection connection in connections)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task Execution_model_section_reports_every_partition()
    {
        // 파티션은 상한(512)이 있어 전부 내도 출력이 유계다 — 커넥션과 달리 표본을 뽑지 않는다.
        await using ChServerMServer server = BuildServer(out _, partitionCount: 4);
        await server.StartAsync(_timeout.Token);

        string text = server.Diagnostics.Collect();

        Assert.Contains("partitions=4", text);
        for (int i = 0; i < 4; i++)
        {
            Assert.Contains($"partition.{i}.thread_alive=yes", text);
            Assert.Contains($"partition.{i}.stalled=no", text);
        }
    }

    [Fact]
    public async Task User_sources_can_be_added()
    {
        await using ChServerMServer server = BuildServer(out _, extraSource: new StubSource("app", "sessions", 7));
        await server.StartAsync(_timeout.Token);

        string text = server.Diagnostics.Collect();

        Assert.Contains("[app]", text);
        Assert.Contains("sessions=7", text);
    }

    [Fact]
    public void Null_source_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerBuilder().AddDiagnosticsSource(null!));
    }

    private static ChServerMServer BuildServer(
        out TcpServerTransport transport,
        int partitionCount = 2,
        IDiagnosticsSource? extraSource = null)
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        transport = new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), new TcpTransportOptions());

        ServerBuilder builder = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseExecutionModel(new PartitionedExecutionModel(
                new PartitionedExecutionOptions { PartitionCount = partitionCount }))
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled)));

        if (extraSource is not null)
        {
            builder.AddDiagnosticsSource(extraSource);
        }

        return builder.Build();
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("조건이 제한 시간 안에 만족되지 않았다.");
            }

            await Task.Delay(20, _timeout.Token);
        }
    }
}
