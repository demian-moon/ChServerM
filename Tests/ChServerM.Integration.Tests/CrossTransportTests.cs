using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// <b>ADR-0004 의 합격 기준을 직접 검증한다 — 같은 핸들러 코드가 두 전송에서 동작하는가.</b>
/// </summary>
/// <remarks>
/// <para>
/// 모든 테스트가 <c>[Theory]</c> 로 <see cref="TransportKind.InMemory"/> 와
/// <see cref="TransportKind.Tcp"/> 양쪽을 돈다. 핸들러·프레이밍·디스패치 코드는
/// 두 경우에 <b>완전히 동일</b>하다. 한쪽만 통과하는 항목이 생기면 그것은
/// 추상화가 전송 세부를 흘리고 있다는 신호다.
/// </para>
/// <para>
/// 이것이 통과하면 "TCP 커넥션 서버로도, 무상태 웹서버로도 조립할 수 있는 프레임워크"라는
/// 목표의 첫 증거가 확보된다.
/// </para>
/// </remarks>
public sealed class CrossTransportTests
{
    private const ushort EchoMessageId = 100;
    private const ushort GreetMessageId = 300;
    private const ushort UnknownMessageId = 999;

    /// <summary>받은 페이로드를 그대로 돌려보내는 핸들러. 전송을 알지 못한다.</summary>
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

    /// <summary>타입 있는 핸들러. 직렬화 포맷도 전송도 알지 못한다.</summary>
    private sealed class GreetHandler(IFrameEncoder encoder) : IMessageHandler<string>
    {
        public async ValueTask HandleAsync(MessageContext context, string message)
        {
            byte[] reply = Encoding.UTF8.GetBytes($"안녕, {message}");
            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Header.MessageId,
                reply,
                FrameFlags.None,
                context.Header.Sequence,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<TestHarness> StartEchoAsync(
        TransportKind kind,
        FramedConnectionOptions? options = null,
        int maxPayloadLength = 4096)
    {
        FixedHeaderFrameEncoder encoder = new(maxPayloadLength);

        return TestHarness.StartAsync(
            builder => builder
                .MapRaw(new MessageId(EchoMessageId), Echo(encoder))
                .Map(new MessageId(GreetMessageId), Utf8StringSerializer.Instance, new GreetHandler(encoder)),
            kind,
            connectionOptions: options,
            maxPayloadLength: maxPayloadLength);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task RequestResponse_RoundTrips(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        byte[] payload = Encoding.UTF8.GetBytes("안녕 ChServerM");
        await harness.SendAsync(connection, EchoMessageId, payload);

        (FrameHeader header, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(new MessageId(EchoMessageId), header.MessageId);
        Assert.Equal(payload, echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task TypedHandler_Works(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, GreetMessageId, "세계"u8);
        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal("안녕, 세계", Encoding.UTF8.GetString(reply));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task EmptyPayload_RoundTrips(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, EchoMessageId, []);
        (FrameHeader header, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(0, header.PayloadLength);
        Assert.Empty(echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task ManyFrames_RoundTripInOrder(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        const int FrameCount = 100;
        for (int i = 0; i < FrameCount; i++)
        {
            await harness.SendAsync(connection, EchoMessageId, BitConverter.GetBytes(i));
            (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);
            Assert.Equal(i, BitConverter.ToInt32(echoed));
        }
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task PipelinedFrames_PreserveOrder(TransportKind kind)
    {
        // 응답을 기다리지 않고 몰아서 보낸다. TCP 에서는 한 번의 read 에 프레임 여러 개가
        // 뭉쳐 들어오므로, 읽기 루프의 내부 루프가 실제로 검증된다.
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        const int FrameCount = 200;
        for (int i = 0; i < FrameCount; i++)
        {
            await harness.SendAsync(connection, EchoMessageId, BitConverter.GetBytes(i));
        }

        for (int i = 0; i < FrameCount; i++)
        {
            (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);
            Assert.Equal(i, BitConverter.ToInt32(echoed));
        }
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task LargePayload_SpanningManySegments_RoundTrips(TransportKind kind)
    {
        // TCP 에서 40KB 는 반드시 여러 번의 read 로 쪼개져 도착한다.
        // 프레임 재조립과 세그먼트 경계 처리가 여기서 실제로 걸린다.
        await using TestHarness harness = await StartEchoAsync(kind, maxPayloadLength: 256 * 1024);
        await using IConnection connection = await harness.ConnectAsync();

        byte[] payload = new byte[200_000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await harness.SendAsync(connection, EchoMessageId, payload);
        (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(payload, echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task ConcurrentConnections_AllRoundTrip(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);

        const int ConnectionCount = 16;
        Task[] clients = new Task[ConnectionCount];

        for (int i = 0; i < ConnectionCount; i++)
        {
            int index = i;
            clients[i] = Task.Run(async () =>
            {
                await using IConnection connection = await harness.ConnectAsync();
                byte[] payload = Encoding.UTF8.GetBytes($"client-{index}");

                for (int round = 0; round < 20; round++)
                {
                    await harness.SendAsync(connection, EchoMessageId, payload);
                    (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);
                    Assert.Equal(payload, echoed);
                }
            });
        }

        await Task.WhenAll(clients);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task UnknownMessageId_DoesNotCloseConnectionByDefault(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, UnknownMessageId, [1, 2, 3]);
        await harness.SendAsync(connection, EchoMessageId, [4, 5, 6]);

        (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);
        Assert.Equal<byte>([4, 5, 6], echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task UnknownMessageId_ClosesConnection_WhenConfigured(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(
            kind, new FramedConnectionOptions { CloseOnHandlerNotFound = true });

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, UnknownMessageId, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task EndPointFeature_IsAvailableOnBothTransports(TransportKind kind)
    {
        // 같은 기능 인터페이스를 두 전송이 각자 제공한다 — 이것이 Features 의 목적이다.
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection connection = await harness.ConnectAsync();

        IConnectionEndPointFeature? feature = connection.Features.Get<IConnectionEndPointFeature>();

        Assert.NotNull(feature);
        Assert.NotNull(feature.RemoteEndPoint);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Unbind_StopsNewConnections_ButKeepsExistingAlive(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);
        await using IConnection existing = await harness.ConnectAsync();

        await harness.SendAsync(existing, EchoMessageId, [1]);
        await harness.ReceiveAsync(existing, TestTimeout.Token);

        await harness.Server.UnbindAsync();

        // 신규 연결은 실패한다. 전송마다 예외 타입이 다르므로 종류만 확인한다.
        await Assert.ThrowsAnyAsync<Exception>(async () => await harness.ConnectAsync());

        // 기존 커넥션은 계속 산다 — 무중단 배포의 창.
        await harness.SendAsync(existing, EchoMessageId, [2]);
        (_, byte[] echoed) = await harness.ReceiveAsync(existing, TestTimeout.Token);
        Assert.Equal<byte>([2], echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Stop_DrainsClosedConnections(TransportKind kind)
    {
        await using TestHarness harness = await StartEchoAsync(kind);

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);
        await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(1, harness.ServerConnectionCount);

        await connection.DisposeAsync();
        await harness.Server.StopAsync(TestTimeout.Token);

        Assert.Equal(0, harness.ServerConnectionCount);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task AbruptClientDisconnect_DoesNotAffectOtherConnections(TransportKind kind)
    {
        // 한 클라이언트가 사고로 끊기는 것은 일상이다. 다른 커넥션이 영향받으면
        // 장애가 전파된다.
        await using TestHarness harness = await StartEchoAsync(kind);

        IConnection victim = await harness.ConnectAsync();
        await using IConnection survivor = await harness.ConnectAsync();

        await harness.SendAsync(victim, EchoMessageId, [1]);
        await harness.ReceiveAsync(victim, TestTimeout.Token);

        victim.Abort(new ConnectionCloseInfo(CloseReason.TransportError));

        await harness.SendAsync(survivor, EchoMessageId, [2]);
        (_, byte[] echoed) = await harness.ReceiveAsync(survivor, TestTimeout.Token);
        Assert.Equal<byte>([2], echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task OversizedFrameHeader_ClosesConnection(TransportKind kind)
    {
        // 상한을 넘는 길이를 선언하면 페이로드를 기다리지 않고 즉시 끊어야 한다.
        // 기다리면 그것이 곧 메모리 고갈 경로다.
        await using TestHarness harness = await StartEchoAsync(kind, maxPayloadLength: 256);
        await using IConnection connection = await harness.ConnectAsync();

        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(EchoMessageId), 1024));
        await connection.Output.WriteAsync(header, TestTimeout.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }
}

/// <summary>
/// TCP 전송에만 해당하는 검증.
/// </summary>
/// <remarks>
/// 소켓이 실제로 관여할 때만 의미가 있는 것들 — 포트 배정, IP 종단, 소켓 옵션.
/// 여기 있는 항목이 늘어난다면 그것은 <b>추상화가 부족하다는 신호</b>다.
/// </remarks>
public sealed class TcpTransportSpecificTests
{
    private const ushort EchoMessageId = 100;

    private static MessageDelegate Echo(IFrameEncoder encoder) => async context =>
    {
        await FrameWriter.WriteFrameAsync(
            context.Connection.Output, encoder, context.Header.MessageId, context.Payload,
            FrameFlags.None, context.Header.Sequence,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    };

    private static Task<TestHarness> StartAsync(TcpTransportOptions? options = null)
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        return TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(encoder)),
            TransportKind.Tcp,
            tcpOptions: options);
    }

    [Fact]
    public async Task Bind_WithPortZero_ReportsTheAssignedPort()
    {
        // 포트를 하드코딩하면 병렬 실행에서 충돌하고 CI 가 산발적으로 실패한다.
        await using TestHarness harness = await StartAsync();

        IPEndPoint endPoint = Assert.IsType<IPEndPoint>(harness.Server.LocalEndPoint);
        Assert.NotEqual(0, endPoint.Port);
        Assert.Equal(IPAddress.Loopback, endPoint.Address);
    }

    [Fact]
    public async Task RemoteEndPoint_IsAnIPEndPoint()
    {
        await using TestHarness harness = await StartAsync();
        await using IConnection connection = await harness.ConnectAsync();

        IConnectionEndPointFeature feature =
            Assert.IsAssignableFrom<IConnectionEndPointFeature>(
                connection.Features.Get<IConnectionEndPointFeature>());

        IPEndPoint remote = Assert.IsType<IPEndPoint>(feature.RemoteEndPoint);
        Assert.Equal(((IPEndPoint)harness.Server.LocalEndPoint!).Port, remote.Port);
    }

    [Fact]
    public async Task Connect_ToClosedPort_Throws()
    {
        // 듣고 있지 않은 포트를 찾기 위해 잠깐 열었다 닫는다.
        Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int freePort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Dispose();

        await using TcpClientTransport client = new();

        await Assert.ThrowsAsync<SocketException>(
            async () => await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, freePort)));
    }

    [Fact]
    public async Task MaxConnections_RejectsBeyondTheLimit()
    {
        // 거부가 붕괴보다 낫다. TCP 에서는 서버가 수락 직후 소켓을 닫으므로
        // 클라이언트는 연결은 되지만 곧 스트림 종료를 본다.
        await using TestHarness harness = await StartAsync(new TcpTransportOptions { MaxConnections = 1 });

        await using IConnection first = await harness.ConnectAsync();
        await harness.SendAsync(first, EchoMessageId, [1]);
        await harness.ReceiveAsync(first, TestTimeout.Token);

        await using IConnection second = await harness.ConnectAsync();
        await harness.SendAsync(second, EchoMessageId, [2]);

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.ReceiveAsync(second, TestTimeout.Token));

        // 상한 안의 커넥션은 멀쩡해야 한다.
        await harness.SendAsync(first, EchoMessageId, [3]);
        (_, byte[] echoed) = await harness.ReceiveAsync(first, TestTimeout.Token);
        Assert.Equal<byte>([3], echoed);
    }

    [Fact]
    public async Task WaitForDataBeforeAllocating_CanBeDisabled()
    {
        // 두 경로 모두 동작해야 한다. 최적화 경로만 테스트하면 폴백이 썩는다.
        await using TestHarness harness = await StartAsync(
            new TcpTransportOptions { WaitForDataBeforeAllocating = false });

        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, EchoMessageId, [1, 2, 3]);
        (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal<byte>([1, 2, 3], echoed);
    }

    [Fact]
    public async Task KeepAlive_CanBeEnabled_OnEveryPlatform()
    {
        // 레거시의 IOControlCode.KeepAliveValues 는 Windows 전용이라 리눅스에서 던졌다.
        // 이식 가능한 옵션만 쓰는지 확인한다 — CI 매트릭스가 두 OS 를 모두 돈다.
        await using TestHarness harness = await StartAsync(new TcpTransportOptions { EnableKeepAlive = true });
        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, EchoMessageId, [1]);
        (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal<byte>([1], echoed);
    }

    [Fact]
    public async Task Bind_ToOccupiedPort_Throws_AndLeavesTransportRebindable()
    {
        await using TestHarness occupied = await StartAsync();
        IPEndPoint endPoint = (IPEndPoint)occupied.Server.LocalEndPoint!;

        FixedHeaderFrameEncoder encoder = new(4096);
        Hosting.Dispatch.MessageDispatcherBuilder builder = new();
        builder.MapRaw(new MessageId(EchoMessageId), Echo(encoder));
        FramedConnectionHandler handler = new(new FixedHeaderFrameDecoder(4096), builder.Build());

        await using TcpServerTransport second = new(endPoint);

        await Assert.ThrowsAsync<SocketException>(async () => await second.BindAsync(handler));

        // 실패한 바인드가 상태를 오염시키면 이 인스턴스는 영원히 좀비가 된다.
        Assert.Null(second.LocalEndPoint);
    }
}
