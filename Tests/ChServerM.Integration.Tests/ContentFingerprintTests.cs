using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Content;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 콘텐츠 지문 게이트의 종단 검증 (ADR-0044).
/// </summary>
/// <remarks>
/// <para>고정하는 것:</para>
/// <list type="bullet">
///   <item><description>일치 경로 — 게이트를 켜도 같은 핸들러가 그대로 동작한다</description></item>
///   <item><description>불일치 경로 — <see cref="ContentFingerprintMismatchException"/> 이 나오고,
///   <b>버전 예외와 구분된다</b>(요구되는 조치가 다르기 때문이다)</description></item>
///   <item><description>실패 격리 — 거부당한 클라이언트 뒤에도 서버가 다음 접속을 정상 수용한다</description></item>
///   <item><description>조립 가드 — 협상 없이 게이트를 켜면 <see cref="ServerBuilder.Build"/> 가 실패한다</description></item>
///   <item><description>비대칭 조립 — 한쪽만 켜면 접속이 실패한다(양쪽 스위치라는 계약)</description></item>
/// </list>
/// </remarks>
public sealed class ContentFingerprintTests : IDisposable
{
    private static readonly MessageId EchoId = new(100);
    private static readonly ContentFingerprint ServerContent = new(0xAAAA_BBBB_CCCC_DDDD, 0x1111_2222_3333_4444);
    private static readonly ContentFingerprint StaleContent = new(0xAAAA_BBBB_CCCC_DDDD, 0x1111_2222_3333_4445);

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task MatchingFingerprint_connectsAndEchoes(TransportKind kind)
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        (IServerTransport serverTransport, IClientTransport clientTransport, EndPoint? known) =
            CreateTransports(kind, "cf-ok");

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .RequireContentFingerprint(ServerContent)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId,
                    context.Payload.ToArray(), FrameFlags.None, context.Envelope.Sequence,
                    context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = known ?? server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 종단이 없다.");

        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(clientTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .SendContentFingerprint(ServerContent)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(target, _timeout.Token);

        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, EchoId, new byte[] { 7 },
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        // 게이트가 프레이밍 앞에 끼어들어도 바이트 경계가 어긋나지 않는다는 증거다 —
        // 지문 프레임까지만 소비하고 나머지를 넘겼다는 뜻.
        Assert.Equal([7], await response.Task.WaitAsync(_timeout.Token));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task StaleFingerprint_isRejectedWithADistinctException_andServerSurvives(TransportKind kind)
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        InMemoryTransportHub? hub = kind == TransportKind.InMemory ? new InMemoryTransportHub() : null;
        InMemoryTransportOptions inMemoryOptions = new();
        InMemoryEndPoint? inMemoryEndPoint =
            hub is null ? null : new InMemoryEndPoint($"cf-reject-{Guid.NewGuid():N}");
        TcpTransportOptions tcpOptions = new();

        IServerTransport serverTransport = hub is null
            ? new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions)
            : new InMemoryServerTransport(hub, inMemoryEndPoint!, inMemoryOptions);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .RequireContentFingerprint(ServerContent)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId,
                    context.Payload.ToArray(), FrameFlags.None, context.Envelope.Sequence,
                    context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = (EndPoint?)inMemoryEndPoint ?? server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 종단이 없다.");

        await using (ChServerMClient stale = new ClientBuilder()
            .UseTransport(NewClientTransport(hub, inMemoryOptions, tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .SendContentFingerprint(StaleContent)
            .Build())
        {
            // ⚠ 여기서 예외 **타입**까지 못 박지 않는다. 거부 통지는 설계상 최선 노력이고
            // (거부 경로에서 상대를 기다리면 그것이 곧 공격 표면이다), 서버는 플러시 직후
            // 커넥션을 끊는다. TCP 는 그 순간 RST 로 버퍼를 버릴 수 있어 **부하가 높으면
            // 사유가 유실**된다 — 실제로 전 스위트 병렬 실행 중 한 번 그렇게 됐다.
            // 전송과 무관하게 확실한 것은 "연결이 수립되지 않는다" 이고, 사유가 제대로
            // 전달되는지는 아래 InMemory 전용 테스트가 결정적으로 고정한다.
            await Assert.ThrowsAnyAsync<Exception>(
                async () => await stale.ConnectAsync(target, _timeout.Token));
        }

        // 실패 격리 — 거부 하나가 서버를 망가뜨리지 않는다.
        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMClient good = new ClientBuilder()
            .UseTransport(NewClientTransport(hub, inMemoryOptions, tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .SendContentFingerprint(ServerContent)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await good.ConnectAsync(target, _timeout.Token);
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, good.Encoder, EchoId, new byte[] { 9 },
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        Assert.Equal([9], await response.Task.WaitAsync(_timeout.Token));
    }

    [Fact]
    public async Task RejectionReason_reachesTheClientAsADistinctException()
    {
        // ⭐ 이 테스트가 고정하는 계약: 지문 불일치는 **버전 거부와 구분되는 예외**로 온다.
        // 요구되는 조치가 다르기 때문이다 — 버전은 "실행 파일을 갱신하라", 지문은
        // "데이터를 갱신하라". 호출자가 서로 다른 안내를 띄울 수 있어야 한다.
        //
        // InMemory 전송을 쓰는 이유: 거부 통지는 최선 노력이라 TCP 에서는 RST 가 통지를
        // 앞지를 수 있다. 여기서 검증하려는 것은 **프로토콜 계약**이지 전송의 타이밍이 아니다.
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        InMemoryTransportHub hub = new();
        InMemoryTransportOptions options = new();
        InMemoryEndPoint endPoint = new($"cf-reason-{Guid.NewGuid():N}");

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .RequireContentFingerprint(ServerContent)
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient stale = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .SendContentFingerprint(StaleContent)
            .Build();

        ContentFingerprintMismatchException error =
            await Assert.ThrowsAsync<ContentFingerprintMismatchException>(
                async () => await stale.ConnectAsync(endPoint, _timeout.Token));

        Assert.IsNotType<VersionNegotiationException>(error);
        Assert.Equal(StaleContent, error.Offered);
    }

    [Fact]
    public async Task ClientWithoutTheGate_failsAgainstAGatedServer()
    {
        // 게이트는 **양쪽 스위치**다. 클라이언트가 지문을 보내지 않으면 서버는 지문을
        // 기다리다 제한 시간에 걸리고, 커넥션은 수립되지 않는다.
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        InMemoryTransportHub hub = new();
        InMemoryTransportOptions options = new();
        InMemoryEndPoint endPoint = new($"cf-asym-{Guid.NewGuid():N}");

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions
            {
                HandshakeTimeout = TimeSpan.FromMilliseconds(300),
            })
            .RequireContentFingerprint(ServerContent)
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .Build();

        // 협상 자체는 통과하므로 ConnectAsync 는 성공한다. 깨지는 것은 그 뒤이며,
        // 서버가 제한 시간에 커넥션을 끊으면 클라이언트의 읽기 루프가 끝난다 —
        // 취소 토큰이 아니라 **읽기 루프의 완료**가 이 실패의 관측 가능한 형태다.
        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        await session.Completion.WaitAsync(_timeout.Token);
    }

    [Fact]
    public void Server_gateWithoutNegotiation_failsAtBuild()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        InMemoryTransportHub hub = new();
        InMemoryTransportOptions options = new();

        ServerBuilder builder = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, new InMemoryEndPoint("cf-guard"), options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .RequireContentFingerprint(ServerContent);

        // 조립 시점 실패가 런타임 디버깅보다 싸다.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains(nameof(ServerBuilder.UseVersionNegotiation), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_gateWithoutNegotiation_failsAtBuild()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };

        ClientBuilder builder = new ClientBuilder()
            .UseTransport(new TcpClientTransport(new TcpTransportOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .SendContentFingerprint(ServerContent);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains(nameof(ClientBuilder.UseVersionNegotiation), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsetFingerprint_isRejectedAtConfiguration()
    {
        // 게이트를 켜고 지문을 넣지 않으면 모든 접속이 거부된다 — 조립 시점에 끊는다.
        Assert.Throws<ArgumentException>(
            () => new ServerBuilder().RequireContentFingerprint(ContentFingerprint.None));
        Assert.Throws<ArgumentException>(
            () => new ClientBuilder().SendContentFingerprint(ContentFingerprint.None));
    }

    private static IClientTransport NewClientTransport(
        InMemoryTransportHub? hub, InMemoryTransportOptions inMemoryOptions, TcpTransportOptions tcpOptions) =>
        hub is null
            ? new TcpClientTransport(tcpOptions)
            : new InMemoryClientTransport(hub, null, inMemoryOptions);

    private static (IServerTransport Server, IClientTransport Client, EndPoint? KnownEndPoint) CreateTransports(
        TransportKind kind, string name)
    {
        if (kind == TransportKind.InMemory)
        {
            InMemoryTransportOptions options = new();
            InMemoryTransportHub hub = new();
            InMemoryEndPoint endPoint = new($"{name}-{Guid.NewGuid():N}");
            return (
                new InMemoryServerTransport(hub, endPoint, options),
                new InMemoryClientTransport(hub, null, options),
                endPoint);
        }

        TcpTransportOptions tcpOptions = new();
        return (
            new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions),
            new TcpClientTransport(tcpOptions),
            null);
    }
}
