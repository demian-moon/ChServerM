using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// <b>Phase 1 의 핵심 합격 기준을 검증한다</b> — 전송·프레이밍·디스패치·핸들러를
/// 인터페이스로만 엮은 파이프라인이 실제로 요청→응답을 왕복시키는가.
/// </summary>
/// <remarks>
/// 여기까지 통과하면 그동안 그은 추상화가 <b>가설이 아니라 동작하는 계약</b>이 된다.
/// </remarks>
public sealed class EchoPipelineTests
{
    private const ushort EchoMessageId = 100;
    private const ushort UnknownMessageId = 999;

    /// <summary>받은 페이로드를 그대로 돌려보내는 핸들러.</summary>
    /// <remarks>
    /// <b>이 핸들러는 전송도 프레이밍도 알지 못한다.</b> 인코더와 커넥션만 받는다.
    /// 그래서 TCP 로 바꿔도 이 코드는 바뀌지 않는다.
    /// </remarks>
    private static MessageDelegate Echo(IFrameEncoder encoder) => async context =>
    {
        // 페이로드를 그대로 흘려보낸다 — ToArray() 로 평탄화하지 않는다.
        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            encoder,
            context.Header.MessageId,
            context.Payload,
            sequence: context.Header.Sequence,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    };

    [Fact]
    public async Task RequestResponse_RoundTrips()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new Framing.FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();
        byte[] payload = Encoding.UTF8.GetBytes("안녕 ChServerM");

        await harness.SendAsync(connection, EchoMessageId, payload);
        (FrameHeader header, byte[] echoed) = await harness.ReceiveAsync(connection);

        Assert.Equal(new MessageId(EchoMessageId), header.MessageId);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task EmptyPayload_RoundTrips()
    {
        // 하트비트가 이 형태다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, EchoMessageId, []);
        (FrameHeader header, byte[] echoed) = await harness.ReceiveAsync(connection);

        Assert.Equal(0, header.PayloadLength);
        Assert.Empty(echoed);
    }

    [Fact]
    public async Task ManyFramesInSequence_AllRoundTrip()
    {
        // 한 커넥션에 프레임이 연달아 들어와도 경계가 유지되는지 — 읽기 루프의 내부 루프 검증.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        const int FrameCount = 200;
        for (int i = 0; i < FrameCount; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"frame-{i}");
            await harness.SendAsync(connection, EchoMessageId, payload);

            (_, byte[] echoed) = await harness.ReceiveAsync(connection);
            Assert.Equal(payload, echoed);
        }
    }

    [Fact]
    public async Task PipelinedFrames_PreserveOrder()
    {
        // 응답을 기다리지 않고 몰아서 보낸다. 순서가 뒤바뀌면 상태가 깨진다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        const int FrameCount = 50;
        for (int i = 0; i < FrameCount; i++)
        {
            await harness.SendAsync(connection, EchoMessageId, BitConverter.GetBytes(i));
        }

        for (int i = 0; i < FrameCount; i++)
        {
            (_, byte[] echoed) = await harness.ReceiveAsync(connection);
            Assert.Equal(i, BitConverter.ToInt32(echoed));
        }
    }

    [Fact]
    public async Task LargePayload_RoundTrips()
    {
        // 페이로드가 파이프의 세그먼트 크기를 넘으면 수신 측에서 여러 세그먼트가 된다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(64 * 1024))),
            maxPayloadLength: 64 * 1024);

        await using IConnection connection = await harness.ConnectAsync();

        byte[] payload = new byte[40_000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await harness.SendAsync(connection, EchoMessageId, payload);
        (_, byte[] echoed) = await harness.ReceiveAsync(connection);

        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task MultipleConnections_AreIndependent()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection first = await harness.ConnectAsync();
        await using IConnection second = await harness.ConnectAsync();

        await harness.SendAsync(first, EchoMessageId, "첫번째"u8);
        await harness.SendAsync(second, EchoMessageId, "두번째"u8);

        (_, byte[] firstEcho) = await harness.ReceiveAsync(first);
        (_, byte[] secondEcho) = await harness.ReceiveAsync(second);

        Assert.Equal("첫번째"u8.ToArray(), firstEcho);
        Assert.Equal("두번째"u8.ToArray(), secondEcho);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task ConcurrentConnections_AllRoundTrip()
    {
        // 커넥션끼리 상태를 공유하지 않는지 — 공유하면 여기서 섞인다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        const int ConnectionCount = 32;
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
                    (_, byte[] echoed) = await harness.ReceiveAsync(connection);
                    Assert.Equal(payload, echoed);
                }
            });
        }

        await Task.WhenAll(clients);
    }

    [Fact]
    public async Task UnknownMessageId_DoesNotCloseConnectionByDefault()
    {
        // 기본값은 관대하다 — 구버전 클라이언트가 모르는 메시지를 보내는 것은 흔한 일이고,
        // 그때마다 끊으면 롤링 배포가 불가능하다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, UnknownMessageId, [1, 2, 3]);
        await harness.SendAsync(connection, EchoMessageId, [4, 5, 6]);

        // 모르는 메시지 뒤에 온 정상 메시지가 처리되어야 한다.
        (_, byte[] echoed) = await harness.ReceiveAsync(connection);
        Assert.Equal<byte>([4, 5, 6], echoed);
    }

    [Fact]
    public async Task UnknownMessageId_ClosesConnection_WhenConfigured()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))),
            connectionOptions: new FramedConnectionOptions { CloseOnHandlerNotFound = true });

        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, UnknownMessageId, [1, 2, 3]);

        // 서버가 닫으면 클라이언트 쪽 읽기는 프레임 없이 완료된다.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task HandlerException_DoesNotKillTheConnection()
    {
        // 애플리케이션 버그 하나로 멀쩡한 후속 메시지까지 잃으면 장애가 증폭된다.
        const ushort FaultingMessageId = 200;

        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            .MapRaw(new MessageId(FaultingMessageId), _ => throw new InvalidOperationException("의도적 실패"))
            .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, FaultingMessageId, [9]);
        await harness.SendAsync(connection, EchoMessageId, [7, 7, 7]);

        (_, byte[] echoed) = await harness.ReceiveAsync(connection);
        Assert.Equal<byte>([7, 7, 7], echoed);
    }

    [Fact]
    public async Task OversizedFrame_IsRejectedByTheDecoder()
    {
        // 상한을 넘는 프레임은 페이로드를 기다리지 않고 즉시 거부되어야 한다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(256))),
            maxPayloadLength: 256);

        await using IConnection connection = await harness.ConnectAsync();

        // 서버 상한(256)을 넘는 길이를 선언하는 헤더를 직접 만들어 보낸다.
        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(EchoMessageId), 1024));
        await connection.Output.WriteAsync(header, TestTimeout.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }
}

/// <summary>테스트가 영원히 매달리지 않게 하는 공용 제한 시간.</summary>
/// <remarks>
/// 교착을 <b>실패로</b> 바꾼다. 제한 시간이 없으면 CI 가 멈추고, 원인 파악이 훨씬 어려워진다.
/// </remarks>
internal static class TestTimeout
{
    /// <summary>기본 제한 시간. 인메모리 전송에는 넉넉하다.</summary>
    public static TimeSpan Duration => TimeSpan.FromSeconds(10);

    /// <summary>제한 시간이 걸린 새 토큰.</summary>
    public static CancellationToken Token => new CancellationTokenSource(Duration).Token;
}
