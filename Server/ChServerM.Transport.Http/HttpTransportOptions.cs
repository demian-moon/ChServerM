using System;

namespace ChServerM.Transport.Http;

/// <summary>
/// HTTP 전송의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> TCP 옵션과 같은 원칙이다 — 여기 있는 값들은 <b>커넥션(스트림) 수를
/// 곱하면 자원량이 되는</b> 숫자이거나, 틀리면 조용히 교착하는 조합 검사의 입력이다.
/// 상수로 박아두면 "1만 스트림에서 메모리가 얼마인가"를 계산할 수 없게 된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 전송 생성 전에 단일 스레드에서 채우고 넘긴다. 전송이 값을 복사하므로
/// 생성 후의 변경은 반영되지 않는다.
/// </para>
/// </remarks>
public sealed class HttpTransportOptions
{
    /// <summary>기본 프레임 스트림 경로.</summary>
    public const string DefaultPath = "/chsm";

    /// <summary>기본 스트림 수신 윈도. 1 MiB.</summary>
    /// <remarks>
    /// HTTP/2 흐름 제어 윈도가 <b>이 전송의 백프레셔</b>다(TCP 의
    /// <c>PauseWriterThreshold</c> 에 대응). 소비되지 않은 바이트가 이 값에 닿으면
    /// 상대의 쓰기가 멈춘다.
    /// </remarks>
    public const int DefaultStreamReceiveWindowSize = 1024 * 1024;

    /// <summary>기본 강제 종료 상한. 5초.</summary>
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>프레임 스트림을 받을 요청 경로.</summary>
    /// <remarks>
    /// 이 경로에 대한 <c>POST</c> 만 커넥션으로 수용한다. 다른 경로·메서드는 404/405 다 —
    /// 같은 포트에 다른 것을 얹지 않는다(관측·헬스는 별도 admin 포트,
    /// <c>ChServerM.Diagnostics.Http</c> 참조).
    /// </remarks>
    public string Path { get; set; } = DefaultPath;

    /// <summary>동시 커넥션(활성 스트림) 상한.</summary>
    /// <remarks>
    /// 상한을 넘는 스트림은 <c>503</c> 으로 거부한다. <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).
    /// </remarks>
    public int MaxConnections { get; set; } = int.MaxValue;

    /// <summary>스트림 하나의 수신 흐름 제어 윈도(바이트).</summary>
    /// <remarks>
    /// <para>
    /// <b>최대 프레임 크기보다 커야 한다.</b> 프레임 디코더는 완전한 프레임이 오기 전에
    /// 아무것도 소비할 수 없으므로, 프레임이 이 값보다 크면 윈도가 소진된 채로 영원히
    /// 벗어나지 못한다 — TCP 의 수신 버퍼 교착과 정확히 같은 기전이고, 그래서 이 값이
    /// <see cref="ChServerM.Transports.ITransportBufferLimits"/> 로 노출되어 조립 시점에 검사된다.
    /// </para>
    /// <para>HTTP/2 규격상 65,535 미만으로 내릴 수 없다.</para>
    /// </remarks>
    public int StreamReceiveWindowSize { get; set; } = DefaultStreamReceiveWindowSize;

    /// <summary>드레인 취소 후 강제 종료가 끝나기를 기다리는 상한.</summary>
    /// <remarks>
    /// 취소 토큰을 무시하는 사용자 핸들러가 서버 종료를 볼모로 잡지 않게 하는 마지막
    /// 안전망이다(TCP·인메모리 전송과 같은 장치).
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

        // HTTP/2 규격(RFC 9113) 의 초기 윈도 하한. Kestrel 도 이 값 미만을 거부한다.
        if (StreamReceiveWindowSize < 65_535)
        {
            throw new InvalidOperationException(
                $"{nameof(StreamReceiveWindowSize)} 는 65535 이상이어야 한다(HTTP/2 규격): {StreamReceiveWindowSize}");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(ShutdownTimeout)} 는 0 보다 커야 한다: {ShutdownTimeout}");
        }
    }
}
