using System;
using System.IO.Pipelines;
using ChServerM.Diagnostics;
using ChServerM.Resilience;

namespace ChServerM.Transport.InMemory;

/// <summary>
/// 인메모리 전송의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 인메모리 전송이 <b>무한 버퍼로 동작하면 안 되기 때문</b>이다.
/// 테스트 전송이 프로덕션과 다른 백프레셔 동작을 하면, 그 전송으로 검증한 결과가
/// 아무것도 보장하지 못한다. 임계값을 명시적으로 노출해 실제 TCP 와 같은 조건을 만든다.
/// </para>
/// <para>
/// <b>무제한 큐 금지</b>(CLAUDE.md 9.6)가 여기에도 적용된다. 소비자가 느리면
/// 생산자의 <c>FlushAsync</c>가 실제로 대기해야 한다.
/// </para>
/// </remarks>
public sealed class InMemoryTransportOptions
{
    /// <summary>기본 쓰기 일시정지 임계값. 64 KiB.</summary>
    public const long DefaultPauseWriterThreshold = 64 * 1024;

    /// <summary>기본 쓰기 재개 임계값. 32 KiB.</summary>
    public const long DefaultResumeWriterThreshold = 32 * 1024;

    /// <summary>버퍼가 이 크기를 넘으면 <c>FlushAsync</c>가 대기한다.</summary>
    /// <remarks>
    /// <b>최대 프레임 크기보다 커야 한다.</b> 프레임 디코더는 완전한 프레임이 오기 전에
    /// 아무것도 소비할 수 없으므로, 프레임이 이 값보다 크면 버퍼가 찬 채로 영원히
    /// 벗어나지 못한다. 서버 조립 시점에 검사한다.
    /// </remarks>
    public long PauseWriterThreshold { get; set; } = DefaultPauseWriterThreshold;

    /// <summary>버퍼가 이 크기 아래로 내려가면 <c>FlushAsync</c>가 재개된다.</summary>
    /// <remarks>
    /// <see cref="PauseWriterThreshold"/>보다 <b>충분히 낮아야</b> 한다. 두 값이 가까우면
    /// 임계값 근처에서 정지·재개가 반복되며(chattering) 처리량이 떨어진다.
    /// </remarks>
    public long ResumeWriterThreshold { get; set; } = DefaultResumeWriterThreshold;

    /// <summary>동시에 허용할 최대 커넥션 수.</summary>
    /// <remarks>
    /// 상한을 넘는 연결 시도는 거부된다. <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).
    /// </remarks>
    public int MaxConnections { get; set; } = int.MaxValue;

    /// <summary>신규 커넥션 동적 수용 제어(Phase 10, T-14). <see langword="null"/>이면 정적 상한만 적용.</summary>
    /// <remarks>
    /// <see cref="MaxConnections"/>(정적 하드 상한)를 통과한 뒤에만 물어본다. 거부하면
    /// <c>Accept</c> 가 예외로 실패한다(인메모리는 통지 소켓이 없다). 참조 구현:
    /// <c>ChServerM.Hosting.ConnectionRateAdmissionControl</c>.
    /// </remarks>
    public IAdmissionControl? AdmissionControl { get; set; }

    /// <summary>커넥션 거부를 관측할 메트릭 싱크(Phase 11). <see langword="null"/>이면 기록하지 않는다.</summary>
    /// <remarks>
    /// 거부된 커넥션은 핸들러에 닿지 않으므로 거부(<see cref="MetricNames.ConnectionsRejected"/>)는
    /// 전송이 직접 방출한다 — 정적 상한 거부와 동적 수용 거부 모두 관측된다.
    /// </remarks>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>정상 종료 시 남은 송신 데이터의 드레인을 기다리는 최대 시간.</summary>
    /// <remarks>
    /// <b>상한 없는 대기는 종료를 영원히 막는다.</b> 상대가 읽지 않으면 백프레셔로
    /// 플러시가 끝나지 않는데, 그 대기에 상한이 없으면 서버 전체의 종료가 그 커넥션
    /// 하나에 볼모로 잡힌다 — 실제로 발견된 결함이다(2026-08-04 감사 H3).
    /// TCP 전송의 종료 상한과 같은 역할이다.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
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
    /// <returns>파이프 설정.</returns>
    /// <remarks>
    /// <c>useSynchronizationContext: false</c> 로 고정한다. 동기화 컨텍스트를 타면
    /// 완료 콜백이 예상치 못한 스레드로 돌아가고, 실행 모델의 파티션 고정이 깨진다.
    /// </remarks>
    internal PipeOptions CreatePipeOptions() =>
        new(pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            useSynchronizationContext: false);
}
