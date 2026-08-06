using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ChServerM.Security.Tls;

/// <summary>
/// <see cref="TlsTransportSecurity"/>의 설정.
/// </summary>
/// <remarks>
/// <para>
/// 방향별 세부(서버 인증서·검증 대상 호스트)를 Core 계약이 아니라 여기에 두는 것이
/// ADR-0017 의 결정이다 — Core 표면 최소화. 서버 전용 조립이면
/// <see cref="ServerCertificate"/>만, 클라이언트 전용이면 <see cref="TargetHost"/>만 채운다.
/// </para>
/// <para>
/// <b>기본 프로토콜은 TLS 1.3 단독이다.</b> TLS 1.2 허용은 구형 클라이언트 지원이
/// 확인된 경우에만 의도적으로 넓힌다(ADR-0017 부정 항목) — 기본값이 넓으면
/// 아무도 좁히지 않는다.
/// </para>
/// <para>
/// 검증은 <see cref="Validate"/>가 조립 시점에 수행한다 — 잘못된 설정은
/// 첫 커넥션이 아니라 시작 시점에 실패해야 한다(CLAUDE.md 2절).
/// </para>
/// </remarks>
public sealed class TlsSecurityOptions
{
    /// <summary>서버 측 핸드셰이크에 제시할 고정 인증서(개인키 포함).
    /// <see cref="ServerCertificateSource"/>와 상호 배타 — 서버 역할이면 둘 중 하나가 필수다.</summary>
    /// <remarks>
    /// 인증서 수명은 호출자가 소유한다 — 이 옵션은 참조만 보관하며 폐기하지 않는다.
    /// 고정 인스턴스는 회전이 없다 — 프로덕션 회전 경로는 <see cref="ServerCertificateSource"/>
    /// (예: <see cref="FileCertificateSource"/>)를 쓴다.
    /// </remarks>
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>서버 인증서의 원천 — 핸드셰이크마다 해석되므로 회전이 재시작 없이 반영된다.</summary>
    /// <remarks>
    /// <see cref="ServerCertificate"/>와 상호 배타(<see cref="Validate"/>가 거부).
    /// 원천의 수명은 조립하는 쪽이 소유한다 — 서버 종료 시 함께 <c>Dispose</c> 한다.
    /// </remarks>
    public IServerCertificateSource? ServerCertificateSource { get; set; }

    /// <summary>클라이언트 측이 검증할 서버 호스트명. 클라이언트 역할이면 필수다.</summary>
    public string? TargetHost { get; set; }

    /// <summary>허용 TLS 버전. 기본값은 <see cref="SslProtocols.Tls13"/> 단독.</summary>
    public SslProtocols EnabledProtocols { get; set; } = SslProtocols.Tls13;

    /// <summary>클라이언트 측 서버 인증서 검증 재정의. null 이면 기본 체인 검증(공인 CA)이다.</summary>
    /// <remarks>
    /// 사설 CA·핀 고정(pinning)·테스트용이다. <b>무조건 true 를 반환하는 콜백은
    /// TLS 의 서버 인증(T-01 완화)을 통째로 끈다</b> — 프로덕션에서 그렇게 쓰지 않는다.
    /// </remarks>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; set; }

    /// <summary>옵션 조합을 검증한다. 조립 시점에 호출된다.</summary>
    /// <exception cref="InvalidOperationException">어느 방향도 구성되지 않았거나 프로토콜이 비어 있을 때.</exception>
    public void Validate()
    {
        if (EnabledProtocols == SslProtocols.None)
        {
            throw new InvalidOperationException(
                $"{nameof(EnabledProtocols)}가 None 이다. 프로토콜 선택을 OS 기본값에 맡기지 않는다 — " +
                "기본값은 시점·플랫폼에 따라 달라져 조립 결과가 재현되지 않는다.");
        }

        if (ServerCertificate is not null && ServerCertificateSource is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(ServerCertificate)}와 {nameof(ServerCertificateSource)}가 함께 지정됐다 — " +
                "어느 쪽이 진짜인지 모호하다. 고정 인스턴스 또는 원천 중 하나만 쓴다.");
        }

        if (ServerCertificate is null && ServerCertificateSource is null && string.IsNullOrEmpty(TargetHost))
        {
            throw new InvalidOperationException(
                $"{nameof(ServerCertificate)}/{nameof(ServerCertificateSource)}(서버 역할)와 " +
                $"{nameof(TargetHost)}(클라이언트 역할) 전부 비어 있다. " +
                "어느 방향의 핸드셰이크도 수행할 수 없는 설정이다.");
        }
    }
}
