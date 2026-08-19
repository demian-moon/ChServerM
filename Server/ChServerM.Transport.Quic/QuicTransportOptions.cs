using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ChServerM.Diagnostics;
using ChServerM.Resilience;

namespace ChServerM.Transport.Quic;

/// <summary>
/// QUIC 전송의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 다른 전송 옵션과 같은 원칙 — 커넥션 수를 곱하면 자원량이 되는 숫자와
/// 조합 검사의 입력을 설정으로 드러낸다. 여기에 더해 <b>QUIC 은 TLS 가 프로토콜 내장</b>이라
/// 서버 인증서가 조립의 필수 입력이다.
/// </para>
/// <para>
/// <b>⚠ 서버 인증서에는 기본값이 없다</b> — 옳은 기본값이 없으면 기본값을 두지 않는다
/// (ADR-0051 결정 6과 같은 자리). 자가서명이라도 serverAuth EKU 가 있어야 하고, 임시 생성
/// 인증서는 PFX 왕복으로 다시 로드해야 한다(Schannel 의 비영속 키 거부 — ADR-0060 실측).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 전송 생성 전에 단일 스레드에서 채우고 넘긴다. 전송이 값을 복사하므로
/// 생성 후의 변경은 반영되지 않는다.
/// </para>
/// </remarks>
public sealed class QuicTransportOptions
{
    /// <summary>기본 ALPN 프로토콜 이름.</summary>
    public const string DefaultAlpnProtocol = "chsm";

    /// <summary>기본 수신 일시정지 임계값. 64 KiB.</summary>
    public const long DefaultPauseWriterThreshold = 64 * 1024;

    /// <summary>기본 수신 재개 임계값. 32 KiB.</summary>
    public const long DefaultResumeWriterThreshold = 32 * 1024;

    /// <summary>QUIC 연결 하나가 받는 인바운드 양방향 스트림 상한의 기본값.</summary>
    public const int DefaultMaxStreamsPerConnection = 512;

    /// <summary>기본 강제 종료 상한. 5초.</summary>
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>ALPN 프로토콜 이름. 서버·클라이언트가 같아야 연결이 수립된다.</summary>
    public string AlpnProtocol { get; set; } = DefaultAlpnProtocol;

    /// <summary>고정 서버 인증서. <see cref="ServerCertificateContextSource"/>와 상호 배타 —
    /// 서버 전송이면 둘 중 하나가 필수다. QUIC 은 TLS 없이 서지 않는다.</summary>
    /// <remarks>
    /// 클라이언트 전송에서는 무시된다. 고정 인스턴스는 회전이 없다 — 전송이 바인드 시점에
    /// <see cref="SslStreamCertificateContext"/>로 1회 승격해 보관한다(감사 2026-08-18 T-3).
    /// 회전이 필요하면 <see cref="ServerCertificateContextSource"/>를 쓴다.
    /// </remarks>
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>서버 인증서 컨텍스트의 원천 — <b>연결 수립마다</b> 해석되므로 인증서 회전이
    /// 재시작 없이 새 연결부터 반영된다(감사 2026-08-18 T-4). <see cref="ServerCertificate"/>와 상호 배타.</summary>
    /// <remarks>
    /// <para>
    /// 타입이 <c>Func</c>인 이유: 회전 원천의 참조 구현은
    /// <c>ChServerM.Security.Tls.FileCertificateSource</c>(<c>IServerCertificateSource</c>)지만,
    /// 이 어셈블리가 TLS 어댑터를 참조하면 어댑터끼리 결합된다 — 메서드 그룹
    /// (<c>source.GetCertificateContext</c>)을 그대로 넘기면 같은 원천을 결합 없이 재사용한다.
    /// </para>
    /// <para>
    /// 콜백은 여러 연결 수립이 동시에 부른다 — 스레드 안전해야 하고, 보관해 둔 컨텍스트를
    /// 돌려줘야 한다(호출 시점 체인 구축 금지 — 연결 수립 경로다). 돌려준 컨텍스트의 수명은
    /// 원천이 소유한다. 클라이언트 전송에서는 무시된다.
    /// </para>
    /// </remarks>
    public Func<SslStreamCertificateContext>? ServerCertificateContextSource { get; set; }

    /// <summary>신규 커넥션(스트림) 동적 수용 제어. <see langword="null"/>이면 정적 상한만 적용.</summary>
    /// <remarks>
    /// <see cref="MaxConnections"/>(정적 하드 상한)를 통과한 뒤에만 물어본다. 거부하면 스트림을
    /// 즉시 중단으로 닫는다 — TCP 전송과 같은 배선(감사 2026-08-18 T-5). 참조 구현:
    /// <c>ChServerM.Hosting.ConnectionRateAdmissionControl</c>(토큰 버킷).
    /// </remarks>
    public IAdmissionControl? AdmissionControl { get; set; }

    /// <summary>커넥션 거부를 관측할 메트릭 싱크. <see langword="null"/>이면 기록하지 않는다.</summary>
    /// <remarks>
    /// 거부된 스트림은 핸들러에 닿지 않으므로 거부(<see cref="MetricNames.ConnectionsRejected"/>)는
    /// 전송이 직접 방출한다 — 정적 상한·동적 수용·드레인 거부 모두 관측된다(감사 2026-08-18 T-5,
    /// CLAUDE.md 9.6 "드롭 수를 메트릭으로 노출한다").
    /// </remarks>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>클라이언트의 서버 인증서 검증 콜백. <see langword="null"/> 이면 시스템 기본 신뢰 체계.</summary>
    /// <remarks>
    /// <b>무조건 <see langword="true"/> 콜백은 프로덕션 금지 패턴이다</b> — 테스트의 자가서명
    /// 인증서처럼 검증할 신뢰 체계가 없는 경우에만 쓴다(TLS 어댑터와 같은 경고).
    /// 서버 전송에서는 무시된다.
    /// </remarks>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; set; }

    /// <summary>클라이언트 TLS 검증에 쓸 대상 호스트 이름. <see langword="null"/> 이면 접속 주소.</summary>
    public string? TargetHost { get; set; }

    /// <summary>동시 커넥션(활성 스트림) 상한.</summary>
    /// <remarks>상한을 넘는 스트림은 즉시 중단으로 거부한다. 거부가 붕괴보다 낫다(9.6).</remarks>
    public int MaxConnections { get; set; } = int.MaxValue;

    /// <summary>QUIC 연결 하나가 받는 인바운드 양방향 스트림 상한.</summary>
    /// <remarks>클라이언트가 이 값을 넘겨 열면 QUIC 흐름 제어가 열기를 지연시킨다.</remarks>
    public int MaxStreamsPerConnection { get; set; } = DefaultMaxStreamsPerConnection;

    /// <summary>수신 버퍼(내부 파이프)가 이 크기를 넘으면 스트림에서 더 읽지 않는다.</summary>
    /// <remarks>
    /// 이것이 이 전송의 백프레셔다 — 소비가 멈추면 수신 펌프가 멈추고 QUIC 스트림 흐름
    /// 제어가 상대를 멈춘다. <b>최대 프레임 크기보다 커야 한다</b>(ADR-0007 교착 검사의 입력).
    /// </remarks>
    public long PauseWriterThreshold { get; set; } = DefaultPauseWriterThreshold;

    /// <summary>수신 버퍼가 이 크기 아래로 내려가면 다시 읽는다.</summary>
    public long ResumeWriterThreshold { get; set; } = DefaultResumeWriterThreshold;

    /// <summary>드레인 취소 후 강제 종료가 끝나기를 기다리는 상한.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = DefaultShutdownTimeout;

    /// <summary>설정이 유효한지 검사한다. 시작 시점에 호출된다.</summary>
    /// <param name="requireServerCertificate">서버 전송이면 <see langword="true"/> — 인증서가 필수다.</param>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났을 때.</exception>
    public void Validate(bool requireServerCertificate = false)
    {
        if (string.IsNullOrEmpty(AlpnProtocol))
        {
            throw new InvalidOperationException($"{nameof(AlpnProtocol)} 은 비어 있을 수 없다.");
        }

        // 고정 인스턴스와 원천이 함께 오면 어느 쪽이 진짜인지 모호하다 — TLS 어댑터의
        // ServerCertificate/ServerCertificateSource 상호 배타와 같은 규율(감사 2026-08-18 T-4).
        if (ServerCertificate is not null && ServerCertificateContextSource is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(ServerCertificate)}와 {nameof(ServerCertificateContextSource)}가 함께 지정됐다 — "
                + "고정 인스턴스 또는 원천 중 하나만 쓴다.");
        }

        if (requireServerCertificate && ServerCertificate is null && ServerCertificateContextSource is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ServerCertificate)} 또는 {nameof(ServerCertificateContextSource)} 는 서버 전송의 "
                + "필수 입력이다 — QUIC 은 TLS 없이 서지 않는다. "
                + "자가서명이라도 serverAuth EKU 와 PFX 재로드가 필요하다(ADR-0060).");
        }

        if (MaxConnections <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxConnections)} 는 1 이상이어야 한다: {MaxConnections}");
        }

        if (MaxStreamsPerConnection <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxStreamsPerConnection)} 는 1 이상이어야 한다: {MaxStreamsPerConnection}");
        }

        if (PauseWriterThreshold <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PauseWriterThreshold)} 는 0 보다 커야 한다: {PauseWriterThreshold}");
        }

        if (ResumeWriterThreshold <= 0 || ResumeWriterThreshold > PauseWriterThreshold)
        {
            throw new InvalidOperationException(
                $"{nameof(ResumeWriterThreshold)} 는 0 보다 크고 {nameof(PauseWriterThreshold)} 이하여야 한다: "
                + $"{ResumeWriterThreshold} (pause={PauseWriterThreshold})");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(ShutdownTimeout)} 는 0 보다 커야 한다: {ShutdownTimeout}");
        }
    }

    /// <summary>내부 파이프 옵션을 만든다.</summary>
    internal System.IO.Pipelines.PipeOptions CreatePipeOptions() => new(
        pauseWriterThreshold: PauseWriterThreshold,
        resumeWriterThreshold: ResumeWriterThreshold,
        useSynchronizationContext: false);
}
