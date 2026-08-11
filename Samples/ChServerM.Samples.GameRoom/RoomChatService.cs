using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.RealTime.Rooms;

namespace ChServerM.Samples.GameRoom;

/// <summary>
/// 룸 입장·퇴장·채팅 브로드캐스트를 묶은 서비스 — 핸들러에서 룸/브로드캐스트 축
/// (<c>ChServerM.RealTime.Rooms</c>, Phase 18)을 조립하는 방법의 참조 구현이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> Phase 18 은 룸 축의 부품(<see cref="RoomDirectory"/> ·
/// <see cref="RoomBroadcaster"/> · <see cref="PartitionedMemberSink"/>)을 만들었지만,
/// "핸들러 안에서 그것들을 어떻게 잇는가"는 조립하는 쪽의 몫으로 남았다.
/// 이 타입이 그 답의 본보기다: 어떤 파티션을 싱크에 주는가, 퇴장 경로 세 갈래를
/// 어떻게 겹치지 않게 묶는가.
/// </para>
/// <para>
/// <b>수명 규약 — 퇴장 경로는 세 갈래이고 전부 <see cref="LeaveCore"/> 로 모인다.</b>
/// (1) 명시적 <see cref="GameRoomProtocol.Leave"/> 요청,
/// (2) 커넥션 종료(<see cref="ChServerM.Connections.IConnection.ConnectionClosed"/> 콜백),
/// (3) 배달 실패(<see cref="PartitionedMemberSinkOptions.OnDeliveryFaulted"/>).
/// 유령 멤버가 남으면 브로드캐스트가 죽은 파이프에 계속 쓴다 — 레거시가 정확히 이 형태로
/// 유저 수만큼 느려졌다. <see cref="LeaveCore"/> 는 멱등이라 세 경로가 겹쳐 불려도 안전하다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 모든 핸들러는 파티션 실행 모델 위에서 돈다 — 같은 커넥션의 요청은
/// 순차, 다른 커넥션끼리는 병렬이다. 따라서 커넥션 하나의 입장/퇴장은 경합하지 않지만
/// 멤버십 사전 자체는 여러 파티션이 동시에 만지므로 <see cref="ConcurrentDictionary{TKey,TValue}"/> 를 쓴다.
/// <see cref="Room"/> 과 <see cref="RoomBroadcaster"/> 는 자체적으로 스레드 안전하다(Phase 18 계약).
/// </para>
/// </remarks>
internal sealed class RoomChatService
{
    private readonly IFrameEncoder _encoder;
    private readonly PartitionedExecutionModel _executionModel;
    private readonly RoomBroadcaster _broadcaster;
    private readonly ConcurrentDictionary<ConnectionId, RoomMembership> _memberships = new();

    private long _broadcastAccepted;
    private long _broadcastRejected;

    internal RoomChatService(IFrameEncoder encoder, PartitionedExecutionModel executionModel)
    {
        _encoder = encoder;
        _executionModel = executionModel;
        Directory = new RoomDirectory();

        // 풀은 명시 인자다(ADR-0051) — "최악 몇 바이트가 대여 중인가"를 조립하는 쪽이
        // 계산해야 하기 때문이다. 이 샘플의 최악치: 송신 큐 깊이(기본값) × 멤버 수 × 프레임 크기.
        _broadcaster = new RoomBroadcaster(encoder, ArrayPool<byte>.Shared);
    }

    /// <summary>룸 디렉터리. 자체 검증이 서버 내부 상태를 단언할 때 쓴다.</summary>
    internal RoomDirectory Directory { get; }

    /// <summary>브로드캐스트로 수락된 누적 배달 수.</summary>
    internal long BroadcastAccepted => Interlocked.Read(ref _broadcastAccepted);

    /// <summary>브로드캐스트에서 거부된 누적 배달 수. 조용한 유실은 관측되지 않으면 존재하지 않는 것과 같다(9.6).</summary>
    internal long BroadcastRejected => Interlocked.Read(ref _broadcastRejected);

    /// <summary>8바이트 룸 번호를 읽어 입장을 시도하고 결과 코드를 돌려보낸다.</summary>
    public async ValueTask<DispatchStatus> HandleJoinAsync(MessageContext context)
    {
        byte result = Join(context);
        await ReplyAsync(context, GameRoomProtocol.Join, result).ConfigureAwait(false);
        return DispatchStatus.Handled;
    }

    /// <summary>텍스트 페이로드를 같은 룸의 다른 멤버 전원에게 브로드캐스트한다.</summary>
    /// <remarks>
    /// 발신자에게는 응답하지 않는다(fire-and-forget). 수신 확인이 필요한 프로토콜이라면
    /// 여기서 <see cref="RoomBroadcastResult"/> 의 수락 수를 돌려주면 된다.
    /// </remarks>
    public ValueTask<DispatchStatus> HandleChatAsync(MessageContext context)
    {
        if (!_memberships.TryGetValue(context.Connection.Id, out RoomMembership? membership))
        {
            // 룸 밖에서 온 채팅 — 처리 대상 아님을 상태 코드로 남긴다(집계는 디스패처 몫).
            return new ValueTask<DispatchStatus>(DispatchStatus.RejectedByState);
        }

        ReadOnlySequence<byte> payload = context.Payload;
        RoomBroadcastResult result;

        if (payload.IsSingleSegment)
        {
            result = _broadcaster.Broadcast(
                membership.Room, GameRoomProtocol.Chat, payload.FirstSpan,
                FrameFlags.None, exceptConnection: context.Connection.Id);
        }
        else
        {
            // 파이프 경계에 걸린 멀티 세그먼트 페이로드 — 브로드캐스터는 연속 스팬을 받으므로
            // 풀에서 빌려 평탄화한다. 반납은 finally 로 — 예외 하나가 풀을 새게 하면 안 된다.
            int length = checked((int)payload.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                payload.CopyTo(rented);
                result = _broadcaster.Broadcast(
                    membership.Room, GameRoomProtocol.Chat, rented.AsSpan(0, length),
                    FrameFlags.None, exceptConnection: context.Connection.Id);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        Interlocked.Add(ref _broadcastAccepted, result.Accepted);
        Interlocked.Add(ref _broadcastRejected, result.Rejected);

        return new ValueTask<DispatchStatus>(DispatchStatus.Handled);
    }

    /// <summary>룸에서 나가고 결과 코드를 돌려보낸다.</summary>
    public async ValueTask<DispatchStatus> HandleLeaveAsync(MessageContext context)
    {
        byte result = LeaveCore(context.Connection.Id) ? LeaveResult.Left : LeaveResult.NotInRoom;
        await ReplyAsync(context, GameRoomProtocol.Leave, result).ConfigureAwait(false);
        return DispatchStatus.Handled;
    }

    private byte Join(MessageContext context)
    {
        if (context.Payload.Length != sizeof(ulong))
        {
            return JoinResult.MalformedPayload;
        }

        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        context.Payload.CopyTo(buffer);
        RoomId roomId = new(BinaryPrimitives.ReadUInt64LittleEndian(buffer));

        ConnectionId connectionId = context.Connection.Id;

        if (_memberships.ContainsKey(connectionId))
        {
            return JoinResult.AlreadyInRoom;
        }

        RoomOpenStatus open = Directory.TryGetOrCreate(roomId, out Room? room);
        if (open is not (RoomOpenStatus.Created or RoomOpenStatus.Existing))
        {
            return JoinResult.OpenFailed;
        }

        // 싱크에는 반드시 "그 커넥션의" 파티션을 준다 — 커넥션 파티션의 배타 슬롯이
        // 파이프 쓰기의 소유권 근거이기 때문이다(ADR-0064). 다른 파티션을 주면
        // 핸들러와 브로드캐스트가 같은 파이프에 동시에 쓴다.
        PartitionedMemberSink sink = new(
            context.Connection,
            _executionModel.GetPartition(connectionId.ToPartitionKey()),
            new PartitionedMemberSinkOptions
            {
                // 퇴장 경로 (3) — 배달이 고장난 멤버는 자동 퇴장시킨다(모듈 주석의 수명 규약).
                OnDeliveryFaulted = faulted => LeaveCore(faulted),
            });

        RoomJoinStatus join = room!.TryJoin(sink);
        if (join != RoomJoinStatus.Joined)
        {
            return join switch
            {
                RoomJoinStatus.AlreadyJoined => JoinResult.AlreadyInRoom,
                RoomJoinStatus.RoomFull => JoinResult.RoomFull,
                _ => JoinResult.Disbanded,
            };
        }

        RoomMembership membership = new(room);
        _memberships[connectionId] = membership;

        // 퇴장 경로 (2) — 커넥션이 닫히면 자동 퇴장. 토큰이 이미 취소돼 있으면 Register 가
        // 콜백을 즉시 실행하므로, 멤버십을 먼저 넣어야 그 즉시 실행이 정리를 완수한다.
        membership.ClosedCleanup = context.Connection.ConnectionClosed.Register(
            static state =>
            {
                (RoomChatService service, ConnectionId id) = ((RoomChatService, ConnectionId))state!;
                service.LeaveCore(id);
            },
            (this, connectionId));

        return JoinResult.Joined;
    }

    /// <summary>세 갈래 퇴장 경로의 합류점. 멱등 — 이미 나간 커넥션이면 아무것도 하지 않는다.</summary>
    private bool LeaveCore(ConnectionId connectionId)
    {
        if (!_memberships.TryRemove(connectionId, out RoomMembership? membership))
        {
            return false;
        }

        membership.ClosedCleanup.Dispose();
        membership.Room.TryLeave(connectionId);
        return true;
    }

    private async ValueTask ReplyAsync(MessageContext context, MessageId messageId, byte status)
    {
        // 1바이트 응답이라 할당이 사소하지만, 핫패스라면 상태 코드별 정적 배열로 없앨 수 있다.
        byte[] payload = [status];

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output, _encoder, messageId, payload,
            FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>커넥션 하나의 룸 소속. 종료 콜백 등록을 함께 들어 퇴장 시 해제한다.</summary>
    private sealed class RoomMembership(Room room)
    {
        /// <summary>소속 룸.</summary>
        public Room Room { get; } = room;

        /// <summary>커넥션 종료 콜백 등록. <see cref="LeaveCore"/> 가 해제한다.</summary>
        public CancellationTokenRegistration ClosedCleanup { get; set; }
    }
}
