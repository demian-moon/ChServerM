using System;
using System.Buffers;
using ChServerM.Framing;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.RealTime.Rooms.Tests;

public sealed class RoomBroadcasterTests
{
    private static readonly MessageId TestMessage = new(100);

    private static (RoomBroadcaster Broadcaster, Room Room) Create(int maxMembers = 16)
    {
        var directory = new RoomDirectory(new RoomDirectoryOptions { MaxMembersPerRoom = maxMembers });
        directory.TryGetOrCreate(new RoomId(1), out Room? room);
        var broadcaster = new RoomBroadcaster(new FixedHeaderFrameEncoder(), ArrayPool<byte>.Shared);
        return (broadcaster, room!);
    }

    [Fact]
    public void 모든_멤버가_같은_와이어_바이트를_받는다()
    {
        (RoomBroadcaster broadcaster, Room room) = Create();
        var sinks = new RecordingSink[3];
        for (uint i = 0; i < sinks.Length; i++)
        {
            sinks[i] = new RecordingSink(new ConnectionId(i + 1, 1));
            room.TryJoin(sinks[i]);
        }

        byte[] payload = [1, 2, 3, 4, 5];
        RoomBroadcastResult result = broadcaster.Broadcast(room, TestMessage, payload, FrameFlags.None);

        Assert.Equal(3, result.Accepted);
        Assert.Equal(0, result.Rejected);

        byte[] first = Assert.Single(sinks[0].Delivered);
        foreach (RecordingSink sink in sinks)
        {
            Assert.Equal(first, Assert.Single(sink.Delivered)); // 헤더+페이로드가 완전히 동일 = 인코딩 1회의 증거
        }

        // 와이어 형식 검증: 실제 디코더가 그대로 읽을 수 있어야 한다.
        var decoder = new FixedHeaderFrameDecoder();
        FrameDecodeResult decoded = decoder.Decode(new ReadOnlySequence<byte>(first));
        Assert.Equal(FrameDecodeStatus.Decoded, decoded.Status);
        Assert.Equal(TestMessage, decoded.Envelope.MessageId);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public void 발신자는_제외된다()
    {
        (RoomBroadcaster broadcaster, Room room) = Create();
        var sender = new RecordingSink(new ConnectionId(1, 1));
        var other = new RecordingSink(new ConnectionId(2, 1));
        room.TryJoin(sender);
        room.TryJoin(other);

        RoomBroadcastResult result = broadcaster.Broadcast(
            room, TestMessage, [9], FrameFlags.None, exceptConnection: sender.ConnectionId);

        Assert.Equal(1, result.Accepted);
        Assert.Empty(sender.Delivered);
        Assert.Single(other.Delivered);
    }

    [Fact]
    public void 거부는_결과에_집계되고_나머지는_전달된다()
    {
        (RoomBroadcaster broadcaster, Room room) = Create();
        var full = new RecordingSink(new ConnectionId(1, 1), RoomDeliveryStatus.QueueFull);
        var ok = new RecordingSink(new ConnectionId(2, 1));
        room.TryJoin(full);
        room.TryJoin(ok);

        RoomBroadcastResult result = broadcaster.Broadcast(room, TestMessage, [7], FrameFlags.None);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.Single(ok.Delivered);
    }

    [Fact]
    public void 빈_룸_브로드캐스트는_무해하다()
    {
        (RoomBroadcaster broadcaster, Room room) = Create();

        RoomBroadcastResult result = broadcaster.Broadcast(room, TestMessage, [1], FrameFlags.None);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(0, result.Rejected);
    }

    [Fact]
    public void 프레임_재사용_중에도_내용이_오염되지_않는다()
    {
        // 참조 계수 회수가 틀리면 두 번째 브로드캐스트가 첫 번째의 버퍼를 덮어쓴다.
        (RoomBroadcaster broadcaster, Room room) = Create();
        var sink = new RecordingSink(new ConnectionId(1, 1));
        room.TryJoin(sink);

        broadcaster.Broadcast(room, TestMessage, [1, 1, 1], FrameFlags.None);
        broadcaster.Broadcast(room, TestMessage, [2, 2, 2, 2], FrameFlags.None);

        Assert.Equal(2, sink.Delivered.Count);
        Assert.Equal([1, 1, 1], sink.Delivered[0][^3..]);
        Assert.Equal([2, 2, 2, 2], sink.Delivered[1][^4..]);
    }

    [Fact]
    public void 큰_페이로드는_프레임_버퍼를_성장시킨다()
    {
        (RoomBroadcaster broadcaster, Room room) = Create();
        var sink = new RecordingSink(new ConnectionId(1, 1));
        room.TryJoin(sink);

        byte[] payload = new byte[64 * 1024];
        new Random(3).NextBytes(payload);

        RoomBroadcastResult result = broadcaster.Broadcast(
            room, TestMessage, payload, FrameFlags.None);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(payload, Assert.Single(sink.Delivered)[^payload.Length..]);
    }

    [Fact]
    public void 이중_해제는_즉시_드러난다()
    {
        // 이중 해제 = 풀 이중 반납. 조용히 넘어가면 다른 프레임의 데이터가 오염된다.
        (RoomBroadcaster broadcaster, Room room) = Create();
        BroadcastFrame? captured = null;
        var stealing = new CapturingSink(new ConnectionId(1, 1), frame => captured = frame);
        room.TryJoin(stealing);

        broadcaster.Broadcast(room, TestMessage, [1], FrameFlags.None);

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(captured.Release); // 싱크가 이미 한 번 해제했다
    }

    private sealed class CapturingSink : IRoomMemberSink
    {
        private readonly Action<BroadcastFrame> _capture;

        internal CapturingSink(ConnectionId id, Action<BroadcastFrame> capture)
        {
            ConnectionId = id;
            _capture = capture;
        }

        public ConnectionId ConnectionId { get; }

        public RoomDeliveryStatus TryDeliver(BroadcastFrame frame)
        {
            _capture(frame);
            frame.Release();
            return RoomDeliveryStatus.Accepted;
        }
    }
}
