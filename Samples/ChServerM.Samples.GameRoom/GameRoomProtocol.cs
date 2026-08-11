using ChServerM.Identity;

namespace ChServerM.Samples.GameRoom;

/// <summary>
/// 이 샘플이 쓰는 메시지 식별자와 페이로드 규약.
/// </summary>
/// <remarks>
/// <para>앱 대역(1~40000)을 쓴다. 프레임워크 대역(40001~)을 침범하면 하트비트와 충돌한다.</para>
/// <list type="table">
///   <item>
///     <term><see cref="Join"/></term>
///     <description>요청: 8바이트 리틀 엔디안 룸 번호. 응답: 1바이트 <see cref="JoinResult"/>.</description>
///   </item>
///   <item>
///     <term><see cref="Chat"/></term>
///     <description>요청: UTF-8 텍스트. 같은 룸의 <b>다른</b> 멤버 전원에게 같은 ID 로
///     브로드캐스트된다(발신자 제외). 브로드캐스트 프레임의 시퀀스는 항상 0 이다 —
///     헤더를 N 명이 공유하므로 커넥션별 일련번호를 실을 수 없다(ADR-0064).</description>
///   </item>
///   <item>
///     <term><see cref="Leave"/></term>
///     <description>요청: 빈 페이로드. 응답: 1바이트 <see cref="LeaveResult"/>.</description>
///   </item>
/// </list>
/// </remarks>
internal static class GameRoomProtocol
{
    /// <summary>룸에 입장하겠다는 요청.</summary>
    public static MessageId Join => new(1);

    /// <summary>같은 룸의 다른 멤버들에게 텍스트를 뿌려달라는 요청.</summary>
    public static MessageId Chat => new(2);

    /// <summary>룸에서 나가겠다는 요청.</summary>
    public static MessageId Leave => new(3);
}

/// <summary><see cref="GameRoomProtocol.Join"/> 응답 코드.</summary>
internal static class JoinResult
{
    /// <summary>입장했다.</summary>
    public const byte Joined = 0;

    /// <summary>이 커넥션은 이미 룸에 있다. 먼저 나가야 한다.</summary>
    public const byte AlreadyInRoom = 1;

    /// <summary>룸 정원이 가득 찼다.</summary>
    public const byte RoomFull = 2;

    /// <summary>해체된 룸이다.</summary>
    public const byte Disbanded = 3;

    /// <summary>룸을 열 수 없다(디렉터리 한도 초과 또는 잘못된 룸 번호).</summary>
    public const byte OpenFailed = 4;

    /// <summary>페이로드가 8바이트 룸 번호 형식이 아니다.</summary>
    public const byte MalformedPayload = 5;
}

/// <summary><see cref="GameRoomProtocol.Leave"/> 응답 코드.</summary>
internal static class LeaveResult
{
    /// <summary>나갔다.</summary>
    public const byte Left = 0;

    /// <summary>룸에 있지 않았다.</summary>
    public const byte NotInRoom = 1;
}
