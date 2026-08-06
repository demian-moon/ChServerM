using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Security.Tls;

/// <summary>
/// 전송 보안 축의 TLS 1.3 어댑터 — <c>SslStream</c> 위임 (ADR-0017).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 키 교환·nonce 관리·무결성·다운그레이드 방지를 검증된 TLS 구현에
/// 위임한다. 이 어셈블리에 암호 프리미티브 호출이 하나도 없는 것이 설계 목표다 —
/// 자체 암호를 만들지 않는 것 자체가 1차 완화책이다.
/// </para>
/// <para>
/// <b>레거시 대응.</b> docs/legacy/07-security 의 전량 폐기 판정(미인증 RSA 교환,
/// XOR, 세션 고정 IV CBC)과 docs/legacy/05-client 의 커넥션당 RSA 2048 키쌍 생성
/// (CPU 고갈, THREAT-MODEL T-16)을 막는다 — 서버 비대칭 연산은 TLS 1.3 핸드셰이크의
/// ECDHE 1회 수준이고 인증서 키는 재사용된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스 하나를 모든 커넥션이 공유 호출한다 — 생성 시점에
/// 옵션을 고정하고 이후 무상태다. 반환된 채널은 그 커넥션 전용이다.
/// </para>
/// <para>
/// <b>실패 규약.</b> 핸드셰이크 실패(<see cref="AuthenticationException"/>·
/// <see cref="IOException"/>)와 취소는 상태로 번역해 반환하고, 그 전에 원본 파이프에
/// 완결을 전파해 상대측 대기를 푼다. 조립 결함(서버 역할인데 인증서 없음)은
/// 예외다 — 공격자가 아니라 운영자가 만든 오류라서다.
/// </para>
/// </remarks>
public sealed class TlsTransportSecurity : ITransportSecurity
{
    private readonly X509Certificate2? _serverCertificate;
    private readonly IServerCertificateSource? _certificateSource;
    private readonly string? _targetHost;
    private readonly SslProtocols _enabledProtocols;
    private readonly RemoteCertificateValidationCallback? _remoteCertificateValidation;

    /// <summary>옵션을 검증·고정해 어댑터를 만든다.</summary>
    /// <param name="options">TLS 설정. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 null 일 때.</exception>
    /// <exception cref="InvalidOperationException">옵션 조합이 유효하지 않을 때(<see cref="TlsSecurityOptions.Validate"/>).</exception>
    public TlsTransportSecurity(TlsSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _serverCertificate = options.ServerCertificate;
        _certificateSource = options.ServerCertificateSource;
        _targetHost = options.TargetHost;
        _enabledProtocols = options.EnabledProtocols;
        _remoteCertificateValidation = options.RemoteCertificateValidation;
    }

    /// <inheritdoc />
    public async ValueTask<SecureChannelResult> SecureAsServerAsync(IDuplexPipe transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        // 원천이 있으면 핸드셰이크마다 해석한다 — 회전이 새 핸드셰이크부터 즉시 반영된다.
        X509Certificate2? certificate = _certificateSource?.GetCertificate() ?? _serverCertificate;
        if (certificate is null)
        {
            throw new InvalidOperationException(
                $"서버 역할에는 {nameof(TlsSecurityOptions.ServerCertificate)} 또는 " +
                $"{nameof(TlsSecurityOptions.ServerCertificateSource)}가 필요하다. 조립 설정을 확인한다.");
        }

        SslStream ssl = new(new DuplexPipeStream(transport), leaveInnerStreamOpen: false);
        try
        {
            SslServerAuthenticationOptions authOptions = new()
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = _enabledProtocols,
                // 클라이언트 인증서는 쓰지 않는다 — 클라이언트 인증은 IAuthenticator(토큰)의 몫이다.
                ClientCertificateRequired = false,
            };
            await ssl.AuthenticateAsServerAsync(authOptions, cancellationToken).ConfigureAwait(false);
#pragma warning disable CA2000 // 소유권 이전 — 채널은 결과에 실려 호출자(호스팅)에게 넘어가고, 호출자가 Dispose 한다(ISecureChannel 수명 계약). 분석기는 구조체 경유 이전을 추적하지 못한다.
            return SecureChannelResult.Established(new TlsSecureChannel(ssl));
#pragma warning restore CA2000
        }
        catch (OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.Canceled);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            // 폐기가 브리지를 거쳐 원본 파이프를 완결시킨다 — 상대측 대기가 풀린다.
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.HandshakeFailed);
        }
    }

    /// <inheritdoc />
    public async ValueTask<SecureChannelResult> SecureAsClientAsync(IDuplexPipe transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (string.IsNullOrEmpty(_targetHost))
        {
            throw new InvalidOperationException(
                $"클라이언트 역할에는 {nameof(TlsSecurityOptions.TargetHost)}가 필요하다. 조립 설정을 확인한다.");
        }

        SslStream ssl = new(new DuplexPipeStream(transport), leaveInnerStreamOpen: false);
        try
        {
            SslClientAuthenticationOptions authOptions = new()
            {
                TargetHost = _targetHost,
                EnabledSslProtocols = _enabledProtocols,
                RemoteCertificateValidationCallback = _remoteCertificateValidation,
            };
            await ssl.AuthenticateAsClientAsync(authOptions, cancellationToken).ConfigureAwait(false);
#pragma warning disable CA2000 // 소유권 이전 — 채널은 결과에 실려 호출자(호스팅)에게 넘어가고, 호출자가 Dispose 한다(ISecureChannel 수명 계약). 분석기는 구조체 경유 이전을 추적하지 못한다.
            return SecureChannelResult.Established(new TlsSecureChannel(ssl));
#pragma warning restore CA2000
        }
        catch (OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.Canceled);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.HandshakeFailed);
        }
    }
}
