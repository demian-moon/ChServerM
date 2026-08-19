using System;
using System.Collections.Generic;
using ChServerM.Diagnostics;
using ChServerM.Resilience;

namespace ChServerM.Transport.WebSocket;

/// <summary>
/// WebSocket 전송의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> TCP·HTTP 옵션과 같은 원칙이다 — 커넥션 수를 곱하면 자원량이 되는
/// 숫자와, 틀리면 조용히 교착하는 조합 검사의 입력을 상수가 아니라 설정으로 드러낸다.
/// </para>
/// <para>
/// <b>서브프로토콜(Sec-WebSocket-Protocol)은 지원하지 않는다.</b> 이 전송은 프레이밍 축이
/// 와이어 형식을 소유하므로 WebSocket 수준의 프로토콜 협상이 설 자리가 없다. 서버는
/// 요청의 서브프로토콜을 에코하지 않으며, 규격(RFC 6455 4.1)상 서브프로토콜을 요구한
/// 클라이언트는 그 응답에 연결을 실패시켜야 한다 — 서브프로토콜 없이 접속한다
/// (감사 2026-08-18 T-6).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 전송 생성 전에 단일 스레드에서 채우고 넘긴다. 전송이 값을 복사하므로
/// 생성 후의 변경은 반영되지 않는다.
/// </para>
/// </remarks>
public sealed class WebSocketTransportOptions
{
    /// <summary>기본 업그레이드 경로.</summary>
    public const string DefaultPath = "/chsm";

    /// <summary>기본 수신 일시정지 임계값. 64 KiB.</summary>
    public const long DefaultPauseWriterThreshold = 64 * 1024;

    /// <summary>기본 수신 재개 임계값. 32 KiB.</summary>
    public const long DefaultResumeWriterThreshold = 32 * 1024;

    /// <summary>기본 강제 종료 상한. 5초.</summary>
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>WebSocket 업그레이드를 받을 요청 경로.</summary>
    /// <remarks>
    /// 이 경로에 대한 업그레이드 <c>GET</c> 만 커넥션으로 수용한다. 다른 경로·비업그레이드
    /// 요청은 404/426 이다 — 같은 포트에 다른 것을 얹지 않는다(HTTP 전송과 같은 판단).
    /// </remarks>
    public string Path { get; set; } = DefaultPath;

    /// <summary>동시 커넥션 상한.</summary>
    /// <remarks>상한을 넘는 업그레이드는 <c>503</c> 으로 거부한다. 거부가 붕괴보다 낫다(9.6).</remarks>
    public int MaxConnections { get; set; } = int.MaxValue;

    /// <summary>업그레이드를 허용할 <c>Origin</c> 화이트리스트.
    /// <see langword="null"/>(기본)이면 검사하지 않는다.</summary>
    /// <remarks>
    /// <para>
    /// <b>CSWSH(Cross-Site WebSocket Hijacking) 방어다.</b> 이 전송의 존재 이유가 브라우저
    /// 통과인데, Origin 을 검증하지 않으면 임의 웹사이트의 JS 가 방문자의 브라우저에서 이
    /// 서버로 WebSocket 을 열 수 있다(감사 2026-08-18 T-6). 브라우저 배포라면 반드시 지정한다.
    /// </para>
    /// <para>
    /// 항목은 <c>Origin</c> 헤더 값 전체(스킴+호스트+포트, 예: <c>https://game.example.com</c>)와
    /// <b>Ordinal 정확 일치</b>로 비교한다 — 대소문자·후행 슬래시가 다르면 불일치다. 불일치는
    /// <c>403</c> 으로 거부한다. <b><c>Origin</c> 헤더가 없는 요청(비브라우저 클라이언트)은
    /// 통과한다</b> — Origin 검사는 브라우저에 대한 방어이고, 헤더를 위조할 수 있는 비브라우저
    /// 공격자에게는 애초에 방어가 아니다(토큰 인증의 몫이다).
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? AllowedOrigins { get; set; }

    /// <summary>신규 커넥션 동적 수용 제어. <see langword="null"/>이면 정적 상한만 적용.</summary>
    /// <remarks>
    /// <see cref="MaxConnections"/>(정적 하드 상한)를 통과한 뒤에만 물어본다. 거부하면
    /// <c>503</c> 으로 응답한다 — TCP 전송과 같은 배선(감사 2026-08-18 T-5). 참조 구현:
    /// <c>ChServerM.Hosting.ConnectionRateAdmissionControl</c>(토큰 버킷).
    /// </remarks>
    public IAdmissionControl? AdmissionControl { get; set; }

    /// <summary>커넥션 거부를 관측할 메트릭 싱크. <see langword="null"/>이면 기록하지 않는다.</summary>
    /// <remarks>
    /// 거부된 업그레이드는 핸들러에 닿지 않으므로 거부(<see cref="MetricNames.ConnectionsRejected"/>)는
    /// 전송이 직접 방출한다 — 정적 상한·동적 수용·드레인 거부 모두 관측된다(감사 2026-08-18 T-5,
    /// CLAUDE.md 9.6 "드롭 수를 메트릭으로 노출한다").
    /// </remarks>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>수신 버퍼(내부 파이프)가 이 크기를 넘으면 소켓에서 더 읽지 않는다.</summary>
    /// <remarks>
    /// <para>
    /// 이것이 이 전송의 <b>백프레셔</b>다 — 소비되지 않은 바이트가 임계값에 닿으면 수신
    /// 펌프가 멈추고, TCP 흐름 제어가 상대의 쓰기를 멈춘다.
    /// </para>
    /// <para>
    /// <b>최대 프레임 크기보다 커야 한다.</b> 프레임 디코더는 완전한 프레임이 오기 전에
    /// 아무것도 소비할 수 없으므로, 어긋난 조합은 그 크기의 프레임에서 조용히 교착한다 —
    /// <see cref="ChServerM.Transports.ITransportBufferLimits"/> 로 노출되어 조립 시점에
    /// 검사된다(ADR-0007).
    /// </para>
    /// </remarks>
    public long PauseWriterThreshold { get; set; } = DefaultPauseWriterThreshold;

    /// <summary>수신 버퍼가 이 크기 아래로 내려가면 다시 읽는다.</summary>
    public long ResumeWriterThreshold { get; set; } = DefaultResumeWriterThreshold;

    /// <summary>드레인 취소 후 강제 종료가 끝나기를 기다리는 상한.</summary>
    /// <remarks>
    /// 취소 토큰을 무시하는 사용자 핸들러가 서버 종료를 볼모로 잡지 않게 하는 마지막
    /// 안전망이다(TCP·인메모리·HTTP 전송과 같은 장치).
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = DefaultShutdownTimeout;

    /// <summary>설정이 유효한지 검사한다. 시작 시점에 호출된다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났을 때.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Path) || Path[0] != '/')
        {
            throw new InvalidOperationException(
                $"{nameof(Path)} 는 '/' 로 시작하는 절대 경로여야 한다: '{Path}'");
        }

        if (MaxConnections <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxConnections)} 는 1 이상이어야 한다: {MaxConnections}");
        }

        if (AllowedOrigins is { } allowedOrigins)
        {
            foreach (string origin in allowedOrigins)
            {
                if (string.IsNullOrWhiteSpace(origin))
                {
                    throw new InvalidOperationException(
                        $"{nameof(AllowedOrigins)} 에 비어 있는 항목이 있다 — 항목은 " +
                        "Origin 헤더 값 전체(예: https://game.example.com)여야 한다.");
                }
            }
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
