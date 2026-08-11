using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

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

    /// <summary>서버 인증서. <b>서버 전송의 필수 입력이다</b> — QUIC 은 TLS 없이 서지 않는다.</summary>
    /// <remarks>클라이언트 전송에서는 무시된다.</remarks>
    public X509Certificate2? ServerCertificate { get; set; }

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

        if (requireServerCertificate && ServerCertificate is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ServerCertificate)} 는 서버 전송의 필수 입력이다 — QUIC 은 TLS 없이 서지 않는다. "
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
