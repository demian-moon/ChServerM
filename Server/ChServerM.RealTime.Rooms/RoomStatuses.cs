namespace ChServerM.RealTime.Rooms;

/// <summary>
/// <see cref="Room.TryJoin"/>의 결과. 연산별 상태 enum 규약(Phase 1 에러 모델).
/// </summary>
public enum RoomJoinStatus
{
    /// <summary>참가했다.</summary>
    Joined = 0,

    /// <summary>이미 같은 커넥션이 참가해 있다.</summary>
    AlreadyJoined = 1,

    /// <summary>정원(<see cref="Room.MaxMembers"/>) 초과. 거부가 붕괴보다 낫다(CLAUDE.md 9.6).</summary>
    RoomFull = 2,

    /// <summary>이미 해산된 룸이다.</summary>
    Disbanded = 3,
}

/// <summary>
/// <see cref="RoomDirectory.TryGetOrCreate"/>의 결과.
/// </summary>
public enum RoomOpenStatus
{
    /// <summary>기존 룸을 돌려줬다.</summary>
    Existing = 0,

    /// <summary>새로 만들었다.</summary>
    Created = 1,

    /// <summary>룸 수 상한(<see cref="RoomDirectoryOptions.MaxRooms"/>) 초과로 거부했다.</summary>
    LimitReached = 2,

    /// <summary><see cref="RoomId.None"/>은 룸 키가 될 수 없다.</summary>
    InvalidId = 3,
}

/// <summary>
/// <see cref="IRoomMemberSink.TryDeliver"/>의 결과.
/// </summary>
public enum RoomDeliveryStatus
{
    /// <summary>수락했다 — 프레임 소유권이 싱크로 넘어갔고, 싱크가 정확히 한 번 해제한다.</summary>
    Accepted = 0,

    /// <summary>송신 큐 포화로 거부했다. 프레임 소유권은 호출자에 남는다.</summary>
    QueueFull = 1,

    /// <summary>싱크가 닫혔거나 송신 실패로 사망했다. 프레임 소유권은 호출자에 남는다.</summary>
    Closed = 2,
}
