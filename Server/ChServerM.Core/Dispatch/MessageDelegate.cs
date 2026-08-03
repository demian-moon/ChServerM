using System.Threading.Tasks;

namespace ChServerM.Dispatch;

/// <summary>
/// 메시지 처리 파이프라인의 한 단계.
/// </summary>
/// <param name="context">처리할 메시지의 문맥.</param>
/// <returns>이 단계가 판정한 처리 결과.</returns>
/// <remarks>
/// <para>
/// <b>결과를 반환값으로 강제하는 이유.</b> 처리 결과를 문맥의 가변 필드에 적어두는 방식이면
/// 아무도 적지 않고 지나갈 수 있다 — 그러면 거부당한 메시지가 "정상 처리"로 집계된다.
/// 반환 타입으로 만들면 컴파일러가 <b>모든 경로에서 결정을 강제</b>한다.
/// 조용히 버려지는 메시지를 만들지 않는 것이 이 설계의 목적이다.
/// </para>
/// <para>
/// 미들웨어 체인은 이 델리게이트를 <b>조립 시점에</b> 한 번 엮어 만든다.
/// 프레임마다 체인을 다시 만들면 그게 곧 프레임당 할당이다.
/// </para>
/// <para>
/// <see cref="ValueTask{TResult}"/>를 쓰는 이유는 대부분의 핸들러가 동기적으로 끝나기 때문이다.
/// <see cref="Task{TResult}"/>였다면 그때마다 힙 객체가 하나씩 생긴다.
/// </para>
/// <para>
/// <b>반환된 <see cref="ValueTask{TResult}"/>를 두 번 await 하지 않는다.</b> 이것은
/// <see cref="ValueTask{TResult}"/>의 계약이고, 어기면 재사용된 상태 기계를 건드리게 된다.
/// </para>
/// </remarks>
public delegate ValueTask<DispatchStatus> MessageDelegate(MessageContext context);
