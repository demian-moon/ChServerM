using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChServerM.Concurrency;

/// <summary>
/// 태스크를 특정 파티션의 스레드에 고정하는 스케줄러.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이것이 ADR-0005 의 주 경로다.</b> 커넥션의 읽기 루프를 이 스케줄러에서
/// 시작하면 그 루프와 <b>모든 <c>await</c> 연속</b>이 같은 파티션 스레드에서 이어진다.
/// 그러면 프레임마다 큐를 거치는 비용이 <b>0</b>이 된다 — 레거시처럼 프레임을 큐에
/// 넣는 모델보다 일반적이면서 더 싸다.
/// </para>
/// <para>
/// <b>인라인 실행을 파티션 스레드에서만 허용한다.</b> 다른 스레드에서 인라인으로
/// 실행해버리면 "같은 키의 작업은 같은 스레드"라는 보장이 그 자리에서 깨진다.
/// 이 한 줄이 순서 보장의 마지막 방어선이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다.
/// </para>
/// </remarks>
internal sealed class PartitionTaskScheduler : TaskScheduler
{
    private readonly ExecutionPartition _partition;

    internal PartitionTaskScheduler(ExecutionPartition partition) => _partition = partition;

    /// <inheritdoc />
    /// <remarks>파티션 스레드 하나뿐이므로 동시성은 1이다.</remarks>
    public override int MaximumConcurrencyLevel => 1;

    /// <summary>파티션 스레드 위에서 태스크를 실행한다.</summary>
    internal void ExecuteInPartition(Task task) => TryExecuteTask(task);

    /// <summary>종료 중이라 파티션이 받을 수 없을 때 다른 스레드에서 실행한다.</summary>
    /// <remarks>
    /// 순서 보장이 깨지지만, 태스크를 버려서 대기자를 영원히 매달아두는 것보다는 낫다.
    /// 종료 경로에서만 일어난다.
    /// </remarks>
    internal void ExecuteOutsidePartition(Task task) => TryExecuteTask(task);

    /// <inheritdoc />
    protected override void QueueTask(Task task) => _partition.Enqueue(task);

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // 이미 큐에 들어간 태스크를 인라인 실행하면 큐에 유령 항목이 남는다.
        if (taskWasPreviouslyQueued)
        {
            return false;
        }

        // 파티션 스레드가 아니면 절대 인라인하지 않는다 — 그 순간 순서 보장이 깨진다.
        return _partition.IsCurrentThread && TryExecuteTask(task);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 디버거 전용 API 다. 큐를 실제로 열거하려면 소비자와 동기화해야 하는데,
    /// 그 비용을 핫패스에 넣을 수 없다. 빈 목록을 돌려준다.
    /// </remarks>
    protected override IEnumerable<Task> GetScheduledTasks() => [];
}
