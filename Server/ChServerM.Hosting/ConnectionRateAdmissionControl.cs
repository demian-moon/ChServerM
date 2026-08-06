using System;
using System.Net;
using System.Threading;
using ChServerM.Resilience;

namespace ChServerM.Hosting;

/// <summary>
/// 신규 연결 속도를 토큰 버킷으로 제한하는 <see cref="IAdmissionControl"/> (Phase 10, T-16).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 정적 <c>MaxConnections</c> 는 정상 상태 상한이라, 그 상한 안에서
/// 초당 수만 건의 연결·해제를 반복하는 폭주(SYN 폭주·재접속 스톰)를 못 막는다 — accept
/// 루프와 핸드셰이크 CPU 가 그대로 소모된다. 이 구현은 <b>신규 연결 속도</b>에 상한을 둬
/// 그 공격 표면을 닫는다.
/// </para>
/// <para>
/// <b>토큰 버킷.</b> 초당 <c>PermitsPerSecond</c> 개씩 채워지고 <c>BurstCapacity</c> 까지
/// 쌓인다. 연결마다 토큰 1개 소비, 없으면 거부. 버스트 용량이 배포 직후 정상 러시를
/// 흡수하고, 지속 속도가 폭주를 막는다. 슬라이딩 윈도우보다 단순하고 메모리가 상수다.
/// </para>
/// <para>
/// <b>⚠ 락을 쓴다 — 근거.</b> 리필(경과 시간 기반)과 소비가 원자적이어야 하는데,
/// 이 경로는 <b>커넥션당 1회</b>(프레임당이 아니다)라 핫패스가 아니다(CLAUDE.md 9.1 은
/// 핫패스 락을 금한다). 저빈도에서 락은 CAS 재시도 루프보다 명백히 정확하고 읽기 쉽다.
/// TCP 수락 루프는 단일 스레드지만 InMemory 다중 게시자·인스턴스 공유를 위해 필요하다.
/// </para>
/// <para>
/// <b>IP 를 보지 않는다(전역 속도).</b> <c>remoteEndPoint</c> 인자는 무시한다 —
/// IP별 제한은 상태(IP→버킷 맵)와 카디널리티·정리 정책이 필요한 별도 구현의 몫이다.
/// 전역 속도가 프로세스 CPU 를 지키는 1차 방어다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다. 여러 전송이 공유할 수 있다.</para>
/// </remarks>
public sealed class ConnectionRateAdmissionControl : IAdmissionControl
{
    private readonly double _permitsPerSecond;
    private readonly double _burstCapacity;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private double _tokens;
    private long _lastRefillTimestamp;

    /// <summary>설정을 검증하고 버킷을 가득 채워 시작한다.</summary>
    /// <param name="options">토큰 버킷 파라미터.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다. 생략하면 시스템 시계.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    /// <remarks>버킷을 가득 채워 시작하는 이유: 서버 부팅 직후 정상 접속 러시를 즉시 흡수하기 위해서다.</remarks>
    public ConnectionRateAdmissionControl(
        ConnectionRateAdmissionControlOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _permitsPerSecond = options.PermitsPerSecond;
        _burstCapacity = options.BurstCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tokens = _burstCapacity;
        _lastRefillTimestamp = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint)
    {
        lock (_gate)
        {
            long now = _timeProvider.GetTimestamp();
            double elapsedSeconds = _timeProvider.GetElapsedTime(_lastRefillTimestamp, now).TotalSeconds;

            // 경과 시간만큼 토큰을 채운다(상한 = 버스트 용량). 음수 경과(시계 이상)는 0으로 본다.
            if (elapsedSeconds > 0)
            {
                _tokens = Math.Min(_burstCapacity, _tokens + (elapsedSeconds * _permitsPerSecond));
                _lastRefillTimestamp = now;
            }

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return AdmissionDecision.Admit();
            }

            return AdmissionDecision.Reject("connection rate exceeded");
        }
    }
}
