using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// Bind → Unbind → Stop 3단 종료를 검증한다.
/// </summary>
/// <remarks>
/// <b>Unbind 와 Stop 사이가 무중단 배포의 창이다.</b> 로드밸런서가 새 트래픽을 다른 노드로
/// 돌리는 동안 이미 붙어 있는 클라이언트는 하던 일을 끝내야 한다.
/// 레거시에는 이 드레인 단계가 없어 종료가 곧 전원 차단이었다.
/// </remarks>
public sealed class TransportLifecycleTests
{
    private const ushort EchoMessageId = 100;

    private static MessageDelegate Echo(IFrameEncoder encoder) => async context =>
    {
        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            encoder,
            context.Header.MessageId,
            context.Payload,
            FrameFlags.None,
            context.Header.Sequence,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    };

    private static FramedConnectionHandler CreateEchoHandler(int maxPayloadLength = 4096)
    {
        FramingOptions framing = new() { MaxPayloadLength = maxPayloadLength };
        MessageDispatcherBuilder builder = new();
        builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(framing)));

        return new FramedConnectionHandler(new FixedHeaderFrameDecoder(framing), builder.Build());
    }

    [Fact]
    public async Task LocalEndPoint_IsNullBeforeBind()
    {
        InMemoryTransportHub hub = new();
        await using InMemoryServerTransport server = new(hub, new InMemoryEndPoint("srv"));

        Assert.Null(server.LocalEndPoint);
    }

    [Fact]
    public async Task LocalEndPoint_IsSetAfterBind()
    {
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new("srv");
        await using InMemoryServerTransport server = new(hub, endPoint);

        await server.BindAsync(CreateEchoHandler());

        Assert.Equal(endPoint, server.LocalEndPoint);
    }

    [Fact]
    public async Task Bind_Twice_Throws()
    {
        InMemoryTransportHub hub = new();
        await using InMemoryServerTransport server = new(hub, new InMemoryEndPoint("srv"));
        await server.BindAsync(CreateEchoHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await server.BindAsync(CreateEchoHandler()));
    }

    [Fact]
    public async Task Bind_ToOccupiedEndPoint_Throws_AndLeavesTransportRebindable()
    {
        // 실패한 바인드가 상태를 오염시키면 그 인스턴스는 영원히 좀비가 된다.
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new("srv");

        await using InMemoryServerTransport first = new(hub, endPoint);
        await first.BindAsync(CreateEchoHandler());

        await using InMemoryServerTransport second = new(hub, endPoint);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await second.BindAsync(CreateEchoHandler()));

        Assert.Null(second.LocalEndPoint);

        // 자리가 비면 다시 바인드할 수 있어야 한다.
        await first.UnbindAsync();
        await second.BindAsync(CreateEchoHandler());
        Assert.Equal(endPoint, second.LocalEndPoint);
    }

    [Fact]
    public async Task Connect_ToUnknownEndPoint_Throws()
    {
        InMemoryTransportHub hub = new();
        await using InMemoryClientTransport client = new(hub);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ConnectAsync(new InMemoryEndPoint("nobody")));
    }

    [Fact]
    public async Task Connect_WithWrongEndPointType_Throws()
    {
        // 전송이 자기 주소 타입만 받는 것은 계약이다.
        InMemoryTransportHub hub = new();
        await using InMemoryClientTransport client = new(hub);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.ConnectAsync(new DnsEndPoint("localhost", 1234)));
    }

    [Fact]
    public async Task Unbind_StopsNewConnections_ButKeepsExistingOnesAlive()
    {
        // 3단 종료의 핵심. 여기가 깨지면 무중단 배포가 불가능하다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection existing = await harness.ConnectAsync();
        await harness.SendAsync(existing, EchoMessageId, [1]);
        await harness.ReceiveAsync(existing, TestTimeout.Token);

        await harness.Server.UnbindAsync();

        // 신규 연결은 거부된다.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.ConnectAsync());

        // 기존 커넥션은 계속 산다.
        await harness.SendAsync(existing, EchoMessageId, [2]);
        (_, byte[] echoed) = await harness.ReceiveAsync(existing, TestTimeout.Token);
        Assert.Equal<byte>([2], echoed);
    }

    [Fact]
    public async Task Unbind_WithoutBind_IsNoOp()
    {
        InMemoryTransportHub hub = new();
        await using InMemoryServerTransport server = new(hub, new InMemoryEndPoint("srv"));

        await server.UnbindAsync();

        Assert.Null(server.LocalEndPoint);
    }

    [Fact]
    public async Task Unbind_ReleasesTheNameForReuse()
    {
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new("srv");
        await using InMemoryServerTransport server = new(hub, endPoint);

        await server.BindAsync(CreateEchoHandler());
        Assert.True(hub.IsListening(endPoint.Name));

        await server.UnbindAsync();
        Assert.False(hub.IsListening(endPoint.Name));
    }

    [Fact]
    public async Task Stop_DrainsConnectionsThatCloseThemselves()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);
        await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(1, harness.ServerConnectionCount);

        // 클라이언트가 정상 종료하면 서버 읽기 루프가 스스로 끝난다.
        await connection.DisposeAsync();
        await harness.Server.StopAsync(TestTimeout.Token);

        Assert.Equal(0, harness.ServerConnectionCount);
    }

    [Fact]
    public async Task Stop_AbortsConnectionsWhenDrainTimeoutExpires()
    {
        // 상한 없는 드레인은 종료를 영원히 막고 배포 파이프라인을 멈춘다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);
        await harness.ReceiveAsync(connection, TestTimeout.Token);

        // 클라이언트가 붙어 있는 채로 짧은 제한 시간을 준다.
        using CancellationTokenSource shortDrain = new(TimeSpan.FromMilliseconds(100));
        await harness.Server.StopAsync(shortDrain.Token);

        Assert.Equal(0, harness.ServerConnectionCount);

        // 클라이언트 쪽 토큰은 직접 발화되지 않는다 — 서버가 끊은 것을
        // 스트림 종료로 관측할 뿐이다. 실제 소켓 전송에서도 같은 방식이다.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task Stop_UnbindsFirst()
    {
        // Unbind 를 먼저 하지 않으면 드레인 중 새 커넥션이 들어와 영원히 끝나지 않는다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await harness.Server.StopAsync(TestTimeout.Token);

        Assert.Equal(0, harness.ListenerCount);
    }

    [Fact]
    public async Task MaxConnections_RejectsBeyondTheLimit()
    {
        // 거부가 붕괴보다 낫다 (CLAUDE.md 9.6).
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))),
            transportOptions: new InMemoryTransportOptions { MaxConnections = 2 });

        await using IConnection first = await harness.ConnectAsync();
        await using IConnection second = await harness.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.ConnectAsync());

        Assert.Equal(2, harness.ServerConnectionCount);
    }

    [Fact]
    public async Task EndPointFeature_IsExposedOnBothSides()
    {
        // 전송별 선택 기능이 IConnection 을 부풀리지 않고 노출되는지.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        IConnectionEndPointFeature? feature = connection.Features.Get<IConnectionEndPointFeature>();

        Assert.NotNull(feature);
        Assert.Equal(harness.EndPoint, feature.RemoteEndPoint);
        Assert.Equal(harness.InMemoryClientEndPoint, feature.LocalEndPoint);
    }

    [Fact]
    public async Task UnknownFeature_ReturnsNull_WithoutThrowing()
    {
        // 없는 것은 정상이다. 예외였다면 상위 계층이 전송 종류를 알아야 한다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        Assert.Null(connection.Features.Get<IUnprovidedFeature>());
    }

    [Fact]
    public async Task Abort_IsIdempotent()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        connection.Abort(new ConnectionCloseInfo(CloseReason.ServerClosed));
        connection.Abort(new ConnectionCloseInfo(CloseReason.TransportError));

        Assert.True(connection.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_AfterAbort_DoesNotThrow()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        IConnection connection = await harness.ConnectAsync();
        connection.Abort(new ConnectionCloseInfo(CloseReason.ServerClosed));

        await connection.DisposeAsync();
    }

    private interface IUnprovidedFeature
    {
    }
}
