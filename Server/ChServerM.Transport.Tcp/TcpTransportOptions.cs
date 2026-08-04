using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace ChServerM.Transport.Tcp;

/// <summary>
/// TCP 전송의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 여기 있는 값들은 전부 <b>커넥션 수를 곱하면 자원량이 되는</b> 숫자다.
/// 상수로 박아두면 워크로드가 바뀔 때 코드를 고쳐야 하고, 무엇보다
/// "1만 접속에서 메모리가 얼마인가"를 계산할 수 없게 된다.
/// </para>
/// <para>
/// 레거시는 커넥션당 <b>64KB 고정 송신 버퍼</b>를 상수로 잡았다. 1만 접속이면
/// 그것만으로 640MB다. 그 숫자가 코드 어디에도 설정으로 드러나 있지 않았다.
/// </para>
/// </remarks>
public sealed class TcpTransportOptions
{
    /// <summary>기본 수락 대기 큐 길이.</summary>
    public const int DefaultBacklog = 512;

    /// <summary>기본 쓰기 일시정지 임계값. 64 KiB.</summary>
    public const long DefaultPauseWriterThreshold = 64 * 1024;

    /// <summary>기본 쓰기 재개 임계값. 32 KiB.</summary>
    public const long DefaultResumeWriterThreshold = 32 * 1024;

    /// <summary>기본 최소 수신 버퍼 요청 크기. 4 KiB.</summary>
    public const int DefaultMinReceiveBufferSize = 4 * 1024;

    /// <summary>수락 대기 큐 길이.</summary>
    /// <remarks>
    /// 짧으면 연결 폭주 시 커널이 SYN 을 버린다. 클라이언트에게는 타임아웃으로 보이므로
    /// 원인 파악이 어렵다. 넉넉하게 잡는 편이 낫다.
    /// </remarks>
    public int Backlog { get; set; } = DefaultBacklog;

    /// <summary>Nagle 알고리즘을 끈다.</summary>
    /// <remarks>
    /// 기본값 <see langword="true"/>(= Nagle 비활성). 작은 프레임을 자주 주고받는
    /// 워크로드에서 Nagle 은 최대 40ms 의 지연을 더한다. 처리량이 지연보다 중요한
    /// 워크로드라면 끈다.
    /// </remarks>
    public bool NoDelay { get; set; } = true;

    /// <summary>TCP keep-alive 를 켠다.</summary>
    /// <remarks>
    /// <para>
    /// <b>이식 가능한 소켓 옵션만 쓴다.</b> 레거시는 <c>IOControlCode.KeepAliveValues</c> 로
    /// keep-alive 간격을 설정했는데, 그것은 <b>Windows 전용</b>이라 리눅스에서 던진다 —
    /// 레거시의 크로스 플랫폼 차단 요인 중 하나였다.
    /// </para>
    /// <para>
    /// 세밀한 간격 제어가 필요하면 애플리케이션 레벨 하트비트를 쓴다
    /// (<see cref="ChServerM.Identity.FrameworkMessageIds.Heartbeat"/>). 그쪽이 이식성도 좋고
    /// 애플리케이션이 살아 있는지까지 확인한다 — TCP keep-alive 는 커널만 확인한다.
    /// </para>
    /// </remarks>
    public bool EnableKeepAlive { get; set; }

    /// <summary>수신 버퍼가 이 크기를 넘으면 소켓에서 더 읽지 않는다.</summary>
    /// <remarks>
    /// <para>
    /// 이것이 <b>진짜 백프레셔</b>다. 애플리케이션이 느리면 커널 수신 버퍼가 차고,
    /// 결국 TCP 윈도가 0이 되어 상대가 보내지 못한다. 무제한이면 그 신호가 사라지고
    /// 메모리로 대신 갚게 된다.
    /// </para>
    /// <para>
    /// <b>최대 프레임 크기보다 커야 한다.</b> 프레임 디코더는 완전한 프레임이 오기 전에
    /// 아무것도 소비할 수 없으므로, 프레임이 이 값보다 크면 버퍼가 찬 채로 영원히
    /// 벗어나지 못한다. TCP 는 커널 소켓 버퍼가 여유분을 흡수해 <b>우연히 통과할 수도</b>
    /// 있는데, 그것은 운이지 보장이 아니다. 서버 조립 시점에 검사한다.
    /// </para>
    /// </remarks>
    public long PauseWriterThreshold { get; set; } = DefaultPauseWriterThreshold;

    /// <summary>수신 버퍼가 이 크기 아래로 내려가면 다시 읽는다.</summary>
    public long ResumeWriterThreshold { get; set; } = DefaultResumeWriterThreshold;

    /// <summary>한 번의 수신에서 요청할 최소 버퍼 크기.</summary>
    /// <remarks>
    /// 너무 작으면 시스템 콜이 늘고, 너무 크면 커넥션당 메모리가 는다.
    /// <see cref="WaitForDataBeforeAllocating"/>이 켜져 있으면 <b>데이터가 있을 때만</b>
    /// 이 크기를 잡으므로 유휴 커넥션에는 영향이 없다.
    /// </remarks>
    public int MinReceiveBufferSize { get; set; } = DefaultMinReceiveBufferSize;

    /// <summary>데이터가 도착한 뒤에 수신 버퍼를 잡는다.</summary>
    /// <remarks>
    /// <para>
    /// 기본값 <see langword="true"/>. 0바이트 수신으로 먼저 대기했다가, 읽을 것이
    /// 생겼을 때만 버퍼를 요청한다.
    /// </para>
    /// <para>
    /// <b>왜 중요한가.</b> 상시 연결 워크로드에서는 대부분의 커넥션이 대부분의 시간 동안
    /// 조용하다. 그런데도 각자 수신 버퍼를 붙들고 있으면 그것이 곧 메모리다 —
    /// 1만 접속 × 4KB = 40MB 가 아무 일도 하지 않으면서 상주한다.
    /// 레거시는 이것의 16배(커넥션당 64KB)를 상수로 잡았다.
    /// </para>
    /// <para>
    /// 대신 데이터 도착마다 시스템 콜이 하나 더 든다. 처리량이 극단적으로 중요하고
    /// 커넥션 수가 적다면 끈다.
    /// </para>
    /// </remarks>
    public bool WaitForDataBeforeAllocating { get; set; } = true;

    /// <summary>동시에 허용할 최대 커넥션 수.</summary>
    /// <remarks><b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).</remarks>
    public int MaxConnections { get; set; } = int.MaxValue;

    /// <summary>정상 종료 시 상대의 응답을 기다리는 최대 시간.</summary>
    /// <remarks>
    /// 2단 종료의 상한이다. FIN 을 보낸 뒤 상대가 답하지 않으면 이 시간이 지나고
    /// 강제로 끊는다. <b>상한 없는 대기는 종료를 영원히 막는다.</b>
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>이 시간 동안 수신·송신이 모두 없으면 커넥션을 끊는다. 기본 비활성.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="TimeSpan.Zero"/>(기본값)는 비활성이다. half-open 커넥션(상대가 전원
    /// 차단·케이블 단절로 사라져 FIN 도 RST 도 없는 상태)은 이것 없이는 영원히 목록에
    /// 남아 자원을 붙든다.
    /// </para>
    /// <para>
    /// <b>구현은 전송당 스윕 타이머 하나다</b>(주기 = 타임아웃/4, 최소 1초) — 커넥션당
    /// 타이머는 만들지 않는다(CLAUDE.md 9.5). 판정 해상도가 스윕 주기만큼 거칠다는
    /// 뜻이기도 하다: 초과 후 최대 스윕 주기만큼 늦게 끊길 수 있다. 계층적 타이밍 휠은
    /// 타이머 시스템(Phase 17)과 함께 온다.
    /// </para>
    /// <para>
    /// 애플리케이션 하트비트(<see cref="ChServerM.Identity.FrameworkMessageIds.Heartbeat"/>)를
    /// 쓰는 워크로드는 타임아웃을 하트비트 주기의 2~3배로 잡는다.
    /// </para>
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; }

    /// <summary>커널 수신 버퍼 크기(SO_RCVBUF). <see langword="null"/>이면 OS 기본값.</summary>
    /// <remarks>커넥션 수를 곱하면 커널 메모리가 된다 — 기본값을 바꿀 때는 그 곱을 계산한다.</remarks>
    public int? SocketReceiveBufferSize { get; set; }

    /// <summary>커널 송신 버퍼 크기(SO_SNDBUF). <see langword="null"/>이면 OS 기본값.</summary>
    public int? SocketSendBufferSize { get; set; }

    /// <summary>
    /// 닫을 때 미전송 데이터를 기다리는 시간(초). <see langword="null"/>이면 OS 기본값,
    /// <c>0</c>이면 즉시 RST.
    /// </summary>
    /// <remarks><c>0</c>(즉시 RST)은 TIME_WAIT 를 피하는 대신 미전송 데이터를 버린다 — 부하 도구용이다.</remarks>
    public int? LingerSeconds { get; set; }

    /// <summary>수락 소켓에 SO_REUSEADDR 를 켠다. 기본 끔.</summary>
    /// <remarks>
    /// 재시작 시 TIME_WAIT 포트를 즉시 재바인드할 수 있게 한다. Windows 에서는 의미가
    /// 달라(활성 바인딩 탈취 가능) 기본을 끔으로 둔다 — 필요한 배포 환경에서만 켠다.
    /// </remarks>
    public bool ReuseAddress { get; set; }

    /// <summary>
    /// 동시 접속 상한으로 거부할 때 닫기 전에 보낼 통지 프레임의 원시 바이트.
    /// 비어 있으면(기본) 통지 없이 닫는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 그냥 닫으면 클라이언트는 RST 하나만 보고 <b>"서버가 꽉 찼다"와 "네트워크가
    /// 끊겼다"를 구분할 수 없다</b> — 재시도 정책을 세울 수 없다(2026-08-04 감사).
    /// </para>
    /// <para>
    /// <b>원시 바이트인 이유.</b> 전송은 프레이밍을 모른다(축 독립). 조립하는 쪽이
    /// 자기 인코더로 <see cref="ChServerM.Identity.FrameworkMessageIds.ConnectionRejected"/>
    /// 프레임을 만들어 넣는다. 전송은 최선 노력으로 이 바이트를 보낸 뒤 닫는다 —
    /// 전송 실패는 무시한다(거부 경로에서 기다리면 그것이 곧 공격 표면이다).
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<byte> RejectionNotice { get; set; }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (Backlog <= 0)
        {
            throw new InvalidOperationException($"{nameof(Backlog)}는 1 이상이어야 한다. 현재 값: {Backlog}");
        }

        if (PauseWriterThreshold <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PauseWriterThreshold)}는 1 이상이어야 한다. 0 은 무제한이 아니라 즉시 정지를 뜻한다.");
        }

        if (ResumeWriterThreshold <= 0 || ResumeWriterThreshold > PauseWriterThreshold)
        {
            throw new InvalidOperationException(
                $"{nameof(ResumeWriterThreshold)}({ResumeWriterThreshold})는 1 이상이면서 " +
                $"{nameof(PauseWriterThreshold)}({PauseWriterThreshold}) 이하여야 한다.");
        }

        if (MinReceiveBufferSize <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MinReceiveBufferSize)}는 1 이상이어야 한다. 현재 값: {MinReceiveBufferSize}");
        }

        if (MaxConnections <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxConnections)}는 1 이상이어야 한다. 현재 값: {MaxConnections}");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(ShutdownTimeout)}는 0보다 커야 한다. 현재 값: {ShutdownTimeout}");
        }

        if (IdleTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(IdleTimeout)}는 음수일 수 없다. 비활성은 0(기본값)이다. 현재 값: {IdleTimeout}");
        }

        if (SocketReceiveBufferSize is <= 0 || SocketSendBufferSize is <= 0)
        {
            throw new InvalidOperationException(
                "소켓 버퍼 크기는 1 이상이어야 한다. OS 기본값을 쓰려면 null 로 둔다.");
        }

        if (LingerSeconds is < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(LingerSeconds)}는 음수일 수 없다. 현재 값: {LingerSeconds}");
        }
    }

    /// <summary>현재 값을 복사한 스냅샷을 만든다.</summary>
    /// <remarks>
    /// 전송은 생성 시점에 이 스냅샷을 보관한다. 라이브 참조를 들고 있으면 <c>Build()</c>
    /// 이후 사용자가 값을 바꿨을 때 <b>조립 검사(ADR-0007)를 통과한 적 없는 조합</b>으로
    /// 커넥션이 만들어진다 — 검사가 사후 무효화되는 구멍이다(2026-08-04 감사).
    /// <c>FramingOptions</c> 가 값을 복사하는 것과 같은 규약이다.
    /// </remarks>
    internal TcpTransportOptions Snapshot() => new()
    {
        Backlog = Backlog,
        NoDelay = NoDelay,
        EnableKeepAlive = EnableKeepAlive,
        PauseWriterThreshold = PauseWriterThreshold,
        ResumeWriterThreshold = ResumeWriterThreshold,
        MinReceiveBufferSize = MinReceiveBufferSize,
        WaitForDataBeforeAllocating = WaitForDataBeforeAllocating,
        MaxConnections = MaxConnections,
        ShutdownTimeout = ShutdownTimeout,
        IdleTimeout = IdleTimeout,
        SocketReceiveBufferSize = SocketReceiveBufferSize,
        SocketSendBufferSize = SocketSendBufferSize,
        LingerSeconds = LingerSeconds,
        ReuseAddress = ReuseAddress,
        RejectionNotice = RejectionNotice,
    };

    /// <summary>이 설정으로 <see cref="PipeOptions"/>를 만든다.</summary>
    /// <remarks>
    /// <c>useSynchronizationContext: false</c> 로 고정한다. 동기화 컨텍스트를 타면
    /// 완료 콜백이 예상치 못한 스레드로 돌아가고, 실행 모델의 파티션 고정이 깨진다.
    /// </remarks>
    internal PipeOptions CreatePipeOptions() =>
        new(pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            useSynchronizationContext: false);

    /// <summary>수락된 소켓에 설정을 적용한다.</summary>
    internal void Apply(Socket socket)
    {
        socket.NoDelay = NoDelay;

        if (EnableKeepAlive)
        {
            // 이식 가능한 옵션만 쓴다. 간격 제어는 하지 않는다 — 위 remarks 참조.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, optionValue: true);
        }

        // 아래 셋은 전부 표준 소켓 옵션이라 크로스 플랫폼이다 — IOControlCode 류
        // (Windows 전용, 레거시의 이식 차단 요인)는 쓰지 않는다.
        if (SocketReceiveBufferSize is { } receiveBuffer)
        {
            socket.ReceiveBufferSize = receiveBuffer;
        }

        if (SocketSendBufferSize is { } sendBuffer)
        {
            socket.SendBufferSize = sendBuffer;
        }

        if (LingerSeconds is { } linger)
        {
            socket.LingerState = new LingerOption(enable: true, seconds: linger);
        }
    }
}
