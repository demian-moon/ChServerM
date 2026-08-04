using System.Threading.Tasks;

namespace ChServerM.Execution;

/// <summary>
/// 독립적으로 실행되는 단위 하나. 같은 파티션의 작업은 서로 배타적이고 순서대로다.
/// </summary>
/// <remarks>
/// <para>
/// <b>이것이 ADR-0005의 실체다.</b> 같은 키의 작업이 항상 같은 파티션으로 가므로
/// 락 없이 순서가 보장되고, 다른 키는 완전히 독립이라 코어 수만큼 확장된다.
/// "공유하지 않는 것이 1순위, 락 금지는 2순위"(CLAUDE.md 9장)의 구현이다.
/// </para>
/// <para>
/// <b>보장의 정의(ADR-0008).</b> 파티션이 보장하는 것은 <b>배타성과 FIFO 순서</b>다 —
/// 같은 파티션의 작업 두 개는 절대 동시에 실행되지 않고, 게시된 순서대로 시작된다.
/// "같은 스레드에서 실행"은 보장이 아니다: 동기 구간은 파티션 전용 스레드에서 돌지만,
/// <c>await</c> 이후의 연속은 다른 스레드에서 돌 수 있다. 배타성은 스레드가 아니라
/// <b>완료 대기</b>로 강제된다 — 그래서 <c>ConfigureAwait(false)</c> 를 쓰는 코드에서도
/// 깨지지 않는다.
/// </para>
/// <para>진입 경로가 셋인데 역할이 다르다.</para>
/// <list type="bullet">
///   <item><description>
///     <b>주 경로 — <see cref="TryEnqueueExclusive"/>.</b> 커넥션의 프레임 디스패치처럼
///     이미 승인된 작업을 배타 구간으로 넣는다. 작업이 <c>await</c> 를 걸쳐도
///     완료까지 배타 구간이 유지된다.
///   </description></item>
///   <item><description>
///     <b>보조 경로 — <see cref="TryPost{TWork}"/>.</b> 타이머 만료나 다른 파티션에서
///     오는 <b>동기</b> 작업을 순서 있게 주입한다. 유계 검사가 있는 유일한 유입 지점이다.
///   </description></item>
///   <item><description>
///     <b><see cref="Scheduler"/>.</b> 태스크의 동기 구간을 파티션 스레드에서 돌린다.
///     <c>await</c> 연속의 스레드 복귀는 <c>ConfigureAwait</c> 정책에 달려 있으므로
///     <b>이것만으로 배타성을 얻으려 하지 않는다</b> — 그 용도는
///     <see cref="TryEnqueueExclusive"/> 다.
///   </description></item>
/// </list>
/// </remarks>
public interface IExecutionPartition
{
    /// <summary>이 파티션의 인덱스. <c>0</c> 이상 <see cref="IExecutionModel.PartitionCount"/> 미만.</summary>
    /// <remarks>메트릭 태그와 진단에 쓴다.</remarks>
    int Index { get; }

    /// <summary>이 파티션에 태스크의 동기 구간을 고정하는 스케줄러.</summary>
    /// <remarks>
    /// <para>
    /// 여기서 시작한 <c>Task</c> 의 본문(첫 <c>await</c> 까지)은 파티션 스레드에서 실행된다.
    /// <b><c>await</c> 이후의 연속까지 이 파티션으로 돌아온다는 보장은 없다</b> —
    /// <c>ConfigureAwait(false)</c> 나 스케줄러를 캡처하지 않는 awaitable(파이프 등)은
    /// 연속을 다른 스레드로 보낸다(ADR-0008 의 반증 근거).
    /// </para>
    /// <para>
    /// <b>⚠ 배타 작업 안에서 이 스케줄러의 태스크를 <c>await</c> 하면 교착한다</b> —
    /// 파티션은 그 배타 작업의 완료를 기다리는 중이라 태스크를 실행할 수 없다.
    /// </para>
    /// </remarks>
    TaskScheduler Scheduler { get; }

    /// <summary>이 파티션에 작업을 넣는다.</summary>
    /// <typeparam name="TWork">작업 타입. 구조체여야 박싱이 없다.</typeparam>
    /// <param name="work">넣을 작업.</param>
    /// <returns>받아들였으면 <see langword="true"/>, 큐가 가득 찼으면 <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>큐는 유계다.</b> 포화 시 <see langword="false"/>를 돌려주고, 호출자는 이를
    /// 반드시 처리한다 — 반환값을 버리면 그것이 곧 조용한 유실이다.
    /// 무제한 큐는 부하가 걸렸을 때 지연을 무한히 늘리다 결국 메모리로 죽는다.
    /// <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).
    /// </para>
    /// <para>블로킹하지 않는다. 어느 스레드에서 불러도 안전하다.</para>
    /// </remarks>
    bool TryPost<TWork>(in TWork work) where TWork : struct, IPartitionWork;

    /// <summary>배타 구간에서 실행할 비동기 작업을 넣는다 (ADR-0008 의 주 경로).</summary>
    /// <param name="work">
    /// 실행할 작업. 게시자가 소유하며 재사용할 수 있다 — 커넥션당 1개를 재사용하는 것이
    /// 의도된 사용법이다(프레임당 할당 0).
    /// </param>
    /// <returns>
    /// 받아들였으면 <see langword="true"/>. <see langword="false"/>는 파티션이 종료 중이라는
    /// 뜻이며, 호출자는 작업을 포기하고 자기 종료 경로를 밟아야 한다.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>용량 검사를 하지 않는다.</b> 이 경로로 오는 작업은 커넥션 수락 시점에 이미
    /// 승인됐고, 호출자가 완료를 기다리므로 호출자당 동시 1건이다(자연 백프레셔).
    /// <see cref="TryPost{TWork}"/> 의 유계 규약과 역할이 다른 이유다.
    /// </para>
    /// <para>
    /// <b>배타성 보장.</b> 파티션은 이 작업의 <see cref="IPartitionExclusiveWork.ExecuteAsync"/>
    /// 가 반환한 <see cref="ValueTask"/> 가 완료될 때까지 다음 큐 항목을 시작하지 않는다.
    /// </para>
    /// <para>블로킹하지 않는다. 어느 스레드에서 불러도 안전하다.</para>
    /// </remarks>
    bool TryEnqueueExclusive(IPartitionExclusiveWork work);
}
