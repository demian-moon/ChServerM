using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
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
    /// <summary>고정 인증서 경로의 컨텍스트 — 생성 시점에 1회 만든다(감사 2026-08-18 T-3).</summary>
    private readonly SslStreamCertificateContext? _serverCertificateContext;
    private readonly IServerCertificateSource? _certificateSource;
    private readonly string? _targetHost;
    private readonly SslProtocols _enabledProtocols;
    private readonly RemoteCertificateValidationCallback? _remoteCertificateValidation;
    private readonly TimeSpan _handshakeTimeout;

    /// <summary>옵션을 검증·고정해 어댑터를 만든다.</summary>
    /// <param name="options">TLS 설정. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 null 일 때.</exception>
    /// <exception cref="InvalidOperationException">옵션 조합이 유효하지 않을 때(<see cref="TlsSecurityOptions.Validate"/>).</exception>
    public TlsTransportSecurity(TlsSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        // 고정 인스턴스는 여기서 컨텍스트로 1회 승격한다 — 원시 인증서를 핸드셰이크마다
        // 넘기면 SslStream 이 매번 체인을 재구축한다(OS 저장소 조회 포함, 감사 2026-08-18 T-3).
        // offline: true — 조립 시점이라도 네트워크(AIA) 조회에 매달리지 않는다(원천 쪽과 같은 규율).
        _serverCertificateContext = options.ServerCertificate is { } serverCertificate
            ? SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null, offline: true)
            : null;
        _certificateSource = options.ServerCertificateSource;
        _targetHost = options.TargetHost;
        _enabledProtocols = options.EnabledProtocols;
        _remoteCertificateValidation = options.RemoteCertificateValidation;
        _handshakeTimeout = options.HandshakeTimeout;
    }

    /// <inheritdoc />
    public async ValueTask<SecureChannelResult> SecureAsServerAsync(IDuplexPipe transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        // 원천이 있으면 핸드셰이크마다 해석한다 — 회전이 새 핸드셰이크부터 즉시 반영된다.
        // 어느 쪽이든 컨텍스트다 — 체인 구축은 적재·회전 시점에 이미 끝났다(감사 2026-08-18 T-3).
        SslStreamCertificateContext? certificateContext =
            _certificateSource?.GetCertificateContext() ?? _serverCertificateContext;
        if (certificateContext is null)
        {
            throw new InvalidOperationException(
                $"서버 역할에는 {nameof(TlsSecurityOptions.ServerCertificate)} 또는 " +
                $"{nameof(TlsSecurityOptions.ServerCertificateSource)}가 필요하다. 조립 설정을 확인한다.");
        }

        SslStream ssl = new(new DuplexPipeStream(transport), leaveInnerStreamOpen: false);

        // ⚠ 핸드셰이크 상한 — ClientHello 를 보내지 않는 클라이언트가 커넥션 슬롯을 무기한
        //   점유하지 못하게 한다(slowloris, T-16). 커넥션 종료 토큰과 합류시킨다 —
        //   ITransportSecurity 계약이 예고한 바로 그 합류 지점이다(감사 2026-08-18 T-1).
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_handshakeTimeout);

        try
        {
            SslServerAuthenticationOptions authOptions = new()
            {
                ServerCertificateContext = certificateContext,
                EnabledSslProtocols = _enabledProtocols,
                // 클라이언트 인증서는 쓰지 않는다 — 클라이언트 인증은 IAuthenticator(토큰)의 몫이다.
                ClientCertificateRequired = false,
            };
            await ssl.AuthenticateAsServerAsync(authOptions, timeout.Token).ConfigureAwait(false);
#pragma warning disable CA2000 // 소유권 이전 — 채널은 결과에 실려 호출자(호스팅)에게 넘어가고, 호출자가 Dispose 한다(ISecureChannel 수명 계약). 분석기는 구조체 경유 이전을 추적하지 못한다.
            return SecureChannelResult.Established(new TlsSecureChannel(ssl));
#pragma warning restore CA2000
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            // 상한 초과는 취소가 아니라 실패다 — 취소로 위장하면 공격 시나리오가 관측에서 사라진다.
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.HandshakeFailed);
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

        // 서버 쪽과 같은 상한 — 응답하지 않는 서버에 접속 시도가 무기한 매달리지 않는다.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_handshakeTimeout);

        try
        {
            SslClientAuthenticationOptions authOptions = new()
            {
                TargetHost = _targetHost,
                EnabledSslProtocols = _enabledProtocols,
                RemoteCertificateValidationCallback = _remoteCertificateValidation,
            };
            await ssl.AuthenticateAsClientAsync(authOptions, timeout.Token).ConfigureAwait(false);
#pragma warning disable CA2000 // 소유권 이전 — 채널은 결과에 실려 호출자(호스팅)에게 넘어가고, 호출자가 Dispose 한다(ISecureChannel 수명 계약). 분석기는 구조체 경유 이전을 추적하지 못한다.
            return SecureChannelResult.Established(new TlsSecureChannel(ssl));
#pragma warning restore CA2000
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.HandshakeFailed);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return SecureChannelResult.Failed(SecureChannelStatus.HandshakeFailed);
        }
    }
}
