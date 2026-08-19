using ChServerM.Identity;

namespace ChServerM.Samples.FlatGameRoom;

/// <summary>
/// 이 샘플이 쓰는 메시지 식별자와 페이로드 규약.
/// </summary>
/// <remarks>
/// <para>
/// 앱 대역(1~40000)을 쓴다. 프레임워크 대역(40001~)을 침범하면 하트비트·세션 재개와 충돌한다.
/// 세션 수립(40009)·재개(40007/40008)는 프레임워크 예약 ID 이므로 여기에 없다 —
/// 와이어 형식은 <see cref="ChServerM.Sessions.SessionHandshakeCodec"/> 가 영구 동결한다.
/// </para>
/// <para>모든 앱 페이로드는 FlatBuffers(FlatSharp) 테이블이다 — <c>Schemas/flat_game_room.fbs</c>.</para>
/// <list type="table">
///   <item>
///     <term><see cref="Login"/></term>
///     <description>요청: <c>LoginRequest</c>. 응답: 세션 수립 통지(40009) 다음에 <c>LoginReply</c>.
///     자격 검증은 <see cref="DemoAuthenticator"/> 가 미들웨어 안에서 수행한다 — 실패는 응답이
///     아니라 커넥션 종료(6000)다.</description>
///   </item>
///   <item>
///     <term><see cref="JoinRoom"/></term>
///     <description>요청: <c>JoinRoomRequest</c>. 응답: <c>JoinRoomReply</c>.</description>
///   </item>
///   <item>
///     <term><see cref="ChatSend"/> / <see cref="ChatBroadcast"/></term>
///     <description>요청과 브로드캐스트의 ID 를 나눈다 — 테이블이 다르기 때문이다(요청에는
///     발신자·시각이 없다. 서버가 채워서 뿌린다). 브로드캐스트 프레임의 시퀀스는 항상 0 이다 —
///     헤더를 N 명이 공유하므로 커넥션별 일련번호를 실을 수 없다(ADR-0064).</description>
///   </item>
///   <item>
///     <term><see cref="MoveUpdate"/> / <see cref="MoveBroadcast"/></term>
///     <description>이동 보고와 그 브로드캐스트. 서버가 좌표 범위를 검증한 뒤에만 뿌린다.</description>
///   </item>
///   <item>
///     <term><see cref="LeaveRoom"/></term>
///     <description>요청: <c>LeaveRoomRequest</c>. 응답: <c>LeaveRoomReply</c>.</description>
///   </item>
/// </list>
/// </remarks>
internal static class FlatGameRoomProtocol
{
    /// <summary><see cref="Login"/> 의 원시 값. 어트리뷰트 인자는 상수여야 해서 분리한다.</summary>
    public const ushort LoginId = 1;

    /// <summary><see cref="JoinRoom"/> 의 원시 값.</summary>
    public const ushort JoinRoomId = 2;

    /// <summary><see cref="ChatSend"/> 의 원시 값.</summary>
    public const ushort ChatSendId = 3;

    /// <summary><see cref="ChatBroadcast"/> 의 원시 값.</summary>
    public const ushort ChatBroadcastId = 4;

    /// <summary><see cref="MoveUpdate"/> 의 원시 값.</summary>
    public const ushort MoveUpdateId = 5;

    /// <summary><see cref="MoveBroadcast"/> 의 원시 값.</summary>
    public const ushort MoveBroadcastId = 6;

    /// <summary><see cref="LeaveRoom"/> 의 원시 값.</summary>
    public const ushort LeaveRoomId = 7;

    /// <summary>로그인(자격 메시지). 인증 미들웨어가 이 ID 만 검증한다.</summary>
    public static MessageId Login => new(LoginId);

    /// <summary>룸에 입장하겠다는 요청.</summary>
    public static MessageId JoinRoom => new(JoinRoomId);

    /// <summary>같은 룸의 다른 멤버들에게 채팅을 뿌려달라는 요청.</summary>
    public static MessageId ChatSend => new(ChatSendId);

    /// <summary>서버→클라이언트 채팅 브로드캐스트.</summary>
    public static MessageId ChatBroadcast => new(ChatBroadcastId);

    /// <summary>이동 보고.</summary>
    public static MessageId MoveUpdate => new(MoveUpdateId);

    /// <summary>서버→클라이언트 이동 브로드캐스트.</summary>
    public static MessageId MoveBroadcast => new(MoveBroadcastId);

    /// <summary>룸에서 나가겠다는 요청.</summary>
    public static MessageId LeaveRoom => new(LeaveRoomId);
}

/// <summary>
/// 커넥션 상태 비트 — 상태별 메시지 화이트리스트(<c>MessageStateFilterMiddleware</c>)의 판정 근거.
/// </summary>
/// <remarks>
/// <para>
/// 비트의 의미는 앱이 정의한다(프레임워크가 상태 이름을 정하면 워크로드 전제가 Core 에
/// 들어온다 — ADR-0004). 이 샘플은 두 단계뿐이다: 연결 직후 → 로그인 완료.
/// </para>
/// <para>
/// 전이 경로는 둘이다. (1) <see cref="DemoAuthenticator"/> 성공 →
/// <c>AuthenticationMiddleware</c> 가 <see cref="LoggedIn"/> 으로 대체 전이,
/// (2) 세션 재개(40007) 성공 → <see cref="SessionResumeStateBridge"/> 가 같은 전이를 수행.
/// 두 경로 모두 "인증됐다" 플래그가 따로 없다 — 상태 집합 그 자체가 인증 여부다(T-20).
/// </para>
/// </remarks>
internal static class ConnectionStates
{
    /// <summary>연결 직후. 로그인과 세션 재개만 허용된다.</summary>
    public const uint Connected = 1u << 0;

    /// <summary>로그인(또는 세션 재개) 완료. 룸·채팅·이동 메시지가 허용된다.</summary>
    public const uint LoggedIn = 1u << 1;
}
