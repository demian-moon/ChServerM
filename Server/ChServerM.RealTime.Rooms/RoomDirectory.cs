using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 디렉터리 — 룸의 생성·조회·해산을 관장하는 레지스트리.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "룸 ID → 룸" 매핑과 룸 수 상한이 없으면, 클라이언트가 새 ID 를 무한히
/// 던지는 것만으로 서버 메모리를 채울 수 있다. 상한 초과는
/// <see cref="RoomOpenStatus.LimitReached"/>로 <b>거부</b>된다(9.6).
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전. 어느 실행 문맥에서든 호출해도 된다.</para>
/// </remarks>
public sealed class RoomDirectory
{
    private readonly ConcurrentDictionary<RoomId, Room> _rooms = new();
    private readonly RoomDirectoryOptions _options;
    private int _roomCount;

    /// <summary>디렉터리를 만든다.</summary>
    /// <param name="options">설정. <see langword="null"/>이면 기본값.</param>
    public RoomDirectory(RoomDirectoryOptions? options = null)
    {
        options?.Validate();
        _options = options?.Snapshot() ?? new RoomDirectoryOptions();
    }

    /// <summary>현재 룸 수.</summary>
    public int RoomCount => Volatile.Read(ref _roomCount);

    /// <summary>룸을 얻거나 만든다.</summary>
    /// <param name="id">룸 키. <see cref="RoomId.None"/>은 거부된다.</param>
    /// <param name="room">성공 시 룸. 거부 시 <see langword="null"/>.</param>
    public RoomOpenStatus TryGetOrCreate(RoomId id, out Room? room)
    {
        if (id.IsNone)
        {
            room = null;
            return RoomOpenStatus.InvalidId;
        }

        if (_rooms.TryGetValue(id, out room))
        {
            return RoomOpenStatus.Existing;
        }

        // 상한을 먼저 선점하고 실패하면 되돌린다 — 검사·추가 사이의 경쟁으로 상한이 뚫리지 않는다.
        if (Interlocked.Increment(ref _roomCount) > _options.MaxRooms)
        {
            Interlocked.Decrement(ref _roomCount);
            room = null;
            return RoomOpenStatus.LimitReached;
        }

        var created = new Room(id, _options.MaxMembersPerRoom);
        Room actual = _rooms.GetOrAdd(id, created);
        if (!ReferenceEquals(actual, created))
        {
            // 경쟁에서 졌다 — 선점한 정원을 되돌린다. Room 생성은 부작용이 없어 버려도 된다
            // (레거시 ConcurrentDictionary+IDisposable 팩토리 함정과 달리 자원이 없다).
            Interlocked.Decrement(ref _roomCount);
        }

        room = actual;
        return ReferenceEquals(actual, created) ? RoomOpenStatus.Created : RoomOpenStatus.Existing;
    }

    /// <summary>룸을 조회한다.</summary>
    public bool TryGet(RoomId id, out Room? room) => _rooms.TryGetValue(id, out room);

    /// <summary>룸을 해산하고 디렉터리에서 제거한다.</summary>
    /// <returns>룸이 있었으면 <see langword="true"/>.</returns>
    public bool TryDisband(RoomId id) => TryDisband(id, out _);

    /// <summary>룸을 해산하고 디렉터리에서 제거하며, 해산 시점의 멤버 스냅샷을 돌려준다.</summary>
    /// <param name="id">해산할 룸.</param>
    /// <param name="members">해산 시점의 멤버. 룸이 없었으면 빈 배열.</param>
    /// <returns>룸이 있었으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// 앱의 사전 통지와 해산 사이의 창에 끼어든 멤버(<see cref="Room.TryJoin"/> 성공 후 통지
    /// 없이 제거될 뻔한)가 이 스냅샷에 담긴다 — 앱이 이 목록으로 마지막 통지·정리를 한다
    /// (감사 2026-08-18 R-6, <see cref="Room.Disband"/> 참조).
    /// </remarks>
    public bool TryDisband(RoomId id, out IRoomMemberSink[] members)
    {
        if (!_rooms.TryRemove(id, out Room? room))
        {
            members = [];
            return false;
        }

        Interlocked.Decrement(ref _roomCount);
        members = room.Disband();
        return true;
    }
}

/// <summary>
/// <see cref="RoomDirectory"/>의 설정.
/// </summary>
public sealed class RoomDirectoryOptions
{
    /// <summary>기본 룸 수 상한. 4,096.</summary>
    public const int DefaultMaxRooms = 4_096;

    /// <summary>기본 룸당 정원. 1,024.</summary>
    public const int DefaultMaxMembersPerRoom = 1_024;

    /// <summary>룸 수 상한. 넘으면 생성이 거부된다.</summary>
    public int MaxRooms { get; set; } = DefaultMaxRooms;

    /// <summary>룸당 정원. 넘으면 참가가 거부된다.</summary>
    public int MaxMembersPerRoom { get; set; } = DefaultMaxMembersPerRoom;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (MaxRooms < 1)
        {
            throw new InvalidOperationException($"{nameof(MaxRooms)}는 1 이상이어야 한다. 현재 값: {MaxRooms}");
        }

        if (MaxMembersPerRoom < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxMembersPerRoom)}은 1 이상이어야 한다. 현재 값: {MaxMembersPerRoom}");
        }
    }

    /// <summary>현재 값을 복사한 스냅샷을 만든다.</summary>
    internal RoomDirectoryOptions Snapshot() => new()
    {
        MaxRooms = MaxRooms,
        MaxMembersPerRoom = MaxMembersPerRoom,
    };
}
