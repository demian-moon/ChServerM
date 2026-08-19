using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Samples.FlatGameRoom.Messages;

namespace ChServerM.Samples.FlatGameRoom;

// ─────────────────────────────────────────────────────────────────────────────
// [MessageHandler] 로 선언된 타입 있는 핸들러들 — 디스패치 소스 제너레이터 경로(ADR-0014).
//
// 등록 코드는 손으로 쓰지 않는다: 제너레이터가 이 선언들에서 MapGeneratedHandlers 를 만들고,
// 역직렬화는 FlatSharp 어댑터가 제공자(FlatSharpMessageSerializerProvider) 경유로 꽂힌다.
// EchoServer(MemoryPack)·StatelessWeb(Protobuf)과 핸들러 작성 방법이 완전히 같다 —
// 직렬화 축만 세 번째 어댑터(FlatBuffers)로 갈아 끼웠다. 그것이 축 교체 가능성의 실증이다.
//
// 각 핸들러는 얇은 어댑터다 — 정책·상태는 전부 FlatGameRoomService 에 있다. 핸들러를
// 로직에서 분리해 두면 같은 서비스가 다른 전송·직렬화 조립에도 그대로 꽂힌다.
//
// 스레드 규약: 파티션 실행 모델 위에서 같은 커넥션은 순차, 다른 커넥션은 병렬로 이
// 인스턴스들을 부른다. 핸들러 자신은 가변 상태를 갖지 않는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>로그인 마무리 핸들러 — 인증 미들웨어 통과 후에만 도달한다.</summary>
/// <remarks>
/// 자격 검증은 여기가 아니라 <see cref="DemoAuthenticator"/>(미들웨어 안)에서 이미 끝났다.
/// 이 핸들러의 몫은 정책의 나머지 절반 — 세션 수립·수립 통지·응답이다(T-20 구조).
/// </remarks>
[MessageHandler(FlatGameRoomProtocol.LoginId)]
internal sealed class LoginHandler(FlatGameRoomService service) : IMessageHandler<LoginRequest>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(MessageContext context, LoginRequest message) =>
        service.CompleteLoginAsync(context, message);
}

/// <summary>룸 입장 핸들러.</summary>
[MessageHandler(FlatGameRoomProtocol.JoinRoomId)]
internal sealed class JoinRoomHandler(FlatGameRoomService service) : IMessageHandler<JoinRoomRequest>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(MessageContext context, JoinRoomRequest message) =>
        service.JoinAsync(context, message);
}

/// <summary>채팅 핸들러 — 같은 룸의 다른 멤버에게 <c>ChatBroadcast</c> 로 배달된다.</summary>
[MessageHandler(FlatGameRoomProtocol.ChatSendId)]
internal sealed class ChatSendHandler(FlatGameRoomService service) : IMessageHandler<ChatSend>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(MessageContext context, ChatSend message) =>
        service.ChatAsync(context, message);
}

/// <summary>이동 보고 핸들러 — 서버 검증 후 <c>MoveBroadcast</c> 로 배달된다.</summary>
[MessageHandler(FlatGameRoomProtocol.MoveUpdateId)]
internal sealed class MoveUpdateHandler(FlatGameRoomService service) : IMessageHandler<MoveUpdate>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(MessageContext context, MoveUpdate message) =>
        service.MoveAsync(context, message);
}

/// <summary>룸 퇴장 핸들러.</summary>
[MessageHandler(FlatGameRoomProtocol.LeaveRoomId)]
internal sealed class LeaveRoomHandler(FlatGameRoomService service) : IMessageHandler<LeaveRoomRequest>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(MessageContext context, LeaveRoomRequest message) =>
        service.LeaveAsync(context, message);
}
