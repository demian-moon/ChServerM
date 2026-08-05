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
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Security.Tls;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 전송 보안 축의 종단 검증 — <b>같은 에코 핸들러가 TLS 유무·전송 종류와 무관하게 동작한다</b>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0017 결정 2(파이프 데코레이터)의 합격 기준이 이 테스트다: 보안 축이 인메모리
/// 전송 위에서도 켜진다면 계약이 진짜 전송 중립이다. 핸들러·프레이밍 코드는 TLS 를 모른다.
/// </para>
/// <para>
/// 평문 클라이언트가 TLS 서버에 붙는 부정 경로도 고정한다 — 실패가 조용히 매달리지 않고
/// 커넥션 종료로 드러나야 하며(THREAT-MODEL T-07), 실패 후에도 서버는 다음 커넥션을
/// 정상 수용해야 한다.
/// </para>
/// </remarks>
public sealed class TlsEndToEndTests : IDisposable
{
    private static readonly MessageId EchoId = new(100);

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    private readonly X509Certificate2 _certificate = CreateSelfSignedCertificate();

    public void Dispose()
    {
        _certificate.Dispose();
        _timeout.Dispose();
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Same_echo_handler_roundtrips_over_tls(TransportKind kind)
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        (IServerTransport serverTransport, IClientTransport clientTransport, EndPoint? knownEndPoint) =
            CreateTransports(kind);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                ServerCertificate = _certificate,
            }))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, async context =>
            {
                // TLS 유무를 모르는 평범한 에코 핸들러 — 이 무지가 검증 대상이다.
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
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
            .UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidation = (_, certificate, _, _) =>
                    certificate is X509Certificate2 received && received.Thumbprint == _certificate.Thumbprint,
            }))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(EchoId, context =>
            {
                response.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(target, _timeout.Token);

        byte[] payload = [10, 20, 30, 40, 50];
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, EchoId, payload,
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        byte[] echoed = await response.Task.WaitAsync(_timeout.Token);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Plaintext_client_is_rejected_and_server_survives()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder encoder = new(framing);
        TcpTransportOptions tcpOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                ServerCertificate = _certificate,
            }))
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

        // 1. 평문 클라이언트 — 프레임 바이트가 TLS ClientHello 로 해석되다 실패해야 한다.
        await using (TcpClientTransport plainTransport = new(tcpOptions))
        {
            IConnection plain = await plainTransport.ConnectAsync(target, _timeout.Token);
            await FrameWriter.WriteFrameAsync(
                plain.Output, encoder, EchoId, new byte[] { 1, 2, 3 },
                FrameFlags.None, sequence: 0, plain.ConnectionClosed);

            // 서버가 커넥션을 닫는다 — 응답이 오는 것처럼 보이면 안 된다.
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

        // 2. 실패 이후에도 서버는 정상 TLS 클라이언트를 수용해야 한다 — 실패 격리.
        TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new TcpClientTransport(tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidation = (_, certificate, _, _) =>
                    certificate is X509Certificate2 received && received.Thumbprint == _certificate.Thumbprint,
            }))
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

    /// <summary>전송 종류에 따라 달라지는 유일한 지점 — TestHarness 와 같은 규약.</summary>
    private static (IServerTransport Server, IClientTransport Client, EndPoint? KnownEndPoint) CreateTransports(
        TransportKind kind)
    {
        if (kind == TransportKind.InMemory)
        {
            InMemoryTransportOptions options = new();
            InMemoryTransportHub hub = new();
            InMemoryEndPoint endPoint = new($"tls-e2e-{Guid.NewGuid():N}");
            return (new InMemoryServerTransport(hub, endPoint, options), new InMemoryClientTransport(hub, null, options), endPoint);
        }

        TcpTransportOptions tcpOptions = new();
        return (new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions), new TcpClientTransport(tcpOptions), null);
    }

    /// <summary>테스트 전용 자가서명 인증서. Schannel 이 ephemeral 키를 못 쓰므로 PFX 왕복으로 로드한다.</summary>
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
