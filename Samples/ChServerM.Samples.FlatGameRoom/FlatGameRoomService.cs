using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Sessions;
using ChServerM.Identity;
using ChServerM.RealTime.Rooms;
using ChServerM.Samples.FlatGameRoom.Messages;
using ChServerM.Serialization.FlatBuffers;
using ChServerM.Sessions;

namespace ChServerM.Samples.FlatGameRoom;

/// <summary>
/// 로그인 완료·룸 입장·채팅/이동 브로드캐스트·퇴장·세션 복원을 묶은 서비스 —
/// FlatBuffers 페이로드로 GameRoom 샘플의 룸 조립(Phase 18)에 인증·세션 축(Phase 9/13)을
/// 얹는 방법의 참조 구현이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 프레임워크는 부품을 준다 — 인증 미들웨어(T-20), 상태 필터(T-19),
/// 세션 재개 배선(ADR-0036), 룸/브로드캐스터(ADR-0064). "그 부품들이 앱 핸들러 안에서
/// 어떻게 이어지는가"는 조립하는 쪽의 몫으로 남고, 이 타입이 그 답의 본보기다.
/// 특히 <b>세션의 경계</b>를 코드로 보인다: <b>수립은 앱이</b>(누구에게 세션을 줄 것인가는
/// 정책 — <see cref="CompleteLoginAsync"/>), <b>재개는 프레임워크가</b>(토큰 대조는 메커니즘 —
/// <c>UseSessions</c> 가 배선하는 <see cref="SessionResumeDispatch.HandleResumeAsync"/>),
/// <b>재개 후 앱 상태 복원은 다시 앱이</b>(<see cref="TryRestoreAfterResumeAsync"/>) 한다.
/// </para>
/// <para>
/// <b>수명 규약 — 퇴장 경로는 세 갈래이고 전부 <see cref="LeaveCore"/> 로 모인다.</b>
/// (1) 명시적 <see cref="FlatGameRoomProtocol.LeaveRoom"/> 요청,
/// (2) 커넥션 종료(<see cref="ChServerM.Connections.IConnection.ConnectionClosed"/> 콜백),
/// (3) 배달 실패(<see cref="PartitionedMemberSinkOptions.OnDeliveryFaulted"/>).
/// 유령 멤버가 남으면 브로드캐스트가 죽은 파이프에 계속 쓴다 — 레거시가 정확히 이 형태로
/// 유저 수만큼 느려졌다. <see cref="LeaveCore"/> 는 멱등이라 세 경로가 겹쳐 불려도 안전하다.
/// 세션은 퇴장과 무관하게 저장소에 남는다 — 그것이 재접속 복구의 전제다(TTL 은 조립이 정한다).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 모든 핸들러는 파티션 실행 모델 위에서 돈다 — 같은 커넥션의 요청은
/// 순차, 다른 커넥션끼리는 병렬이다. 멤버십 사전은 여러 파티션이 동시에 만지므로
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 를 쓰고, 응답 직렬화 버퍼는 공유 필드가
/// 아니라 호출 지역 변수다(병렬 핸들러에서 공유 가변 버퍼는 데이터 경합이다).
/// </para>
/// </remarks>
internal sealed class FlatGameRoomService
{
    /// <summary>채팅 본문 최대 길이. 무제한 문자열을 브로드캐스트에 실어주지 않는다.</summary>
    private const int MaxChatTextLength = 512;

    /// <summary>좌표 절대값 상한 — 이 샘플 월드의 경계. 범위 밖 이동 보고는 버린다.</summary>
    private const float MaxCoordinate = 4096f;

    /// <summary>로그인 응답에 싣는 오늘의 메시지. 자체 검증이 왕복 보존을 단언한다.</summary>
    public const string Motd = "FlatBuffers 룸 서버에 온 것을 환영한다";

    // 응답·브로드캐스트 직렬화기. 전부 상태가 없으므로 공유해도 안전하다.
    private static readonly FlatSharpMessageSerializer<LoginReply> LoginReplySerializer = new(LoginReply.Serializer);
    private static readonly FlatSharpMessageSerializer<JoinRoomReply> JoinReplySerializer = new(JoinRoomReply.Serializer);
    private static readonly FlatSharpMessageSerializer<ChatBroadcast> ChatBroadcastSerializer = new(ChatBroadcast.Serializer);
    private static readonly FlatSharpMessageSerializer<MoveBroadcast> MoveBroadcastSerializer = new(MoveBroadcast.Serializer);
    private static readonly FlatSharpMessageSerializer<LeaveRoomReply> LeaveReplySerializer = new(LeaveRoomReply.Serializer);

    private readonly IFrameEncoder _encoder;
    private readonly PartitionedExecutionModel _executionModel;
    private readonly SessionResumeService _sessions;
    private readonly SessionResumeDispatch _sessionNotifier;
    private readonly RoomBroadcaster _broadcaster;
    private readonly ConcurrentDictionary<ConnectionId, RoomMembership> _memberships = new();

    /// <summary>세션 번호 발급 카운터. 로그인 1회 = 세션 1개이므로 충돌이 없다.</summary>
    private long _sessionSeed;

    private long _broadcastAccepted;
    private long _broadcastRejected;

    internal FlatGameRoomService(
        IFrameEncoder encoder,
        PartitionedExecutionModel executionModel,
        SessionResumeService sessions)
    {
        _encoder = encoder;
        _executionModel = executionModel;
        _sessions = sessions;

        // 수립 통지(40009) 전용으로 우리가 직접 만든다. 재개(40007) 배선은 UseSessions 가
        // 하지만, "세션을 수립해도 되는가"는 정책이라 프레임워크가 대신 판단하지 않는다 —
        // 그 판단을 내린 앱이 결과를 알릴 수단이 이 타입이다(ADR-0036).
        _sessionNotifier = new SessionResumeDispatch(sessions, encoder);

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

    /// <summary>인증 통과 후의 로그인 마무리 — 세션 수립·수립 통지·응답.</summary>
    /// <remarks>
    /// 이 메서드에 도달했다는 것은 <c>AuthenticationMiddleware</c> 가 자격을 이미 승인해
    /// 상태 전이까지 끝냈다는 뜻이다. 여기 남는 일은 정책의 나머지 절반이다:
    /// (1) 세션 수립(<see cref="SessionResumeService.TryCreateAsync"/> — 앱의 몫, ADR-0036),
    /// (2) 수립 통지(40009 — 클라이언트가 세션 식별자·최초 재개 토큰을 받는 유일한 경로),
    /// (3) FlatBuffers 응답. 통지를 응답보다 먼저 보낸다 — 클라이언트가 <c>LoginReply</c> 를
    /// 받은 시점에는 반드시 재개 자격도 손에 있어야 한다.
    /// </remarks>
    public async ValueTask CompleteLoginAsync(MessageContext context, LoginRequest request)
    {
        _ = request; // 자격 내용은 인증기가 이미 소비했다. 시그니처는 생성 등록 경로가 정한다.

        PlayerFeature player = context.Connection.Features.Get<PlayerFeature>()
            ?? throw new InvalidOperationException(
                "PlayerFeature 가 없다 — 인증 미들웨어 없이 로그인 핸들러가 실행됐다. 조립 순서를 확인한다.");

        // 세션 식별자는 로그인 인스턴스를 가리킨다(계정이 아니다). 단조 카운터라 충돌이 없고,
        // 같은 계정이 재로그인하면 새 세션이 생긴다 — 옛 세션은 TTL 로 소멸한다.
        SessionId sessionId = new(new ObjectId(Interlocked.Increment(ref _sessionSeed)));

        SessionBinding binding = await _sessions
            .TryCreateAsync(sessionId, EncodePlayerState(player), context.CancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("세션 식별자가 충돌했다 — 단조 카운터 발급에서는 일어날 수 없다.");

        // 수립 통지가 커넥션의 ISessionFeature 바인딩도 함께 세운다(SessionResumeDispatch 문서).
        await _sessionNotifier.WriteEstablishedAsync(context, sessionId, binding).ConfigureAwait(false);

        LoginReply reply = new() { Result = LoginResult.Ok, PlayerId = player.PlayerId, Motd = Motd };
        await ReplyAsync(context, FlatGameRoomProtocol.Login, LoginReplySerializer, reply).ConfigureAwait(false);
    }

    /// <summary>룸 입장을 시도하고 결과를 FlatBuffers 로 돌려보낸다.</summary>
    public async ValueTask JoinAsync(MessageContext context, JoinRoomRequest request)
    {
        (JoinRoomResult result, int memberCount) = Join(context, request);
        JoinRoomReply reply = new() { Result = result, MemberCount = memberCount };
        await ReplyAsync(context, FlatGameRoomProtocol.JoinRoom, JoinReplySerializer, reply).ConfigureAwait(false);
    }

    /// <summary>채팅을 같은 룸의 다른 멤버 전원에게 브로드캐스트한다.</summary>
    /// <remarks>
    /// 발신자에게는 응답하지 않는다(fire-and-forget). 발신자 이름·시각은 서버가 채운다 —
    /// 클라이언트가 보낸 값을 그대로 뿌리면 사칭과 시계 조작이 그대로 전파된다.
    /// </remarks>
    public ValueTask ChatAsync(MessageContext context, ChatSend request)
    {
        if (!_memberships.TryGetValue(context.Connection.Id, out RoomMembership? membership))
        {
            // 룸 밖에서 온 채팅 — 정상 클라이언트는 만들지 않는 순서이므로 조용히 버린다.
            // (로그인 전 차단은 상태 필터의 몫이고, 여기는 "로그인 후·입장 전" 창이다.)
            return ValueTask.CompletedTask;
        }

        string? text = request.Text;
        if (string.IsNullOrEmpty(text) || text.Length > MaxChatTextLength)
        {
            // 역직렬화 성공 ≠ 유효한 값. 프로덕션이라면 IMessageValidator 오버로드로
            // 핸들러 앞에서 거부하는 것이 정석이다(생성 등록 경로에는 검증기 자리가 아직 없다).
            return ValueTask.CompletedTask;
        }

        PlayerFeature? player = context.Connection.Features.Get<PlayerFeature>();
        if (player is null)
        {
            return ValueTask.CompletedTask;
        }

        ChatBroadcast broadcast = new()
        {
            SenderName = player.DisplayName,
            Text = text,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        BroadcastToRoom(membership.Room, FlatGameRoomProtocol.ChatBroadcast, ChatBroadcastSerializer,
            broadcast, exceptConnection: context.Connection.Id);
        return ValueTask.CompletedTask;
    }

    /// <summary>이동 보고를 검증하고 같은 룸의 다른 멤버에게 브로드캐스트한다.</summary>
    public ValueTask MoveAsync(MessageContext context, MoveUpdate request)
    {
        if (!_memberships.TryGetValue(context.Connection.Id, out RoomMembership? membership))
        {
            return ValueTask.CompletedTask;
        }

        // 원격 입력의 float 는 NaN/Infinity 를 실을 수 있다 — 그대로 뿌리면 수신자 전원의
        // 물리/보간 코드에 전파된다. 서버 검증이 브로드캐스트의 전제 조건이다.
        if (!float.IsFinite(request.X) || !float.IsFinite(request.Y) || !float.IsFinite(request.Heading)
            || Math.Abs(request.X) > MaxCoordinate || Math.Abs(request.Y) > MaxCoordinate
            || request.Heading is < 0f or >= 360f)
        {
            return ValueTask.CompletedTask;
        }

        PlayerFeature? player = context.Connection.Features.Get<PlayerFeature>();
        if (player is null)
        {
            return ValueTask.CompletedTask;
        }

        MoveBroadcast broadcast = new()
        {
            PlayerId = player.PlayerId,
            X = request.X,
            Y = request.Y,
            Heading = request.Heading,
        };

        BroadcastToRoom(membership.Room, FlatGameRoomProtocol.MoveBroadcast, MoveBroadcastSerializer,
            broadcast, exceptConnection: context.Connection.Id);
        return ValueTask.CompletedTask;
    }

    /// <summary>룸에서 나가고 결과를 돌려보낸다. 요청 시 남은 멤버에게 퇴장 통지를 뿌린다.</summary>
    public async ValueTask LeaveAsync(MessageContext context, LeaveRoomRequest request)
    {
        ConnectionId connectionId = context.Connection.Id;

        // 통지에 쓸 룸 참조를 퇴장 전에 확보한다 — LeaveCore 가 멤버십을 지우기 때문이다.
        _memberships.TryGetValue(connectionId, out RoomMembership? membership);

        bool left = LeaveCore(connectionId);

        if (left && request.NotifyOthers && membership is not null)
        {
            PlayerFeature? player = context.Connection.Features.Get<PlayerFeature>();
            ChatBroadcast notice = new()
            {
                SenderName = player?.DisplayName ?? "(알 수 없음)",
                Text = "룸에서 나갔다",
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // 발신자는 이미 룸에서 빠졌으므로 exceptConnection 은 방어적 중복이다.
            BroadcastToRoom(membership.Room, FlatGameRoomProtocol.ChatBroadcast, ChatBroadcastSerializer,
                notice, exceptConnection: connectionId);
        }

        LeaveRoomReply reply = new() { Result = left ? LeaveRoomResult.Left : LeaveRoomResult.NotInRoom };
        await ReplyAsync(context, FlatGameRoomProtocol.LeaveRoom, LeaveReplySerializer, reply).ConfigureAwait(false);
    }

    /// <summary>세션 재개 성공 직후 앱 상태(플레이어 신원)를 커넥션에 복원한다.</summary>
    /// <returns>복원했으면 <see langword="true"/> — 호출자가 상태 전이를 이어서 수행한다.</returns>
    /// <remarks>
    /// <para>
    /// <b>프레임워크와 앱의 경계가 여기다.</b> 재개(40007) 프레임 자체는 <c>UseSessions</c> 가
    /// 배선한 <see cref="SessionResumeDispatch"/> 가 처리한다 — 토큰 대조·회전·응답(40008)·
    /// <see cref="ISessionFeature"/> 바인딩까지가 프레임워크의 일이다. 그러나 세션에 담긴
    /// <b>앱 상태의 의미</b>(이 샘플에서는 플레이어 번호+이름)는 프레임워크가 알 수 없으므로,
    /// 복원은 앱이 한다. <see cref="SessionResumeStateBridge"/> 가 그 호출 지점이다.
    /// </para>
    /// <para>
    /// 재개가 거부됐으면 <see cref="ISessionFeature"/> 가 바인딩되지 않으므로 아무것도 하지
    /// 않는다 — 실패한 커넥션에 상태를 흘리면 그 커넥션이 남의 권한을 얻는다.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> TryRestoreAfterResumeAsync(MessageContext context)
    {
        ISessionFeature? session = context.Connection.Features.Get<ISessionFeature>();
        if (session is null || session.SessionId.IsNone)
        {
            return false;
        }

        ArrayBufferWriter<byte> state = new(64);
        SessionReadResult read = await _sessions
            .TryReadStateAsync(session.SessionId, state, context.CancellationToken)
            .ConfigureAwait(false);

        if (!read.Found || !TryDecodePlayerState(state.WrittenSpan, out long playerId, out string displayName))
        {
            // 재개와 이 읽기 사이에 세션이 만료·삭제된 극히 짧은 창. 복원 없이는 신원이
            // 없으므로 승격하지 않는다 — 다음 앱 메시지는 상태 필터가 거부한다.
            return false;
        }

        context.Connection.Features.Set(new PlayerFeature(playerId, displayName));
        return true;
    }

    private (JoinRoomResult Result, int MemberCount) Join(MessageContext context, JoinRoomRequest request)
    {
        if (request.RoomId == 0)
        {
            return (JoinRoomResult.InvalidRoomId, 0);
        }

        ConnectionId connectionId = context.Connection.Id;

        if (_memberships.ContainsKey(connectionId))
        {
            return (JoinRoomResult.AlreadyInRoom, 0);
        }

        RoomOpenStatus open = Directory.TryGetOrCreate(new RoomId(request.RoomId), out Room? room);
        if (open is not (RoomOpenStatus.Created or RoomOpenStatus.Existing))
        {
            return (JoinRoomResult.OpenFailed, 0);
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
            return (join switch
            {
                RoomJoinStatus.AlreadyJoined => JoinRoomResult.AlreadyInRoom,
                RoomJoinStatus.RoomFull => JoinRoomResult.RoomFull,
                _ => JoinRoomResult.Disbanded,
            }, 0);
        }

        RoomMembership membership = new(room);
        _memberships[connectionId] = membership;

        // 퇴장 경로 (2) — 커넥션이 닫히면 자동 퇴장. 토큰이 이미 취소돼 있으면 Register 가
        // 콜백을 즉시 실행하므로, 멤버십을 먼저 넣어야 그 즉시 실행이 정리를 완수한다.
        membership.ClosedCleanup = context.Connection.ConnectionClosed.Register(
            static state =>
            {
                (FlatGameRoomService service, ConnectionId id) = ((FlatGameRoomService, ConnectionId))state!;
                service.LeaveCore(id);
            },
            (this, connectionId));

        return (JoinRoomResult.Joined, room.MemberCount);
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

    /// <summary>응답 하나를 FlatBuffers 로 직렬화해 요청자의 파이프에 쓴다.</summary>
    /// <remarks>버퍼는 호출 지역 변수다 — 서로 다른 커넥션의 핸들러가 병렬로 돈다(모듈 주석).</remarks>
    private async ValueTask ReplyAsync<TReply>(
        MessageContext context,
        MessageId messageId,
        FlatSharpMessageSerializer<TReply> serializer,
        TReply reply)
        where TReply : class
    {
        ArrayBufferWriter<byte> buffer = new(256);
        serializer.Serialize(buffer, in reply);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output, _encoder, messageId, buffer.WrittenSpan,
            FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>메시지를 한 번만 직렬화·인코딩해 룸 전체에 뿌린다(1회 인코딩 계약, ADR-0064).</summary>
    private void BroadcastToRoom<TMessage>(
        Room room,
        MessageId messageId,
        FlatSharpMessageSerializer<TMessage> serializer,
        TMessage message,
        ConnectionId exceptConnection)
        where TMessage : class
    {
        ArrayBufferWriter<byte> buffer = new(256);
        serializer.Serialize(buffer, in message);

        RoomBroadcastResult result = _broadcaster.Broadcast(
            room, messageId, buffer.WrittenSpan, FrameFlags.None, exceptConnection: exceptConnection);

        Interlocked.Add(ref _broadcastAccepted, result.Accepted);
        Interlocked.Add(ref _broadcastRejected, result.Rejected);
    }

    /// <summary>세션에 저장할 앱 상태를 만든다: [8B 플레이어 번호 LE][UTF-8 표시 이름].</summary>
    /// <remarks>
    /// 세션 저장소 계약은 불투명 바이트다(ADR-0033) — 형식은 전적으로 앱이 정한다.
    /// 여기서는 재개 후 신원 복원에 필요한 최소만 담는다.
    /// </remarks>
    private static byte[] EncodePlayerState(PlayerFeature player)
    {
        byte[] name = Encoding.UTF8.GetBytes(player.DisplayName);
        byte[] state = new byte[sizeof(long) + name.Length];
        BinaryPrimitives.WriteInt64LittleEndian(state, player.PlayerId);
        name.CopyTo(state.AsSpan(sizeof(long)));
        return state;
    }

    /// <summary><see cref="EncodePlayerState"/> 의 역. 형식이 어긋나면 실패 값을 돌려준다.</summary>
    private static bool TryDecodePlayerState(ReadOnlySpan<byte> state, out long playerId, out string displayName)
    {
        playerId = 0;
        displayName = string.Empty;

        if (state.Length <= sizeof(long))
        {
            return false;
        }

        playerId = BinaryPrimitives.ReadInt64LittleEndian(state);
        displayName = Encoding.UTF8.GetString(state[sizeof(long)..]);
        return playerId > 0 && displayName.Length > 0;
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
