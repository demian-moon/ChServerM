using System.Threading.Tasks;

namespace ChServerM.Dispatch;

/// <summary>
/// 메시지 처리 파이프라인의 한 단계.
/// </summary>
/// <param name="context">처리할 메시지의 문맥.</param>
/// <returns>처리가 끝나면 완료되는 작업.</returns>
/// <remarks>
/// <para>
/// 미들웨어 체인은 이 델리게이트를 <b>조립 시점에</b> 한 번 엮어 만든다.
/// 프레임마다 체인을 다시 만들면 그게 곧 프레임당 할당이다.
/// </para>
/// <para>
/// <see cref="ValueTask"/>를 쓰는 이유는 대부분의 핸들러가 동기적으로 끝나기 때문이다.
/// <see cref="Task"/>였다면 그때마다 힙 객체가 하나씩 생긴다.
/// </para>
/// <para>
/// <b>반환된 <see cref="ValueTask"/>를 두 번 await 하지 않는다.</b> 이것은
/// <see cref="ValueTask"/>의 계약이고, 어기면 재사용된 상태 기계를 건드리게 된다.
/// </para>
/// </remarks>
public delegate ValueTask MessageDelegate(MessageContext context);
