using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Serialization.Protobuf;

namespace ChServerM.Samples.StatelessWeb;

/// <summary>
/// 이 샘플이 쓰는 메시지 식별자.
/// </summary>
/// <remarks>
/// 앱 대역(1~40000)을 쓴다. 프레임워크 대역(40001~)을 침범하면 하트비트와 충돌한다.
/// </remarks>
internal static class WebProtocol
{
    /// <summary><see cref="Sum"/> 의 원시 값. 어트리뷰트 인자는 상수여야 해서 분리한다.</summary>
    public const ushort SumId = 1;

    /// <summary>정수 목록을 보내면 합계를 돌려달라는 요청 — Protobuf 직렬화 경로.</summary>
    public static MessageId Sum => new(SumId);
}

/// <summary>
/// 합계 핸들러 — <c>stateless-web</c> 프로필(ADR-0004)의 핸들러 예시.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 이 타입은 두 가지를 실증한다: (1) protoc 가 생성한 메시지 타입이
/// <c>[MessageHandler]</c> + 소스 제너레이터 등록 경로에 그대로 꽂힌다(직렬화 축 교체 증명 —
/// EchoServer 의 <c>GreetHandler</c> 는 같은 자리에 MemoryPack 을 꽂았다).
/// (2) 무상태 프로필의 스레드 규약을 코드로 보인다(아래).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 실행 모델이 없는 조립(무상태 프로필)에서는 이 인스턴스 하나를
/// <b>모든 커넥션이 공유하고, 스레드풀에서 병렬로 호출한다.</b> 순서 보장이 없는 대신
/// 수평으로 퍼진다. 그래서 이 타입에는 가변 필드가 없다 — 재사용 버퍼를 필드로 두면
/// (파티션 실행 모델에서는 합법인 패턴) 여기서는 데이터 경합이다.
/// </para>
/// </remarks>
[MessageHandler(WebProtocol.SumId)]
internal sealed class SumHandler(IFrameEncoder encoder) : IMessageHandler<SumRequest>
{
    /// <summary>응답 직렬화기. 상태가 없으므로 공유해도 안전하다.</summary>
    private static readonly ProtobufMessageSerializer<SumReply> ReplySerializer = new();

    /// <summary>합계를 계산해 <see cref="SumReply"/> 로 돌려보낸다.</summary>
    /// <remarks>
    /// 응답 프레임은 요청의 시퀀스 번호를 그대로 되돌린다. 병렬 실행이라 응답 순서가
    /// 보장되지 않으므로, 클라이언트는 이 번호로 요청과 응답을 짝짓는다.
    /// </remarks>
    public async ValueTask HandleAsync(MessageContext context, SumRequest message)
    {
        long sum = 0;
        foreach (long value in message.Values)
        {
            // 샘플이므로 오버플로는 래핑으로 둔다. 실서비스라면 checked + 오류 응답이 맞다.
            sum = unchecked(sum + value);
        }

        SumReply reply = new() { Sum = sum, TermCount = message.Values.Count };

        // 지역 버퍼를 쓴다 — 위 스레드 규약 참조. 병렬 핸들러에서 공유 가변 버퍼는 경합이다.
        ArrayBufferWriter<byte> buffer = new(64);
        ReplySerializer.Serialize(buffer, in reply);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            encoder,
            context.Envelope.MessageId,
            buffer.WrittenSpan,
            FrameFlags.None,
            context.Envelope.Sequence,
            context.CancellationToken).ConfigureAwait(false);
    }
}
