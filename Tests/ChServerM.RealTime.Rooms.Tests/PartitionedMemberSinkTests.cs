using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using ChServerM.Framing;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.RealTime.Rooms.Tests;

public sealed class PartitionedMemberSinkTests
{
    private static readonly MessageId TestMessage = new(200);

    private static (RoomBroadcaster Broadcaster, Room Room) CreateRoom()
    {
        var directory = new RoomDirectory();
        directory.TryGetOrCreate(new RoomId(1), out Room? room);
        return (new RoomBroadcaster(new FixedHeaderFrameEncoder(), ArrayPool<byte>.Shared), room!);
    }

    [Fact]
    public async Task 브로드캐스트가_커넥션_파이프까지_닿는다()
    {
        (RoomBroadcaster broadcaster, Room room) = CreateRoom();
        await using var connection = new PipeConnection(slot: 1);
        var partition = new InlinePartition();
        var sink = new PartitionedMemberSink(connection, partition);
        room.TryJoin(sink);

        byte[] payload = [10, 20, 30];
        RoomBroadcastResult result = broadcaster.Broadcast(room, TestMessage, payload, FrameFlags.None);
        Assert.Equal(1, result.Accepted);

        ReadResult read = await connection.Reader.ReadAsync();
        var decoder = new FixedHeaderFrameDecoder();
        FrameDecodeResult decoded = decoder.Decode(read.Buffer);
        Assert.Equal(FrameDecodeStatus.Decoded, decoded.Status);
        Assert.Equal(TestMessage, decoded.Envelope.MessageId);
        Assert.Equal(payload, decoded.Payload.ToArray());
        connection.Reader.AdvanceTo(decoded.Consumed);
    }

    [Fact]
    public async Task 연속_브로드캐스트가_순서대로_도착한다()
    {
        (RoomBroadcaster broadcaster, Room room) = CreateRoom();
        await using var connection = new PipeConnection(slot: 1);
        var sink = new PartitionedMemberSink(connection, new InlinePartition());
        room.TryJoin(sink);

        for (byte i = 1; i <= 5; i++)
        {
            broadcaster.Broadcast(room, TestMessage, [i], FrameFlags.None);
        }

        ReadResult read = await connection.Reader.ReadAsync();
        var decoder = new FixedHeaderFrameDecoder();
        ReadOnlySequence<byte> buffer = read.Buffer;
        for (byte i = 1; i <= 5; i++)
        {
            FrameDecodeResult decoded = decoder.Decode(buffer);
            Assert.Equal(FrameDecodeStatus.Decoded, decoded.Status);
            Assert.Equal(new[] { i }, decoded.Payload.ToArray()); // FIFO — 배타 슬롯 직렬화의 증거
            buffer = buffer.Slice(decoded.Consumed);
        }
    }

    [Fact]
    public async Task 큐_포화는_거부되고_관측된다()
    {
        (RoomBroadcaster broadcaster, Room room) = CreateRoom();
        await using var connection = new PipeConnection(slot: 1);
        var partition = new InlinePartition { RejectEnqueue = false };
        // 드레인이 절대 돌지 않게 만들어 큐를 채운다: 파티션이 거부하는 대신,
        // 예약 자체를 막을 수는 없으므로 깊이 1 큐 + 수동 파티션으로 재현한다.
        var manual = new ManualPartition();
        var sink = new PartitionedMemberSink(connection, manual, new PartitionedMemberSinkOptions
        {
            SendQueueDepth = 1,
        });
        room.TryJoin(sink);

        Assert.Equal(1, broadcaster.Broadcast(room, TestMessage, [1], FrameFlags.None).Accepted);
        RoomBroadcastResult second = broadcaster.Broadcast(room, TestMessage, [2], FrameFlags.None);
        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Rejected); // TryWrite 의 false 가 조용히 사라지지 않는다

        await manual.RunPendingAsync(); // 이제 드레인을 돌리면 첫 프레임은 도착한다
        ReadResult read = await connection.Reader.ReadAsync();
        Assert.False(read.Buffer.IsEmpty);
    }

    [Fact]
    public async Task 상대가_닫히면_싱크가_사망하고_통지된다()
    {
        (RoomBroadcaster broadcaster, Room room) = CreateRoom();
        await using var connection = new PipeConnection(slot: 1);
        ConnectionId? faulted = null;
        var sink = new PartitionedMemberSink(connection, new InlinePartition(), new PartitionedMemberSinkOptions
        {
            OnDeliveryFaulted = id => faulted = id,
        });
        room.TryJoin(sink);

        connection.CompleteReader(); // 수신자가 파이프를 닫았다 — FlushResult.IsCompleted 경로

        broadcaster.Broadcast(room, TestMessage, [1], FrameFlags.None);

        Assert.True(sink.IsFaulted, "FlushResult 를 버리면 죽은 커넥션이 살아 있는 척한다(ADR-0051)");
        Assert.Equal(connection.Id, faulted);
        Assert.Equal(RoomDeliveryStatus.Closed, sink.TryDeliver(MakeThrowawayFrame()));
    }

    [Fact]
    public async Task 파티션_종료_중이면_싱크가_사망한다()
    {
        await using var connection = new PipeConnection(slot: 1);
        var sink = new PartitionedMemberSink(connection, new InlinePartition { RejectEnqueue = true });

        Assert.Equal(RoomDeliveryStatus.Accepted, sink.TryDeliver(MakeThrowawayFrame()));
        Assert.True(sink.IsFaulted);
    }

    private static BroadcastFrame MakeThrowawayFrame()
    {
        var broadcaster = new RoomBroadcaster(new FixedHeaderFrameEncoder(), ArrayPool<byte>.Shared);
        var directory = new RoomDirectory();
        directory.TryGetOrCreate(new RoomId(9), out Room? room);
        var capture = new FrameCapture();
        room!.TryJoin(capture);
        broadcaster.Broadcast(room, TestMessage, [0], FrameFlags.None);
        return capture.Frame!;
    }

    /// <summary>프레임을 붙들어 두는 싱크. 해제하지 않고 소유권을 테스트로 넘긴다.</summary>
    private sealed class FrameCapture : IRoomMemberSink
    {
        internal BroadcastFrame? Frame;

        public ConnectionId ConnectionId => new(99, 1);

        public RoomDeliveryStatus TryDeliver(BroadcastFrame frame)
        {
            Frame = frame;
            return RoomDeliveryStatus.Accepted; // 참조를 보유한 채 반환 — 테스트가 소유자다.
        }
    }

    /// <summary>예약을 쌓아 두고 테스트가 원할 때 실행하는 파티션.</summary>
    private sealed class ManualPartition : ChServerM.Execution.IExecutionPartition
    {
        private readonly System.Collections.Generic.Queue<ChServerM.Execution.IPartitionExclusiveWork> _pending = new();

        public int Index => 0;

        public TaskScheduler Scheduler => TaskScheduler.Default;

        public bool TryPost<TWork>(in TWork work) where TWork : struct, ChServerM.Execution.IPartitionWork
        {
            work.Execute();
            return true;
        }

        public bool TryEnqueueExclusive(ChServerM.Execution.IPartitionExclusiveWork work)
        {
            _pending.Enqueue(work);
            return true;
        }

        internal async Task RunPendingAsync()
        {
            while (_pending.Count > 0)
            {
                await _pending.Dequeue().ExecuteAsync();
            }
        }
    }
}
