using System.Threading.Tasks;

namespace ChServerM.Dispatch;

/// <summary>
/// 역직렬화가 끝난 메시지 하나를 처리한다.
/// </summary>
/// <typeparam name="TMessage">처리할 메시지 타입.</typeparam>
/// <remarks>
/// <para>
/// <b>애플리케이션이 실제로 쓰는 타입이다.</b> 여기 있는 코드는 전송이 TCP인지
/// 인메모리인지, 직렬화가 FlatBuffers인지 Protobuf인지 알지 못한다.
/// 그것이 이 프레임워크의 합격 기준이다 — <b>같은 핸들러가 모든 조합에서 돈다</b>(ADR-0004).
/// </para>
/// <para>
/// 구현체는 <b>스레드 안전해야 한다</b>는 가정을 하지 않아도 된다. 실행 모델이
/// 같은 파티션 키의 메시지를 단일 소비자에게 보내므로, 같은 키에 대해서는
/// 순차 실행이 보장된다(ADR-0005). 다른 키끼리는 병렬이다 —
/// <b>핸들러가 공유 상태를 만들면 그 보장이 깨진다.</b>
/// </para>
/// </remarks>
public interface IMessageHandler<TMessage>
{
    /// <summary>메시지를 처리한다.</summary>
    /// <param name="context">프레임 문맥. 응답 전송과 커넥션 제어에 쓴다.</param>
    /// <param name="message">역직렬화된 메시지.</param>
    /// <returns>처리가 끝나면 완료되는 작업.</returns>
    /// <remarks>
    /// <see cref="MessageContext.Payload"/>는 이 메서드가 반환하면 무효다.
    /// <paramref name="message"/>가 그 버퍼를 참조하고 있다면 여기서 복사해야 한다.
    /// </remarks>
    ValueTask HandleAsync(MessageContext context, TMessage message);
}
