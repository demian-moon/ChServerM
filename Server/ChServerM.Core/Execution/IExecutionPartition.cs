using System.Threading.Tasks;

namespace ChServerM.Execution;

/// <summary>
/// 독립적으로 실행되는 단위 하나. 같은 파티션의 작업은 서로 순차적이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>이것이 ADR-0005의 실체다.</b> 같은 키의 작업이 항상 같은 파티션으로 가므로
/// 락 없이 순서가 보장되고, 다른 키는 완전히 독립이라 코어 수만큼 확장된다.
/// "공유하지 않는 것이 1순위, 락 금지는 2순위"(CLAUDE.md 9장)의 구현이다.
/// </para>
/// <para>진입 경로가 둘인데 역할이 다르다.</para>
/// <list type="bullet">
///   <item><description>
///     <b>주 경로 — <see cref="Scheduler"/>.</b> 커넥션의 읽기 루프 자체를 이 스케줄러에서
///     돌린다. 그러면 프레임마다 큐를 거치는 비용이 <b>0</b>이다. 큐에 프레임을 넣는
///     모델(레거시가 그랬다)보다 일반적이면서 더 싸다.
///   </description></item>
///   <item><description>
///     <b>보조 경로 — <see cref="TryPost{TWork}"/>.</b> 타이머 만료나 다른 파티션에서
///     오는 작업을 순서 있게 주입한다.
///   </description></item>
/// </list>
/// </remarks>
public interface IExecutionPartition
{
    /// <summary>이 파티션의 인덱스. <c>0</c> 이상 <see cref="IExecutionModel.PartitionCount"/> 미만.</summary>
    /// <remarks>메트릭 태그와 진단에 쓴다.</remarks>
    int Index { get; }

    /// <summary>이 파티션에 작업을 고정하는 스케줄러.</summary>
    /// <remarks>
    /// 여기서 시작한 <c>Task</c>와 그 <c>await</c> 연속은 모두 이 파티션에서 이어진다.
    /// 커넥션 읽기 루프를 여기에 태우는 것이 기본 사용법이다.
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
}
