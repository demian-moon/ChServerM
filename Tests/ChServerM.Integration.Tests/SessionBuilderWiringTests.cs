using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Sessions;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Sessions;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 세션 축의 <b>빌더 배선</b>을 실제 서버·실제 소켓으로 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// 단위 테스트는 <c>SessionResumeDispatch</c> 를 직접 불러 검증한다. 여기서 확인하는 것은
/// <b>배선</b>이다 — <c>UseSessions</c> 하나로 예약 메시지가 실제로 라우팅되는가.
/// 앱이 <c>FrameworkMessageIds.SessionResume</c> 을 알 필요가 없어야 한다는 것이 요점이다.
/// </para>
/// </remarks>
public sealed class SessionBuilderWiringTests : IDisposable
{
    private static readonly FramingOptions Framing = new() { MaxPayloadLength = 4096 };

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    private static SessionId Id(int seed) => new(new ObjectId(seed));

    [Fact]
    public async Task Reserved_resume_message_is_routed_without_the_app_mapping_it()
    {
        // ★ 앱은 ID 40007 을 한 번도 언급하지 않는다 — 그것이 이 배선의 존재 이유다.
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService resume = new(store);

        SessionBinding created = (await resume.TryCreateAsync(Id(1), new byte[] { 9, 9, 9 }))!.Value;

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), new TcpTransportOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(Framing), new FixedHeaderFrameEncoder(Framing))
            .UseExecutionModel(new PartitionedExecutionModel(new PartitionedExecutionOptions { PartitionCount = 2 }))
            .UseSessions(resume)
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint endPoint = server.LocalEndPoint ?? throw new InvalidOperationException("바인드 주소가 없다.");

        await using TcpClientTransport client = new(new TcpTransportOptions());
        await using IConnection connection = await client.ConnectAsync(endPoint, _timeout.Token);

        // 재개 요청을 보낸다.
        byte[] payload = new byte[SessionHandshakeCodec.ResumeRequestSize];
        Span<byte> token = stackalloc byte[SessionHandshakeCodec.TokenLength];
        created.ResumeToken.CopyTo(token);
        SessionHandshakeCodec.WriteResumeRequest(payload, 1, token);

        await connection.WriteFrameAsync(
            new FixedHeaderFrameEncoder(Framing),
            FrameworkMessageIds.SessionResume,
            payload,
            FrameFlags.None,
            sequence: 0);

        (MessageId id, byte[] responsePayload) = await ReadFrameAsync(connection);

        Assert.Equal(FrameworkMessageIds.SessionResumed, id);

        byte[] rotated = new byte[SessionHandshakeCodec.TokenLength];
        Assert.True(SessionHandshakeCodec.TryReadResumeResponse(
            responsePayload, out SessionResumeStatus status, rotated));
        Assert.Equal(SessionResumeStatus.Resumed, status);

        // 회전됐으므로 옛 토큰은 더 이상 통하지 않는다.
        byte[] original = new byte[SessionHandshakeCodec.TokenLength];
        created.ResumeToken.CopyTo(original);
        Assert.NotEqual(original, rotated);
    }

    [Fact]
    public async Task Server_exposes_the_session_surface_only_when_wired()
    {
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService resume = new(store);

        await using ChServerMServer withSessions = Build(resume);
        Assert.NotNull(withSessions.Sessions);
        Assert.NotNull(withSessions.SessionDispatch);

        await using ChServerMServer without = Build(null);
        Assert.Null(without.Sessions);
        Assert.Null(without.SessionDispatch);
    }

    [Fact]
    public void Null_service_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerBuilder().UseSessions(null!));
    }

    private static ChServerMServer Build(SessionResumeService? resume)
    {
        ServerBuilder builder = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), new TcpTransportOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(Framing), new FixedHeaderFrameEncoder(Framing));

        if (resume is not null)
        {
            builder.UseSessions(resume);
        }

        return builder.Build();
    }

    private async Task<(MessageId Id, byte[] Payload)> ReadFrameAsync(IConnection connection)
    {
        FixedHeaderFrameDecoder decoder = new(Framing);

        while (true)
        {
            System.IO.Pipelines.ReadResult read = await connection.Input.ReadAsync(_timeout.Token);
            FrameDecodeResult result = decoder.Decode(read.Buffer);

            if (result.IsDecoded)
            {
                byte[] payload = result.Payload.ToArray();
                MessageId id = result.Envelope.MessageId;
                connection.Input.AdvanceTo(result.Consumed);
                return (id, payload);
            }

            if (result.IsFatal)
            {
                throw new InvalidOperationException($"프레임 디코드 실패: {result.Status}");
            }

            connection.Input.AdvanceTo(read.Buffer.Start, read.Buffer.End);

            if (read.IsCompleted)
            {
                throw new InvalidOperationException("응답 전에 연결이 끊겼다.");
            }
        }
    }
}
