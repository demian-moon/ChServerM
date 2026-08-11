using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Handshake;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Security.Tls;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 버전 협상 핸드셰이크의 종단 검증 (ADR-0017 결정 3, THREAT-MODEL R-1~R-5).
/// </summary>
/// <remarks>
/// <para>고정하는 것:</para>
/// <list type="bullet">
///   <item><description>합의 경로 — 같은 에코 핸들러가 협상 유무·전송 종류와 무관하게 동작하고,
///   양쪽 커넥션에서 <see cref="IProtocolVersionFeature"/> 로 결과가 조회된다</description></item>
///   <item><description>거부 경로(R-3) — 교집합이 없으면 서버 지원 구간이 실린 거부가 오고,
///   서버는 다음 커넥션을 정상 수용한다(실패 격리)</description></item>
///   <item><description>협상 없는 커넥션 — 형식 위반·무응답은 커넥션 종료로 드러난다(T-16)</description></item>
///   <item><description>동결 레이아웃 — Core 의 <see cref="VersionHandshakeCodec"/> 와
///   Framing 의 <see cref="FrameHeaderCodec"/> 가 같은 헤더를 말한다(의도적 중복의 교차 검증)</description></item>
/// </list>
/// </remarks>
public sealed class VersionNegotiationTests : IDisposable
{
    private static readonly MessageId EchoId = new(100);

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Negotiation_completes_and_echo_roundtrips(TransportKind kind)
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        (IServerTransport serverTransport, IClientTransport clientTransport, EndPoint? knownEndPoint) =
            CreateTransports(kind, "vn-ok");

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                // 서버 측 협상 결과를 페이로드로 되돌린다 — 피처 등록까지 종단으로 검증된다.
                IProtocolVersionFeature? feature =
                    context.Connection.Features.Get<IProtocolVersionFeature>();
                byte[] reply = [(byte)(feature?.NegotiatedVersion ?? 0)];
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, reply,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = knownEndPoint ?? server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 종단이 없다.");

        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(clientTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(target, _timeout.Token);

        // 클라이언트 커넥션에도 협상 결과가 피처로 남는다.
        IProtocolVersionFeature? clientFeature = session.Connection.Features.Get<IProtocolVersionFeature>();
        Assert.NotNull(clientFeature);
        Assert.Equal((ushort)1, clientFeature.NegotiatedVersion);

        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, EchoId, new byte[] { 42 },
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        // 서버 핸들러가 되돌린 값 = 서버 커넥션의 협상 버전.
        byte[] echoed = await response.Task.WaitAsync(_timeout.Token);
        Assert.Equal([1], echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Disjoint_ranges_are_rejected_with_server_range_and_server_survives(TransportKind kind)
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        // 거부당한 클라이언트가 전송을 정리한 뒤에도 같은 서버에 새 클라이언트를 붙여야
        // 하므로(실패 격리 검증), 전송을 튜플이 아니라 팩토리로 만든다.
        InMemoryTransportHub? hub = kind == TransportKind.InMemory ? new InMemoryTransportHub() : null;
        InMemoryTransportOptions inMemoryOptions = new();
        InMemoryEndPoint? inMemoryEndPoint =
            kind == TransportKind.InMemory ? new InMemoryEndPoint($"vn-reject-{Guid.NewGuid():N}") : null;
        TcpTransportOptions tcpOptions = new();

        IServerTransport serverTransport = kind == TransportKind.InMemory
            ? new InMemoryServerTransport(hub!, inMemoryEndPoint!, inMemoryOptions)
            : new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions);
        Func<IClientTransport> clientTransportFactory = kind == TransportKind.InMemory
            ? () => new InMemoryClientTransport(hub!, null, inMemoryOptions)
            : () => new TcpClientTransport(tcpOptions);
        EndPoint? knownEndPoint = inMemoryEndPoint;

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseVersionNegotiation(new VersionNegotiationOptions()) // 서버 [1,1]
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = knownEndPoint ?? server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 종단이 없다.");

        // 1. 미래 버전만 아는 클라이언트 [2,9] — 교집합 없음 → 거부에 서버 구간이 실린다(R-3).
        await using (ChServerMClient futureClient = new ClientBuilder()
            .UseTransport(clientTransportFactory())
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions
            {
                SupportedVersions = new ProtocolVersionRange(2, 9),
            })
            .Build())
        {
            VersionNegotiationException rejected = await Assert.ThrowsAsync<VersionNegotiationException>(
                async () => await futureClient.ConnectAsync(target, _timeout.Token));

            // 이 단언은 한때 CI 에서만 간헐 실패했다(2026-08-10~11, 3회): 서버가 거부
            // 프레임 flush 직후 Abort 를 불러, 송신 펌프가 소켓에 쓰기 전에 프레임이
            // 파괴될 수 있었다. 거부 경로가 정상 종료(FIN)로 바뀌어 이제 전달이 보장된다 —
            // 여기서 null 이 다시 보이면 그 회귀다(VersionNegotiatingConnectionHandler 참조).
            Assert.Equal(new ProtocolVersionRange(1, 1), rejected.ServerSupportedVersions);
        }

        // 2. 거부 이후에도 서버는 정상 클라이언트를 수용해야 한다 — 실패 격리.
        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(clientTransportFactory())
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(target, _timeout.Token);
        byte[] payload = [7, 7, 7];
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, EchoId, payload,
            FrameFlags.None, sequence: 2, session.Connection.ConnectionClosed);

        Assert.Equal(payload, await response.Task.WaitAsync(_timeout.Token));
    }

    [Fact]
    public async Task NonHello_first_frame_closes_connection_and_server_survives()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder encoder = new(framing);
        TcpTransportOptions tcpOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, encoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = server.LocalEndPoint!;

        // 1. 협상을 모르는 클라이언트가 앱 프레임부터 보낸다 — 응답 없이 커넥션이 닫혀야 한다.
        //    (ID 100 은 ClientHello 형식 위반 — 인증 전 화이트리스트(T-19)와 같은 "기본 거부" 원칙)
        await using (TcpClientTransport plainTransport = new(tcpOptions))
        {
            IConnection plain = await plainTransport.ConnectAsync(target, _timeout.Token);
            await FrameWriter.WriteFrameAsync(
                plain.Output, encoder, EchoId, new byte[] { 1, 2, 3 },
                FrameFlags.None, sequence: 0, plain.ConnectionClosed);

            try
            {
                System.IO.Pipelines.ReadResult read = await plain.Input.ReadAsync(_timeout.Token);
                Assert.True(read.IsCompleted);
                Assert.True(read.Buffer.IsEmpty);
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                // abortive 종료(RST)로 드러나는 플랫폼도 있다 — 역시 합격.
            }

            await plain.DisposeAsync();
        }

        // 2. 실패 이후에도 정상 협상 클라이언트는 통과해야 한다.
        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new TcpClientTransport(tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(target, _timeout.Token);
        byte[] payload = [9, 9];
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, EchoId, payload,
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        Assert.Equal(payload, await response.Task.WaitAsync(_timeout.Token));
    }

    [Fact]
    public async Task Silent_client_is_disconnected_after_handshake_timeout()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        TcpTransportOptions tcpOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseVersionNegotiation(new VersionNegotiationOptions
            {
                HandshakeTimeout = TimeSpan.FromMilliseconds(200),
            })
            .Build();

        await server.StartAsync(_timeout.Token);

        // 협상 프레임을 보내지 않고 매달린다 — 슬로우로리스 변형(T-16). 서버가 끊어야 한다.
        await using TcpClientTransport silentTransport = new(tcpOptions);
        IConnection silent = await silentTransport.ConnectAsync(server.LocalEndPoint!, _timeout.Token);

        try
        {
            System.IO.Pipelines.ReadResult read = await silent.Input.ReadAsync(_timeout.Token);
            Assert.True(read.IsCompleted);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            // abortive 종료(RST)도 "서버가 끊었다"는 증거다 — 합격.
        }

        await silent.DisposeAsync();
    }

    [Fact]
    public async Task Negotiation_runs_inside_tls_channel()
    {
        // ADR-0017 결정 3: 협상은 보안 채널 확립 후다 — 조립 순서(보안 바깥·협상 안)를 종단으로 확인.
        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        InMemoryTransportOptions options = new();
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"vn-tls-{Guid.NewGuid():N}");

        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                ServerCertificate = certificate,
            }))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidation = (_, received, _, _) =>
                    received is X509Certificate2 cert && cert.Thumbprint == certificate.Thumbprint,
            }))
            .UseVersionNegotiation(new VersionNegotiationOptions())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        Assert.Equal(
            (ushort)1,
            session.Connection.Features.Get<IProtocolVersionFeature>()?.NegotiatedVersion);

        byte[] payload = [11, 22, 33];
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, EchoId, payload,
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        Assert.Equal(payload, await response.Task.WaitAsync(_timeout.Token));
    }

    [Fact]
    public void Frozen_layout_matches_fixed_header_codec()
    {
        // Core 코덱은 헤더 레이아웃을 의도적으로 중복한다(역방향 의존 회피).
        // 두 정의가 같은 와이어를 말하는지 여기서 교차 검증한다 — 이 테스트가 어긋나면
        // 동결 위반이다. Framing 쪽을 고치지 말고 무엇이 레이아웃을 건드렸는지 찾는다.
        byte[] hello = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(hello, new ProtocolVersionRange(1, 3));

        FrameDecodeStatus status = FrameHeaderCodec.TryRead(
            hello, maxPayloadLength: 4096, acceptedVersion: VersionHandshakeCodec.BootstrapHeaderVersion,
            out FrameHeader header);

        Assert.Equal(FrameDecodeStatus.Decoded, status);
        Assert.Equal(FrameworkMessageIds.ClientHello, header.MessageId);
        Assert.Equal(VersionHandshakeCodec.ClientHelloPayloadSize, header.PayloadLength);
        Assert.Equal(FrameFlags.None, header.Flags);
        Assert.Equal(0u, header.Sequence);
        Assert.Equal(VersionHandshakeCodec.HeaderSize, FrameHeader.Size);

        byte[] serverHello = new byte[VersionHandshakeCodec.ServerHelloFrameSize];
        VersionHandshakeCodec.WriteServerHello(serverHello, 1);
        Assert.Equal(FrameDecodeStatus.Decoded, FrameHeaderCodec.TryRead(serverHello, 4096, 1, out FrameHeader sh));
        Assert.Equal(FrameworkMessageIds.ServerHello, sh.MessageId);

        byte[] rejection = new byte[VersionHandshakeCodec.RejectionFrameSize];
        VersionHandshakeCodec.WriteRejection(rejection, new ProtocolVersionRange(1, 1));
        Assert.Equal(FrameDecodeStatus.Decoded, FrameHeaderCodec.TryRead(rejection, 4096, 1, out FrameHeader rj));
        Assert.Equal(FrameworkMessageIds.ConnectionRejected, rj.MessageId);
    }

    /// <summary>전송 종류에 따라 달라지는 유일한 지점 — TestHarness 와 같은 규약.</summary>
    private static (IServerTransport Server, IClientTransport Client, EndPoint? KnownEndPoint) CreateTransports(
        TransportKind kind, string name)
    {
        if (kind == TransportKind.InMemory)
        {
            InMemoryTransportOptions options = new();
            InMemoryTransportHub hub = new();
            InMemoryEndPoint endPoint = new($"{name}-{Guid.NewGuid():N}");
            return (new InMemoryServerTransport(hub, endPoint, options), new InMemoryClientTransport(hub, null, options), endPoint);
        }

        TcpTransportOptions tcpOptions = new();
        return (new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions), new TcpClientTransport(tcpOptions), null);
    }

    /// <summary>테스트 전용 자가서명 인증서 — <see cref="TlsEndToEndTests"/> 와 같은 방식.</summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
