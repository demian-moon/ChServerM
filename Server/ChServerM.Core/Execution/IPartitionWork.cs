namespace ChServerM.Execution;

/// <summary>
/// 특정 파티션에서 실행될 작업 하나.
/// </summary>
/// <remarks>
/// <para>
/// <b>구조체로 구현한다.</b> <see cref="IExecutionPartition.TryPost{TWork}"/>가
/// <c>where TWork : struct</c>로 제약하므로 제네릭 특수화가 일어나고,
/// 작업을 큐에 넣을 때 <b>박싱이 없다.</b>
/// </para>
/// <para>
/// 델리게이트(<c>Action</c>)를 쓰지 않는 이유가 여기 있다. 클로저는 캡처마다 힙 객체를
/// 만들고, 초당 수십만 건의 타이머·작업 주입에서 그것이 그대로 GC 압력이 된다.
/// </para>
/// <para>
/// <b>예외를 던지지 않는다.</b> 파티션 실행 루프에서 예외가 새면 그 파티션에 묶인
/// 모든 커넥션이 함께 죽는다. 실패는 작업 안에서 처리하고 기록한다.
/// </para>
/// </remarks>
public interface IPartitionWork
{
    /// <summary>작업을 실행한다.</summary>
    /// <remarks>파티션 스레드에서 동기적으로 호출된다. <b>블로킹하지 않는다.</b></remarks>
    void Execute();
}
