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
    /// 이것이 <b>진짜 백프레셔</b>다. 애플리케이션이 느리면 커널 수신 버퍼가 차고,
    /// 결국 TCP 윈도가 0이 되어 상대가 보내지 못한다. 무제한이면 그 신호가 사라지고
    /// 메모리로 대신 갚게 된다.
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
    }

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
    }
}
