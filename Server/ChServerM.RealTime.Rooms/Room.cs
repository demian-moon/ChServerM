using System;
using System.Threading;
using ChServerM.Identity;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 하나 — 멤버십과 생명주기(참가·퇴장·해산)를 담는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "이 메시지를 같은 공간의 모두에게"의 그 <b>같은 공간</b>이다. 레거시
/// <c>MapObjM</c>의 맵 단위 브로드캐스트 계약(승계 판정)을 오브젝트 모델에서 떼어내
/// 독립 프리미티브로 만들었다 — 게임 룸뿐 아니라 채팅 채널·협업 문서·대시보드 구독에도
/// 같은 모양이 필요하다.
/// </para>
/// <para>
/// <b>동시성 설계 — 변경은 락, 읽기는 스냅샷.</b> 참가·퇴장은 서로 다른 커넥션의 실행
/// 문맥에서 오므로 동기화가 필요하다. 여기서는 <b>copy-on-write 배열</b>을 쓴다:
/// 브로드캐스트(핫패스)는 <see cref="Volatile"/> 읽기 한 번으로 일관된 스냅샷을 얻고
/// 락도 할당도 없다. 변경은 락 안에서 새 배열을 만든다 — 참가·퇴장은 접속 수명당 몇 번
/// 뿐인 저빈도 경로라 락이 정당하다(CLAUDE.md 하드 룰: 락 필요 사유 명시).
/// </para>
/// <para>
/// <b>해산 규약.</b> 해산은 되돌릴 수 없고, 이후의 참가는 <see cref="RoomJoinStatus.Disbanded"/>로
/// 거부된다. 멤버에게의 "룸이 닫혔다" 통지는 해산 <b>전에</b> 앱이 브로드캐스트한다 —
/// 룸은 싱크의 수명을 소유하지 않는다(싱크는 커넥션 수명에 속한다).
/// </para>
/// </remarks>
public sealed class Room
{
    private static readonly IRoomMemberSink[] EmptyMembers = [];

    private readonly object _mutationLock = new();
    private IRoomMemberSink[] _members = EmptyMembers;
    private volatile bool _disbanded;

    internal Room(RoomId id, int maxMembers)
    {
        Id = id;
        MaxMembers = maxMembers;
    }

    /// <summary>룸 식별자.</summary>
    public RoomId Id { get; }

    /// <summary>정원. 초과 참가는 거부된다(9.6).</summary>
    public int MaxMembers { get; }

    /// <summary>현재 멤버 수.</summary>
    public int MemberCount => Volatile.Read(ref _members).Length;

    /// <summary>해산됐는지 여부.</summary>
    public bool IsDisbanded => _disbanded;

    /// <summary>브로드캐스트용 멤버 스냅샷. 반환 배열은 불변으로 취급한다.</summary>
    internal IRoomMemberSink[] MembersSnapshot => Volatile.Read(ref _members);

    /// <summary>멤버를 참가시킨다.</summary>
    /// <param name="member">멤버의 수신 싱크. 커넥션당 하나여야 한다.</param>
    public RoomJoinStatus TryJoin(IRoomMemberSink member)
    {
        ArgumentNullException.ThrowIfNull(member);

        // 참가·퇴장은 저빈도 경로라 락을 쓴다. 핫패스(브로드캐스트)는 이 락을 지나지 않는다.
        lock (_mutationLock)
        {
            if (_disbanded)
            {
                return RoomJoinStatus.Disbanded;
            }

            IRoomMemberSink[] current = _members;
            if (current.Length >= MaxMembers)
            {
                return RoomJoinStatus.RoomFull;
            }

            foreach (IRoomMemberSink existing in current)
            {
                if (existing.ConnectionId == member.ConnectionId)
                {
                    return RoomJoinStatus.AlreadyJoined;
                }
            }

            var next = new IRoomMemberSink[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = member;
            Volatile.Write(ref _members, next);
            return RoomJoinStatus.Joined;
        }
    }

    /// <summary>멤버를 퇴장시킨다.</summary>
    /// <returns>멤버가 아니었으면 <see langword="false"/>.</returns>
    public bool TryLeave(ConnectionId connectionId)
    {
        lock (_mutationLock)
        {
            IRoomMemberSink[] current = _members;
            int index = -1;
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i].ConnectionId == connectionId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return false;
            }

            if (current.Length == 1)
            {
                Volatile.Write(ref _members, EmptyMembers);
                return true;
            }

            var next = new IRoomMemberSink[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref _members, next);
            return true;
        }
    }

    /// <summary>룸을 해산한다. 여러 번 불러도 안전하다.</summary>
    /// <returns>해산 시점의 멤버 수. 이미 해산됐으면 0.</returns>
    public int Disband()
    {
        lock (_mutationLock)
        {
            if (_disbanded)
            {
                return 0;
            }

            int count = _members.Length;
            _disbanded = true;
            Volatile.Write(ref _members, EmptyMembers);
            return count;
        }
    }
}
