using System.Threading.Tasks;

namespace ChServerM.Dispatch;

/// <summary>
/// 프레임을 알맞은 핸들러로 보낸다.
/// </summary>
/// <remarks>
/// <para>
/// 역직렬화·미들웨어 체인·핸들러 호출을 한데 묶는다. 읽기 루프는 프레임을 꺼내
/// 이것 하나만 부르면 되고, 그래서 전송 코드에 애플리케이션 지식이 새지 않는다.
/// </para>
/// <para>
/// 조회는 <b>메시지 식별자 → 핸들러</b>이고, 조립 시점에 확정된 테이블을 쓴다.
/// 레거시는 프레임마다 선형 탐색 + 가상 호출을 했다.
/// </para>
/// <para>구현체는 <b>스레드 안전해야 한다.</b> 모든 커넥션이 같은 인스턴스를 공유한다.</para>
/// </remarks>
public interface IMessageDispatcher
{
    /// <summary>메시지를 디스패치한다.</summary>
    /// <param name="context">디스패치할 메시지의 문맥.</param>
    /// <returns>디스패치 결과.</returns>
    /// <remarks>
    /// <b>예외를 던지지 않는다.</b> 핸들러가 던진 예외는
    /// <see cref="DispatchStatus.Faulted"/>로 바뀌어 돌아온다.
    /// </remarks>
    ValueTask<DispatchStatus> DispatchAsync(MessageContext context);
}
