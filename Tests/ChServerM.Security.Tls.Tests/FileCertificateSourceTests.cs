using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Security;
using Xunit;

namespace ChServerM.Security.Tls.Tests;

/// <summary>
/// 파일 인증서 원천의 운영 계약 — 적재(PFX/PEM, Windows ephemeral 함정 포함),
/// 회전(주기 도래·명시 Reload), 실패 시 기존 유지, 구세대 보호를 고정한다.
/// </summary>
public sealed class FileCertificateSourceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("chsm-cert-").FullName;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        _timeout.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 임시 디렉터리 정리 실패는 테스트 실패가 아니다.
        }
    }

    // ── 적재 ─────────────────────────────────────────────────────

    [Fact]
    public void Pfx_loads_with_private_key()
    {
        (string thumbprint, byte[] pfx, _, _) = TestCertificates.CreateFileMaterial();
        string path = WriteFile("cert.pfx", pfx);

        using FileCertificateSource source = new(new FileCertificateOptions { PfxPath = path });
        X509Certificate2 loaded = source.GetCertificateContext().TargetCertificate;

        Assert.Equal(thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Context_is_created_once_per_generation_not_per_handshake()
    {
        // 감사 2026-08-18 T-3 — 컨텍스트가 호출마다 새로 만들어지면 핸드셰이크마다
        // 체인 재구축 비용을 그대로 내는 것이다. 같은 세대에서는 같은 인스턴스여야 한다.
        (_, byte[] pfx, _, _) = TestCertificates.CreateFileMaterial();
        string path = WriteFile("cert.pfx", pfx);

        using FileCertificateSource source = new(new FileCertificateOptions { PfxPath = path });

        SslStreamCertificateContext first = source.GetCertificateContext();
        SslStreamCertificateContext second = source.GetCertificateContext();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Pem_pair_loads_and_completes_tls_handshake()
    {
        // PEM 경로의 회귀 방지 지점 — CreateFromPemFile 의 ephemeral 개인키를 Schannel 이
        // 거부하는 Windows 함정을 원천이 PFX 왕복으로 흡수하는지, 실제 핸드셰이크로 검증한다.
        (string thumbprint, _, string certificatePem, string privateKeyPem) = TestCertificates.CreateFileMaterial();
        string certificatePath = WriteFile("cert.pem", certificatePem);
        string keyPath = WriteFile("key.pem", privateKeyPem);

        using FileCertificateSource source = new(new FileCertificateOptions
        {
            CertificatePemPath = certificatePath,
            PrivateKeyPemPath = keyPath,
        });

        TlsTransportSecurity server = new(new TlsSecurityOptions { ServerCertificateSource = source });
        TlsTransportSecurity client = new(new TlsSecurityOptions
        {
            TargetHost = "localhost",
            RemoteCertificateValidation = (_, certificate, _, _) =>
                certificate is X509Certificate2 received && received.Thumbprint == thumbprint,
        });

        (SecureChannelResult serverResult, SecureChannelResult clientResult) =
            await HandshakeAsync(server, client);

        Assert.True(serverResult.IsEstablished);
        Assert.True(clientResult.IsEstablished);

        await serverResult.Channel!.DisposeAsync();
        await clientResult.Channel!.DisposeAsync();
    }

    [Fact]
    public void Missing_file_fails_at_construction()
    {
        // 시작 시점 적재 실패는 예외 — 잘못 조립된 서버는 뜨지 않는 편이 낫다.
        // 예외 형태(파일 IO vs 암호화 계층)는 플랫폼·로더 구현에 따라 갈려 고정하지 않는다.
        Exception? thrown = Record.Exception(() => new FileCertificateSource(new FileCertificateOptions
        {
            PfxPath = Path.Combine(_directory, "does-not-exist.pfx"),
        }));

        Assert.True(
            thrown is IOException or CryptographicException,
            $"예상 밖 예외 형태: {thrown?.GetType().Name ?? "(없음)"}");
    }

    // ── 회전 ─────────────────────────────────────────────────────

    [Fact]
    public void Rotation_applies_after_interval_and_old_generation_stays_usable()
    {
        (string firstThumbprint, byte[] firstPfx, _, _) = TestCertificates.CreateFileMaterial();
        (string secondThumbprint, byte[] secondPfx, _, _) = TestCertificates.CreateFileMaterial();
        string path = WriteFile("cert.pfx", firstPfx);
        ManualTimeProvider time = new();

        using FileCertificateSource source = new(
            new FileCertificateOptions { PfxPath = path, ReloadCheckInterval = TimeSpan.FromMinutes(1) },
            time);

        SslStreamCertificateContext contextA = source.GetCertificateContext();
        X509Certificate2 generationA = contextA.TargetCertificate;
        Assert.Equal(firstThumbprint, generationA.Thumbprint);

        // 파일 교체 — 주기 도래 전에는 옛 인증서(같은 컨텍스트 인스턴스)가 유지된다(파일 IO 없이).
        OverwriteFile(path, secondPfx);
        Assert.Same(contextA, source.GetCertificateContext());

        // 주기 도래 후 첫 핸드셰이크가 회전을 집는다 — 새 세대는 새 컨텍스트다(T-3).
        time.Advance(TimeSpan.FromMinutes(2));
        SslStreamCertificateContext contextB = source.GetCertificateContext();
        Assert.NotSame(contextA, contextB);
        Assert.Equal(secondThumbprint, contextB.TargetCertificate.Thumbprint);

        // 직전 세대는 폐기되지 않았다 — 진행 중 핸드셰이크가 참조할 수 있다.
        Assert.Equal(firstThumbprint, generationA.Thumbprint);
    }

    [Fact]
    public void Reload_failure_keeps_current_and_recovers_next_cycle()
    {
        (string firstThumbprint, byte[] firstPfx, _, _) = TestCertificates.CreateFileMaterial();
        (string secondThumbprint, byte[] secondPfx, _, _) = TestCertificates.CreateFileMaterial();
        string path = WriteFile("cert.pfx", firstPfx);
        ManualTimeProvider time = new();

        using FileCertificateSource source = new(
            new FileCertificateOptions { PfxPath = path, ReloadCheckInterval = TimeSpan.FromMinutes(1) },
            time);

        // 1. 깨진 파일(반쯤 쓰인 순간의 모사) — 기존 인증서로 계속 서비스해야 한다.
        OverwriteFile(path, [1, 2, 3]);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(firstThumbprint, source.GetCertificateContext().TargetCertificate.Thumbprint);

        // 2. 다음 주기에 정상 파일이 오면 복구된다.
        OverwriteFile(path, secondPfx);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(secondThumbprint, source.GetCertificateContext().TargetCertificate.Thumbprint);
    }

    [Fact]
    public void Explicit_reload_applies_immediately_even_with_auto_check_disabled()
    {
        (string firstThumbprint, byte[] firstPfx, _, _) = TestCertificates.CreateFileMaterial();
        (string secondThumbprint, byte[] secondPfx, _, _) = TestCertificates.CreateFileMaterial();
        string path = WriteFile("cert.pfx", firstPfx);

        using FileCertificateSource source = new(
            new FileCertificateOptions { PfxPath = path, ReloadCheckInterval = TimeSpan.Zero });

        OverwriteFile(path, secondPfx);

        // 자동 재확인이 꺼져 있으므로 교체가 반영되지 않는다.
        Assert.Equal(firstThumbprint, source.GetCertificateContext().TargetCertificate.Thumbprint);

        // 운영 신호(SIGHUP 류)의 명시 재적재는 즉시 반영된다.
        source.Reload();
        Assert.Equal(secondThumbprint, source.GetCertificateContext().TargetCertificate.Thumbprint);
    }

    // ── 조립 시점 검증 ────────────────────────────────────────────

    [Fact]
    public void Option_combinations_are_validated()
    {
        // 형식 미지정.
        Assert.Throws<InvalidOperationException>(
            static () => new FileCertificateOptions().Validate());

        // PFX 와 PEM 혼합.
        Assert.Throws<InvalidOperationException>(() => new FileCertificateOptions
        {
            PfxPath = "a.pfx",
            CertificatePemPath = "a.pem",
        }.Validate());

        // PEM 인증서만 있고 개인키가 없다.
        Assert.Throws<InvalidOperationException>(() => new FileCertificateOptions
        {
            CertificatePemPath = "a.pem",
        }.Validate());

        // 음수 주기.
        Assert.Throws<InvalidOperationException>(() => new FileCertificateOptions
        {
            PfxPath = "a.pfx",
            ReloadCheckInterval = TimeSpan.FromSeconds(-1),
        }.Validate());
    }

    [Fact]
    public void Fixed_certificate_and_source_are_mutually_exclusive()
    {
        (_, byte[] pfx, _, _) = TestCertificates.CreateFileMaterial();
        string path = WriteFile("cert.pfx", pfx);
        using X509Certificate2 fixedCertificate = TestCertificates.CreateSelfSigned();
        using FileCertificateSource source = new(new FileCertificateOptions { PfxPath = path });

        TlsSecurityOptions options = new()
        {
            ServerCertificate = fixedCertificate,
            ServerCertificateSource = source,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    // ── 도우미 ───────────────────────────────────────────────────

    private string WriteFile(string name, byte[] contents)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private string WriteFile(string name, string contents)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void OverwriteFile(string path, byte[] contents)
    {
        File.WriteAllBytes(path, contents);
        Touch(path);
    }

    /// <summary>수정 시각을 확실히 전진시킨다 — 파일시스템 시각 해상도에 기대지 않는다.</summary>
    private static void Touch(string path) =>
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

    private async Task<(SecureChannelResult Server, SecureChannelResult Client)> HandshakeAsync(
        TlsTransportSecurity server, TlsTransportSecurity client)
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        DuplexPipeAdapter serverSide = new(clientToServer.Reader, serverToClient.Writer);
        DuplexPipeAdapter clientSide = new(serverToClient.Reader, clientToServer.Writer);

        Task<SecureChannelResult> serverTask = server.SecureAsServerAsync(serverSide, _timeout.Token).AsTask();
        Task<SecureChannelResult> clientTask = client.SecureAsClientAsync(clientSide, _timeout.Token).AsTask();
        await Task.WhenAll(serverTask, clientTask);

        return (await serverTask, await clientTask);
    }

    private sealed class DuplexPipeAdapter(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    /// <summary>손으로 감는 시계 — 재확인 주기를 실제 대기 없이 검증한다.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
