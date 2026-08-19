using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

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
    /// (예: <see cref="FileCertificateSource"/>)를 쓴다. 어댑터는 이 인스턴스로
    /// <see cref="System.Net.Security.SslStreamCertificateContext"/>를 생성 시점에 1회 만들어
    /// 보관한다 — 핸드셰이크마다 체인을 재구축하지 않는다(감사 2026-08-18 T-3). 따라서
    /// 어댑터 생성 후에는 이 인증서를 폐기하면 안 된다.
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
    /// <remarks>
    /// <b>CA5398 억제 근거.</b> 분석기는 <see cref="SslProtocols"/> 하드코딩을 경고하며 OS 가
    /// 버전을 고르게 <c>None</c> 을 권한다. 그러나 이 프레임워크는 ADR-0017 에서 <b>TLS 1.3
    /// 을 기본으로 못박는 것</b>이 결정이다 — 구버전으로의 다운그레이드를 기본값이 열어두지
    /// 않는다. 게다가 이 값은 설정 가능한 <b>기본값</b>이지 고정 상수가 아니다(소비자가
    /// 재정의할 수 있다). 따라서 CA5398 은 이 설계에 대한 오탐이다.
    /// </remarks>
#pragma warning disable CA5398 // TLS 1.3 을 안전한 기본값으로 못박는다(ADR-0017) — 위 remarks 참조.
    public SslProtocols EnabledProtocols { get; set; } = SslProtocols.Tls13;
#pragma warning restore CA5398

    /// <summary>핸드셰이크 상한 시간. 기본 10초. <b>끌 수 없다</b> — 0·음수·무한은 검증이 거부한다.</summary>
    /// <remarks>
    /// TCP 연결 후 ClientHello 를 보내지 않거나 찔끔찔끔 보내는 클라이언트(slowloris)는
    /// 이 상한이 없으면 핸드셰이크 대기 상태로 커넥션 슬롯·메모리를 무기한 점유한다 —
    /// <c>IdleTimeout</c> 기본값이 비활성이라 기본 조립에는 다른 회수 경로가 없다
    /// (감사 2026-08-18 T-1, THREAT-MODEL T-16). Kestrel 의 기본 TLS 핸드셰이크 타임아웃(10초)과
    /// 같은 값이며, <c>VersionNegotiationOptions.HandshakeTimeout</c>과 같은 규율(끌 수 없음)이다.
    /// 초과는 <see cref="Security.SecureChannelStatus.HandshakeFailed"/>로 관측된다 — 외부
    /// 취소(<see cref="Security.SecureChannelStatus.Canceled"/>)와 구분된다.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

        if (HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException(
                $"{nameof(HandshakeTimeout)}({HandshakeTimeout})는 양수여야 하고 끌 수 없다 — " +
                "상한 없는 핸드셰이크 대기는 slowloris 형 점유 공격에 커넥션 슬롯을 내준다(T-16).");
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
