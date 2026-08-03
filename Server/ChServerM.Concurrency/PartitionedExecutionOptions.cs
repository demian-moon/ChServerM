using System;

namespace ChServerM.Concurrency;

/// <summary>
/// 키 기반 파티션 실행 모델의 설정 (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 스레드 수와 큐 깊이는 <b>반드시 옵션이고 반드시 상한이 있어야 한다</b>
/// (CLAUDE.md 9.5, 9.6). 레거시는 송신·수신 각각 <c>ProcessorCount × 팩터</c>개의
/// 전용 스레드를 만들었고, 16코어에 팩터 2.0이면 <b>전용 스레드 64개</b>가 됐다.
/// 그 숫자가 어디에도 설정으로 드러나 있지 않았다.
/// </para>
/// </remarks>
public sealed class PartitionedExecutionOptions
{
    /// <summary>파티션 개수의 절대 상한.</summary>
    /// <remarks>
    /// 설정 실수로 수천 개의 전용 스레드를 만드는 것을 막는다. 파티션 하나가
    /// 스레드 하나이므로, 이 값을 넘겨야 한다면 설계를 다시 봐야 한다는 신호다.
    /// </remarks>
    public const int AbsoluteMaxPartitionCount = 512;

    /// <summary>기본 외부 작업 큐 용량(파티션당).</summary>
    public const int DefaultQueueCapacity = 4096;

    /// <summary>기본 종료 대기 시간.</summary>
    public static TimeSpan DefaultShutdownTimeout => TimeSpan.FromSeconds(5);

    /// <summary>파티션 개수.</summary>
    /// <remarks>
    /// <para>
    /// 기본값은 <see cref="Environment.ProcessorCount"/>다. 파티션 하나가 전용 스레드
    /// 하나이므로, 코어 수보다 많이 만들면 컨텍스트 스위칭만 늘고 처리량은 오히려 떨어진다.
    /// </para>
    /// <para>
    /// <b>2의 거듭제곱일 필요는 없다.</b> <see cref="Identity.PartitionKey.ToIndex"/> 가
    /// 곱셈-시프트로 축소하므로 임의의 개수를 쓸 수 있다.
    /// </para>
    /// <para>
    /// <b>실행 중에 바꿀 수 없다.</b> 개수가 바뀌면 키→파티션 매핑이 통째로 달라져
    /// 순서 보장이 사라진다. 재해싱 전략은 Phase 15 에서 정한다.
    /// </para>
    /// </remarks>
    public int PartitionCount { get; set; } = Environment.ProcessorCount;

    /// <summary>파티션당 외부 작업 큐의 용량.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Execution.IExecutionPartition.TryPost{TWork}"/> 로 들어오는 작업에만
    /// 적용된다. 이 수를 넘으면 <see langword="false"/>를 반환한다 —
    /// <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).
    /// </para>
    /// <para>
    /// 스케줄러를 통해 들어오는 연속(continuation)에는 적용되지 않는다.
    /// 그 이유는 <see cref="ExecutionPartition"/> 문서에 있다.
    /// </para>
    /// </remarks>
    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    /// <summary>파티션 스레드 이름의 접두사.</summary>
    /// <remarks>
    /// 디버거·프로파일러·덤프에서 <b>어느 스레드가 무엇인지</b> 보이게 한다.
    /// 이름 없는 스레드 64개를 덤프에서 구분하는 것은 사실상 불가능하다.
    /// </remarks>
    public string ThreadNamePrefix { get; set; } = "chsm-partition";

    /// <summary>파티션 스레드의 우선순위.</summary>
    public System.Threading.ThreadPriority ThreadPriority { get; set; } =
        System.Threading.ThreadPriority.Normal;

    /// <summary>종료 시 파티션 스레드가 끝나기를 기다리는 최대 시간.</summary>
    /// <remarks><b>상한 없는 대기는 종료를 영원히 막는다.</b></remarks>
    public TimeSpan ShutdownTimeout { get; set; } = DefaultShutdownTimeout;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    /// <remarks>
    /// <b>시작 시점에 검증한다.</b> 레거시는 static 초기자에서 다른 static 을 읽어
    /// 파티션 수가 0 이 될 수 있었고, 그러면 첫 나눗셈에서
    /// <see cref="DivideByZeroException"/> 이 났다 (CLAUDE.md 9.1).
    /// </remarks>
    public void Validate()
    {
        if (PartitionCount <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PartitionCount)}는 1 이상이어야 한다. 현재 값: {PartitionCount}");
        }

        if (PartitionCount > AbsoluteMaxPartitionCount)
        {
            throw new InvalidOperationException(
                $"{nameof(PartitionCount)}({PartitionCount})가 절대 상한" +
                $"({AbsoluteMaxPartitionCount})을 넘는다. 파티션 하나가 전용 스레드 하나다.");
        }

        if (QueueCapacity <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(QueueCapacity)}는 1 이상이어야 한다. 0 은 무제한이 아니라 즉시 거부를 뜻한다.");
        }

        if (string.IsNullOrWhiteSpace(ThreadNamePrefix))
        {
            throw new InvalidOperationException($"{nameof(ThreadNamePrefix)}는 비어 있을 수 없다.");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(ShutdownTimeout)}는 0보다 커야 한다. 현재 값: {ShutdownTimeout}");
        }
    }
}
