using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Security;
using Xunit;

namespace ChServerM.Security.Tls.Tests;

/// <summary>
/// <see cref="TlsTransportSecurity"/>의 실동 검증 — 소켓 없이 인메모리 파이프 쌍으로
/// 서버·클라이언트 핸드셰이크를 맞물린다 (ADR-0017 결정 2가 노린 테스트 형태).
/// </summary>
public sealed class TlsTransportSecurityTests : IDisposable
{
    /// <summary>개별 테스트가 걸리는 것을 막는 안전망. 정상 경로는 수 ms 에 끝난다.</summary>
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private readonly X509Certificate2 _certificate = TestCertificates.CreateSelfSigned();

    public void Dispose()
    {
        _certificate.Dispose();
        _timeout.Dispose();
    }

    [Fact]
    public async Task Handshake_establishes_and_plaintext_roundtrips()
    {
        (IDuplexPipe serverSide, IDuplexPipe clientSide) = CreateTransportPair();

        (SecureChannelResult server, SecureChannelResult client) =
            await HandshakeAsync(serverSide, clientSide, PinnedClientOptions());

        Assert.True(server.IsEstablished);
        Assert.True(client.IsEstablished);

        byte[] request = [1, 2, 3, 4, 5];
        await client.Channel!.Output.WriteAsync(request, _timeout.Token);
        byte[] received = await ReadExactlyAsync(server.Channel!.Input, request.Length);
        Assert.Equal(request, received);

        byte[] response = [9, 8, 7];
        await server.Channel!.Output.WriteAsync(response, _timeout.Token);
        byte[] echoed = await ReadExactlyAsync(client.Channel!.Input, response.Length);
        Assert.Equal(response, echoed);

        await server.Channel!.DisposeAsync();
        await client.Channel!.DisposeAsync();
    }

    [Fact]
    public async Task Untrusted_certificate_reports_handshake_failed_without_throwing()
    {
        (IDuplexPipe serverSide, IDuplexPipe clientSide) = CreateTransportPair();

        // 검증 콜백 없음 → 기본 체인 검증 → 자가서명은 불신 → 실패해야 한다.
        TlsTransportSecurity client = new(new TlsSecurityOptions { TargetHost = "localhost" });

        (SecureChannelResult serverResult, SecureChannelResult clientResult) =
            await HandshakeAsync(serverSide, clientSide, client);

        Assert.Equal(SecureChannelStatus.HandshakeFailed, clientResult.Status);
        Assert.Null(clientResult.Channel);

        // 서버 쪽 단언은 느슨하다 — TLS 1.3 은 서버가 자기 Finished 직후(클라이언트의
        // 인증서 검증 전, 0.5-RTT) 확립을 보고할 수 있어 플랫폼에 따라 결과가 갈린다.
        // 계약으로 고정할 것은 "매달리지 않는다"(HandshakeAsync 의 WhenAll 이 이미 끝났다)와
        // "확립됐다면 실패가 첫 읽기에서 드러난다"뿐이다.
        if (serverResult.IsEstablished)
        {
            try
            {
                ReadResult read = await serverResult.Channel!.Input.ReadAsync(_timeout.Token);
                Assert.True(read.IsCompleted); // 잘려 끝났다 — 데이터가 온 것처럼 보이면 안 된다
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException)
            {
                // 클라이언트의 거부 알림(alert)이 읽기 실패로 드러난 경우 — 역시 합격.
            }

            await serverResult.Channel!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Precanceled_token_reports_canceled()
    {
        (IDuplexPipe serverSide, _) = CreateTransportPair();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        TlsTransportSecurity server = new(new TlsSecurityOptions { ServerCertificate = _certificate });
        SecureChannelResult result = await server.SecureAsServerAsync(serverSide, cts.Token);

        Assert.Equal(SecureChannelStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task Server_dispose_surfaces_as_graceful_completion_to_client()
    {
        (IDuplexPipe serverSide, IDuplexPipe clientSide) = CreateTransportPair();

        (SecureChannelResult server, SecureChannelResult client) =
            await HandshakeAsync(serverSide, clientSide, PinnedClientOptions());

        await server.Channel!.DisposeAsync();

        // close_notify → 평문 스트림 EOF → 파이프 완결. "잘림"이 아니라 정상 종료로 보여야 한다.
        ReadResult read = await client.Channel!.Input.ReadAsync(_timeout.Token);
        Assert.True(read.IsCompleted);
        Assert.True(read.Buffer.IsEmpty);

        await client.Channel!.DisposeAsync();
    }

    [Fact]
    public async Task Missing_direction_configuration_is_an_assembly_fault()
    {
        // 서버 인증서 없이 서버 역할 → 공격이 아니라 조립 결함이므로 예외가 맞다.
        TlsTransportSecurity clientOnly = new(new TlsSecurityOptions { TargetHost = "localhost" });
        (IDuplexPipe serverSide, _) = CreateTransportPair();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await clientOnly.SecureAsServerAsync(serverSide, CancellationToken.None));
    }

    [Fact]
    public void Empty_options_fail_at_assembly_time()
    {
        Assert.Throws<InvalidOperationException>(static () => new TlsTransportSecurity(new TlsSecurityOptions()));
    }

    // ── 조립 도우미 ────────────────────────────────────────────

    private static (IDuplexPipe ServerSide, IDuplexPipe ClientSide) CreateTransportPair()
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        return (
            new DuplexPipeAdapter(clientToServer.Reader, serverToClient.Writer),
            new DuplexPipeAdapter(serverToClient.Reader, clientToServer.Writer));
    }

    private TlsTransportSecurity PinnedClientOptions() => new(new TlsSecurityOptions
    {
        TargetHost = "localhost",
        // 자가서명 테스트 인증서를 지문으로 핀 고정 — "무조건 true" 콜백을 쓰지 않는 모범을 테스트에도 적용한다.
        RemoteCertificateValidation = (_, certificate, _, _) =>
            certificate is X509Certificate2 received && received.Thumbprint == _certificate.Thumbprint,
    });

    private async Task<(SecureChannelResult Server, SecureChannelResult Client)> HandshakeAsync(
        IDuplexPipe serverSide, IDuplexPipe clientSide, TlsTransportSecurity client)
    {
        TlsTransportSecurity server = new(new TlsSecurityOptions { ServerCertificate = _certificate });

        Task<SecureChannelResult> serverTask = server.SecureAsServerAsync(serverSide, _timeout.Token).AsTask();
        Task<SecureChannelResult> clientTask = client.SecureAsClientAsync(clientSide, _timeout.Token).AsTask();
        await Task.WhenAll(serverTask, clientTask);

        return (await serverTask, await clientTask);
    }

    private async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(_timeout.Token);
            if (result.Buffer.Length >= count)
            {
                byte[] data = result.Buffer.Slice(0, count).ToArray();
                reader.AdvanceTo(result.Buffer.GetPosition(count));
                return data;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException("스트림이 조기에 완결됐다.");
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    private sealed class DuplexPipeAdapter(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
