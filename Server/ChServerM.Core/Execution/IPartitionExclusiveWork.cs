using System.Threading.Tasks;

namespace ChServerM.Execution;

/// <summary>
/// 파티션의 배타 구간에서 실행되는 비동기 작업 (ADR-0008).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 파티션의 배타성 보장은 "같은 스레드에서 실행"이 아니라
/// <b>"완료까지 다음 작업을 시작하지 않는다"</b>로 정의된다(ADR-0008). 스레드 어피니티로
/// 배타성을 얻으려던 이전 설계는 <c>ConfigureAwait(false)</c> 라이브러리 규약과 구조적으로
/// 충돌해 실전 경로에서 깨졌다 — <c>await</c> 연속이 스레드풀로 이탈하면 같은 파티션의
/// 작업 두 개가 병렬로 돌 수 있었다. 이 계약은 <c>await</c> 를 걸치는 작업에도 배타성이
/// 유지되도록, 작업의 <b>완료 시점</b>을 파티션에게 알리는 수단이다.
/// </para>
/// <para>
/// <b>실행 규약.</b> <see cref="ExecuteAsync"/> 의 동기 구간은 파티션 전용 스레드에서
/// 실행된다. 반환된 <see cref="ValueTask"/> 가 완료될 때까지 파티션은 다음 큐 항목을
/// 시작하지 않는다 — 비동기로 이어지는 구간은 다른 스레드에서 돌 수 있지만,
/// <b>같은 파티션의 다른 작업과 절대 겹치지 않는다.</b>
/// </para>
/// <para>
/// <b>예외 규약.</b> <see cref="ExecuteAsync"/> 는 예외를 밖으로 던지지 않아야 한다.
/// 파티션은 작업의 결과를 소비할 수 없으므로(로그가 전부다), 결과·오류는 작업이 소유한
/// 별도 채널(예: <c>IValueTaskSource</c>)로 게시자에게 전달한다.
/// </para>
/// <para>
/// <b>⚠ 교착 규약.</b> 이 작업 안에서 <b>같은 파티션</b>의
/// <see cref="IExecutionPartition.Scheduler"/> 로 스케줄한 태스크를 <c>await</c> 하면
/// 교착한다 — 파티션은 이 작업의 완료를 기다리고 있으므로 그 태스크를 실행할 수 없다.
/// 같은 파티션에 후속 작업을 넣으려면 <see cref="IExecutionPartition.TryPost{TWork}"/> 로
/// <b>기다리지 않고</b> 게시한다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 게시자가 인스턴스를 소유하고 재사용할 수 있다(커넥션당 1개 재사용이
/// 의도된 사용법). 파티션은 실행이 끝난 뒤 참조를 붙들지 않는다.
/// </para>
/// </remarks>
public interface IPartitionExclusiveWork
{
    /// <summary>작업을 실행한다. 파티션 전용 스레드에서 호출된다.</summary>
    /// <returns>
    /// 작업의 완료를 나타내는 <see cref="ValueTask"/>. 파티션은 이것이 완료될 때까지
    /// 다음 큐 항목을 시작하지 않는다.
    /// </returns>
    ValueTask ExecuteAsync();
}
