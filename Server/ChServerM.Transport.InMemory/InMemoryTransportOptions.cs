using System;
using System.IO.Pipelines;

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
