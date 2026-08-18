using System;
using System.Threading.Tasks;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.RealTime.Rooms.Tests;

public sealed class RoomTests
{
    private static RecordingSink Sink(uint slot) => new(new ConnectionId(slot, 1));

    private static Room CreateRoom(int maxMembers = 8)
    {
        var directory = new RoomDirectory(new RoomDirectoryOptions { MaxMembersPerRoom = maxMembers });
        Assert.Equal(RoomOpenStatus.Created, directory.TryGetOrCreate(new RoomId(1), out Room? room));
        return room!;
    }

    [Fact]
    public void 참가_퇴장_생명주기가_동작한다()
    {
        Room room = CreateRoom();
        RecordingSink a = Sink(1);

        Assert.Equal(RoomJoinStatus.Joined, room.TryJoin(a));
        Assert.Equal(RoomJoinStatus.AlreadyJoined, room.TryJoin(Sink(1))); // 같은 커넥션
        Assert.Equal(1, room.MemberCount);

        Assert.True(room.TryLeave(a.ConnectionId));
        Assert.False(room.TryLeave(a.ConnectionId));
        Assert.Equal(0, room.MemberCount);
    }

    [Fact]
    public void 정원_초과는_거부된다()
    {
        Room room = CreateRoom(maxMembers: 2);

        Assert.Equal(RoomJoinStatus.Joined, room.TryJoin(Sink(1)));
        Assert.Equal(RoomJoinStatus.Joined, room.TryJoin(Sink(2)));
        Assert.Equal(RoomJoinStatus.RoomFull, room.TryJoin(Sink(3)));

        room.TryLeave(new ConnectionId(1, 1));
        Assert.Equal(RoomJoinStatus.Joined, room.TryJoin(Sink(3))); // 자리가 나면 다시 받는다
    }

    [Fact]
    public void 해산은_되돌릴_수_없다()
    {
        Room room = CreateRoom();
        room.TryJoin(Sink(1));
        room.TryJoin(Sink(2));

        // 해산은 그 시점의 멤버 스냅샷을 돌려준다 — 사전 통지와 해산 사이 창에 끼어든
        // 멤버까지 앱이 수습할 수 있게(감사 2026-08-18 R-6).
        IRoomMemberSink[] disbanded = room.Disband();
        Assert.Equal(2, disbanded.Length);
        Assert.True(room.IsDisbanded);
        Assert.Equal(0, room.MemberCount);
        Assert.Empty(room.Disband()); // 두 번째 해산은 무해
        Assert.Equal(RoomJoinStatus.Disbanded, room.TryJoin(Sink(3)));
    }

    [Fact]
    public void 디렉터리가_생성과_조회와_해산을_관장한다()
    {
        var directory = new RoomDirectory(new RoomDirectoryOptions { MaxRooms = 2 });

        Assert.Equal(RoomOpenStatus.InvalidId, directory.TryGetOrCreate(RoomId.None, out _));
        Assert.Equal(RoomOpenStatus.Created, directory.TryGetOrCreate(new RoomId(1), out Room? first));
        Assert.Equal(RoomOpenStatus.Existing, directory.TryGetOrCreate(new RoomId(1), out Room? again));
        Assert.Same(first, again);
        Assert.Equal(RoomOpenStatus.Created, directory.TryGetOrCreate(new RoomId(2), out _));
        Assert.Equal(RoomOpenStatus.LimitReached, directory.TryGetOrCreate(new RoomId(3), out _));

        Assert.True(directory.TryDisband(new RoomId(1)));
        Assert.True(first!.IsDisbanded);
        Assert.False(directory.TryGet(new RoomId(1), out _));
        Assert.Equal(RoomOpenStatus.Created, directory.TryGetOrCreate(new RoomId(3), out _)); // 자리 반환
    }

    [Fact]
    public async Task 동시_참가와_퇴장에도_멤버십이_일관된다()
    {
        // 반복 실행으로 경합을 노출한다(9.9 — 단발 테스트는 경합을 재현하지 않는다).
        for (int round = 0; round < 20; round++)
        {
            Room room = CreateRoom(maxMembers: 64);
            var tasks = new Task[16];
            for (int i = 0; i < tasks.Length; i++)
            {
                uint slot = (uint)(i + 1);
                tasks[i] = Task.Run(() =>
                {
                    var sink = new RecordingSink(new ConnectionId(slot, 1));
                    Assert.Equal(RoomJoinStatus.Joined, room.TryJoin(sink));
                    Assert.True(room.TryLeave(sink.ConnectionId));
                    Assert.Equal(RoomJoinStatus.Joined, room.TryJoin(sink));
                });
            }

            await Task.WhenAll(tasks);
            Assert.Equal(16, room.MemberCount);
        }
    }
}
