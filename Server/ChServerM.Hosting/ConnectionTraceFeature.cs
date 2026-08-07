using System.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 커넥션 span 의 부모 컨텍스트를 프레임 디스패치로 나르는 커넥션 기능 (ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — <see cref="Activity.Current"/> 가 파티션 스레드로 흐르지 않는다.</b>
/// 커넥션 span 은 읽기 루프(수락) 스레드에서 열리지만, 디스패치는 실행 모델(ADR-0008)에서
/// <b>파티션 스레드</b>에서 돈다. <see cref="Activity.Current"/> 는 AsyncLocal 이라 채널
/// 핸드오프를 넘지 못하므로, 디스패치 span 을 커넥션 span 의 자식으로 만들려면 부모
/// <see cref="ActivityContext"/> 를 <b>명시적으로</b> 실어 넘겨야 한다. 이 기능이 그 매개체다.
/// </para>
/// <para>
/// <b>수명·스레드 규약.</b> <see cref="TracingConnectionHandler"/> 가 커넥션 수립 시
/// <b>1회</b> <see cref="ChServerM.Features.IFeatureCollection.Set{TFeature}"/> 하고, 디스패치가
/// 프레임마다 <see cref="ChServerM.Features.IFeatureCollection.Get{TFeature}"/> 로 읽는다.
/// 이는 기능 모음의 규약("수립 시 단일 스레드로 등록하고 그 뒤에는 읽기만") 그대로다 —
/// Set 이 첫 디스패치보다 확실히 앞서고(데코레이터가 읽기 루프를 감싼다), 채널 게시-소비가
/// 가시성을 보장하므로 락이 필요 없다.
/// </para>
/// </remarks>
internal sealed class ConnectionTraceFeature
{
    /// <summary>커넥션 span 의 컨텍스트로 기능을 만든다.</summary>
    /// <param name="parentContext">커넥션 span 의 <see cref="ActivityContext"/>. 디스패치 span 의 부모가 된다.</param>
    public ConnectionTraceFeature(ActivityContext parentContext) => ParentContext = parentContext;

    /// <summary>디스패치 span 에 부모로 넘길 커넥션 span 의 컨텍스트.</summary>
    public ActivityContext ParentContext { get; }
}
