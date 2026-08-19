using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Dispatch;
using ChServerM.Dispatch.Generated;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Hosting.Sessions;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.RealTime.Rooms;
using ChServerM.Samples.FlatGameRoom.Messages;
using ChServerM.Serialization.FlatBuffers;
using ChServerM.Sessions;
using ChServerM.Transport.Tcp;

namespace ChServerM.Samples.FlatGameRoom;

/// <summary>
/// FlatBuffers 게임 룸 예제 — 로그인·세션(재개 토큰)·룸 채팅·실데이터 페이로드를 한 조립에
/// 총망라한다. GameRoom 샘플(룸 축)에 인증·상태 필터·세션 축을 얹고 직렬화를 FlatSharp 로 바꿨다.
/// </summary>
/// <remarks>
/// <para>조합: TCP + 고정 헤더 프레이밍 + 파티션 실행 모델 + FlatSharp 직렬화
/// + 인증/상태 필터 미들웨어 + 세션(InMemory 저장소) + <c>ChServerM.RealTime.Rooms</c>.</para>
/// <para>이 프로그램이 실증하는 것.</para>
/// <list type="number">
///   <item><description><b>미들웨어 보안 체인</b> — 상태 필터(T-19, 기본 거부)가 로그인 전
///     앱 메시지를 커넥션 종료로 거부하고, 인증(T-20)의 성공만이 상태를 전이시킨다.
///     ⚠ 필터 화이트리스트에 세션 재개(40007)를 빠뜨리면 재개가 영영 거부된다(감사 H-7) —
///     <see cref="BuildServer"/> 의 <c>Allow</c> 목록 참조.</description></item>
///   <item><description><b>세션 수립·재개의 경계</b> — 수립은 앱(로그인 핸들러)이,
///     재개(토큰 대조·회전·40008 응답)는 프레임워크(<c>UseSessions</c>)가, 재개 후 앱 상태
///     복원은 다시 앱(<see cref="SessionResumeStateBridge"/>)이 한다(ADR-0036).</description></item>
///   <item><description><b>FlatBuffers 실데이터 왕복</b> — 요청·응답·브로드캐스트 전부가
///     FlatSharp Greedy 직렬화이고, 생성 등록 경로(<c>[MessageHandler]</c> +
///     <c>MapGeneratedHandlers</c>)에 제공자만 갈아 끼웠다(ADR-0012/0014).</description></item>
/// </list>
/// <para>
/// 인자 없이 실행하면 자체 검증(로그인→룸→채팅/이동→재접속·재개→퇴장 시나리오)을 돌고
/// 종료한다(CI 용). <c>--serve [포트]</c> 로 실행하면 TCP 서버로 계속 떠 있는다.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>이 샘플이 허용하는 최대 페이로드.</summary>
    private const int MaxPayload = 64 * 1024;

    /// <summary>헤더까지 포함한 최대 프레임 크기.</summary>
    private const int MaxFrame = MaxPayload + FrameHeader.Size;

    private static FramingOptions CreateFramingOptions() => new() { MaxPayloadLength = MaxPayload };

    /// <summary>전송 임계값은 최대 프레임보다 커야 한다 — 어긋난 조합은 <c>Build()</c> 가 막는다(ADR-0007).</summary>
    private static TcpTransportOptions CreateTcpOptions() => new()
    {
        PauseWriterThreshold = 2L * MaxFrame,
        ResumeWriterThreshold = MaxFrame,
    };

    private static async Task<int> Main(string[] args)
    {
        UseUtf8Console();

        if (args.Length > 0 && string.Equals(args[0], "--serve", StringComparison.Ordinal))
        {
            int port = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 5000;
            await ServeAsync(port).ConfigureAwait(false);
            return 0;
        }

        return await SelfTestAsync().ConfigureAwait(false);
    }

    /// <summary>콘솔 출력을 UTF-8 로 맞춘다. (EchoServer 와 같은 이유 — 리다이렉트 환경에서는 삼킨다.)</summary>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // 콘솔이 없거나 리다이렉트됐다.
        }
        catch (PlatformNotSupportedException)
        {
            // 이 플랫폼은 인코딩 변경을 지원하지 않는다.
        }
    }

    /// <summary>FlatBuffers 룸 서버를 조립한다. 자체 검증과 <c>--serve</c> 가 같은 조립을 쓴다.</summary>
    /// <remarks>
    /// <para>미들웨어 순서는 보안 경계다: <b>상태 필터 → 인증 → 재개 브리지</b>.
    /// 필터·인증의 역순은 <c>Build()</c> 가 조립 시점 예외로 거부한다.</para>
    /// <para>직렬화 제공자에는 <b>요청 타입만</b> 등록한다 — 제공자는 수신(역직렬화) 경로에
    /// 쓰이고, 응답·브로드캐스트 직렬화는 핸들러가 직접 한다(StatelessWeb 과 같은 패턴).</para>
    /// </remarks>
    private static ChServerMServer BuildServer(
        IPEndPoint listen,
        FixedHeaderFrameEncoder encoder,
        FixedHeaderFrameDecoder decoder,
        PartitionedExecutionModel executionModel,
        SessionResumeService sessions,
        FlatGameRoomService service)
    {
        FlatSharpMessageSerializerProvider serializers = new FlatSharpMessageSerializerProvider()
            .Register(LoginRequest.Serializer)
            .Register(JoinRoomRequest.Serializer)
            .Register(ChatSend.Serializer)
            .Register(MoveUpdate.Serializer)
            .Register(LeaveRoomRequest.Serializer);

        // 기본 거부 화이트리스트(T-19). 여기 없는 메시지는 어떤 상태에서도 커넥션 종료다.
        MessageStateFilterOptions filterOptions = new() { InitialStates = ConnectionStates.Connected };
        filterOptions
            // 연결 직후 허용되는 것은 딱 둘 — 로그인과 세션 재개다.
            .Allow(FlatGameRoomProtocol.Login, ConnectionStates.Connected)
            // ⚠ 함정(감사 H-7): UseSessions 가 40007 라우팅을 자동 배선해도, 이 Allow 가
            // 없으면 필터의 기본 거부가 먼저 걸려 재개 요청이 커넥션 종료가 된다.
            // 재로그인 차단과 같은 이유로 LoggedIn 상태에서는 재개도 허용하지 않는다.
            .Allow(FrameworkMessageIds.SessionResume, ConnectionStates.Connected)
            .Allow(FlatGameRoomProtocol.JoinRoom, ConnectionStates.LoggedIn)
            .Allow(FlatGameRoomProtocol.ChatSend, ConnectionStates.LoggedIn)
            .Allow(FlatGameRoomProtocol.MoveUpdate, ConnectionStates.LoggedIn)
            .Allow(FlatGameRoomProtocol.LeaveRoom, ConnectionStates.LoggedIn);

        return new ServerBuilder()
            .UseTransport(new TcpServerTransport(listen, CreateTcpOptions()))
            .UseFraming(decoder, encoder)
            // 룸 축은 파티션 실행 모델을 전제한다 — 커넥션 파티션의 배타 슬롯이
            // 브로드캐스트 쓰기의 소유권 근거다(ADR-0064).
            .UseExecutionModel(executionModel)
            // 세션 축 — 재개 예약 메시지(40007)가 자동 배선된다. 수립은 앱의 몫이다(ADR-0036).
            .UseSessions(sessions)
            .ConfigureDispatcher(dispatcher => dispatcher
                .Use(new MessageStateFilterMiddleware(filterOptions))
                .Use(new AuthenticationMiddleware(
                    new AuthenticationOptions { CredentialMessageId = FlatGameRoomProtocol.Login },
                    new DemoAuthenticator()))
                .Use(new SessionResumeStateBridge(service))
                // [MessageHandler] 선언에서 생성된 등록 — 손으로 쓴 Map 이 아니다(ADR-0014).
                // 직렬화 제공자만 FlatSharp 로 갈아 끼웠다(EchoServer=MemoryPack, StatelessWeb=Protobuf).
                .MapGeneratedHandlers(
                    serializers,
                    handler1: new LoginHandler(service),
                    handler2: new JoinRoomHandler(service),
                    handler3: new ChatSendHandler(service),
                    handler5: new MoveUpdateHandler(service),
                    handler7: new LeaveRoomHandler(service)))
            .Build();
    }

    /// <summary>TCP FlatBuffers 룸 서버로 계속 떠 있는다.</summary>
    private static async Task ServeAsync(int port)
    {
        FramingOptions framing = CreateFramingOptions();
        FixedHeaderFrameEncoder encoder = new(framing);
        FixedHeaderFrameDecoder decoder = new(framing);

        await using PartitionedExecutionModel executionModel = new();

        // 세션 저장소의 소유권은 조립하는 쪽에 있다(SessionResumeService 문서) — 여기서 만들고
        // 여기서 닫는다. TTL 30분 = 끊긴 클라이언트가 돌아올 수 있는 시간의 상한.
        using InMemorySessionStore store = new();
        SessionResumeService sessions = new(store, TimeSpan.FromMinutes(30));
        FlatGameRoomService service = new(encoder, executionModel, sessions);

        await using ChServerMServer server = BuildServer(
            new IPEndPoint(IPAddress.Any, port), encoder, decoder, executionModel, sessions, service);

        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"FlatBuffers 룸 서버 시작: {server.LocalEndPoint}");
        Console.WriteLine($"데모 로그인: 표시 이름 + 공유 비밀 \"{DemoAuthenticator.SharedSecret}\"");
        Console.WriteLine("Ctrl+C 로 종료한다.");

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // 기본 동작(즉시 종료)을 막고 정상 종료 경로를 탄다.
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료 요청.
        }

        await server.UnbindAsync().ConfigureAwait(false);

        using CancellationTokenSource drain = new(TimeSpan.FromSeconds(10));
        await server.StopAsync(drain.Token).ConfigureAwait(false);

        Console.WriteLine(
            $"룸 {service.Directory.RoomCount}개, 세션 {store.Count}개, "
            + $"배달 수락 {service.BroadcastAccepted}건 / 거부 {service.BroadcastRejected}건");
    }

    /// <summary>
    /// 자체 검증 — 로그인 전 거부(음성) → 로그인 → 룸 입장 → 채팅/이동 브로드캐스트 →
    /// 재접속 + 세션 재개(토큰 회전) → 퇴장까지 한 시나리오로 검증한다.
    /// </summary>
    /// <returns>모두 통과하면 0.</returns>
    private static async Task<int> SelfTestAsync()
    {
        Console.WriteLine("ChServerM FlatBuffers 게임 룸 샘플 — 인증·세션 재개·룸 브로드캐스트 총망라.");
        Console.WriteLine();

        // 테스트 전체가 이 시간 안에 끝나야 한다. 어긋난 조립은 걸려서 멈추는 것이 아니라
        // 실패로 드러나야 한다.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        CancellationToken ct = timeout.Token;

        FramingOptions framing = CreateFramingOptions();
        FixedHeaderFrameEncoder encoder = new(framing);
        FixedHeaderFrameDecoder decoder = new(framing);

        await using PartitionedExecutionModel executionModel = new(new PartitionedExecutionOptions
        {
            PartitionCount = Math.Min(4, Environment.ProcessorCount),
        });

        // 자체 검증은 수 초 안에 끝난다 — 청소 타이머(SweepInterval)도 TTL 도 필요 없다.
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService sessions = new(store);
        FlatGameRoomService service = new(encoder, executionModel, sessions);

        // 포트 0 → OS 가 배정한다. 하드코딩하면 병렬 실행에서 충돌한다.
        await using ChServerMServer server = BuildServer(
            new IPEndPoint(IPAddress.Loopback, 0), encoder, decoder, executionModel, sessions, service);
        await server.StartAsync().ConfigureAwait(false);

        EndPoint target = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        bool ok = true;

        // ── (1) 음성 검증 — 로그인 전 앱 메시지는 상태 필터가 커넥션 종료로 거부한다(T-19).
        await using (FlatGameClient intruder =
            await FlatGameClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false))
        {
            await intruder.SendJoinAsync(100, ct).ConfigureAwait(false);
            ok &= Check(
                await intruder.WaitClosedAsync(ct).ConfigureAwait(false),
                "로그인 전 Join 은 상태 필터가 커넥션을 닫는다 (기본 거부)");
            ok &= Check(!intruder.HasPendingFrames, "거부된 요청에는 응답 프레임이 없다");
        }

        // ── (2) 로그인 — FlatBuffers 자격 왕복 + 세션 수립 통지(40009).
        await using FlatGameClient a =
            await FlatGameClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false);
        await using FlatGameClient b =
            await FlatGameClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false);

        (long playerA, string motdA, long sessionA, byte[] tokenA) =
            await a.LoginAsync("아리", DemoAuthenticator.SharedSecret, ct).ConfigureAwait(false);
        (long playerB, string motdB, long sessionB, byte[] tokenB) =
            await b.LoginAsync("바다", DemoAuthenticator.SharedSecret, ct).ConfigureAwait(false);

        ok &= Check(motdA == FlatGameRoomService.Motd && motdB == FlatGameRoomService.Motd,
            "로그인 응답(LoginReply)의 MOTD 가 FlatBuffers 왕복을 보존한다");
        ok &= Check(playerA > 0 && playerB > 0 && playerA != playerB, "플레이어 번호가 발급되고 서로 다르다");
        ok &= Check(sessionA > 0 && sessionB > 0 && sessionA != sessionB, "세션 수립 통지(40009)로 세션 번호를 받는다");
        ok &= Check(tokenA.Length == SessionHandshakeCodec.TokenLength, "최초 재개 토큰(32B)을 받는다");
        _ = tokenB; // B 의 토큰은 시나리오에서 쓰지 않는다 — 수립 통지 수신 자체가 검증이다.

        // ── (3) 룸 입장.
        JoinRoomReply joinA = await a.JoinAsync(100, ct).ConfigureAwait(false);
        JoinRoomReply joinB = await b.JoinAsync(100, ct).ConfigureAwait(false);
        ok &= Check(joinA.Result == JoinRoomResult.Joined && joinA.MemberCount == 1, "A 가 룸 100 에 입장한다 (인원 1)");
        ok &= Check(joinB.Result == JoinRoomResult.Joined && joinB.MemberCount == 2, "B 가 룸 100 에 입장한다 (인원 2)");

        JoinRoomReply joinDup = await a.JoinAsync(100, ct).ConfigureAwait(false);
        ok &= Check(joinDup.Result == JoinRoomResult.AlreadyInRoom, "중복 입장은 거부된다");

        // ── (4) 채팅 브로드캐스트 — 발신자·본문·서버 시각 검증.
        long chatSentAfter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await a.SendChatAsync("안녕, FlatBuffers 룸", ct).ConfigureAwait(false);
        ChatBroadcast chat = await b.ReadChatBroadcastAsync(ct).ConfigureAwait(false);
        long chatSentBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        ok &= Check(chat.SenderName == "아리" && chat.Text == "안녕, FlatBuffers 룸",
            "B 가 A 의 채팅을 ChatBroadcast(발신자+본문)로 받는다");
        ok &= Check(chat.SentAtUnixMs >= chatSentAfter && chat.SentAtUnixMs <= chatSentBefore,
            "브로드캐스트 시각은 서버가 채운 Unix ms 다");

        // ── (5) 이동 실데이터 왕복 — float 3개가 검증을 거쳐 그대로 전달된다.
        await a.SendMoveAsync(12.5f, -3.25f, 270f, ct).ConfigureAwait(false);
        MoveBroadcast move = await b.ReadMoveBroadcastAsync(ct).ConfigureAwait(false);
        ok &= Check(move.PlayerId == playerA, "이동 브로드캐스트가 발신자 플레이어 번호를 싣는다");
        ok &= Check(move.X == 12.5f && move.Y == -3.25f && move.Heading == 270f,
            "좌표 실데이터(float)가 손실 없이 왕복한다");

        // ── (6) 재접속 + 세션 재개 — 회전 토큰 수신, 옛 토큰 무효화, 상태 복원까지.
        await a.DisposeAsync().ConfigureAwait(false);
        ok &= Check(
            await WaitForAsync(
                () => service.Directory.TryGet(new RoomId(100), out Room? room100) && room100!.MemberCount == 1,
                ct).ConfigureAwait(false),
            "A 의 접속 종료로 룸 100 에는 B 만 남는다 (자동 퇴장)");

        await using FlatGameClient a2 =
            await FlatGameClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false);
        (SessionResumeStatus resumeStatus, byte[] rotated) =
            await a2.ResumeAsync(sessionA, tokenA, ct).ConfigureAwait(false);

        ok &= Check(resumeStatus == SessionResumeStatus.Resumed, "재접속한 A 가 재개 토큰으로 세션을 재개한다 (40007→40008)");
        ok &= Check(!rotated.AsSpan().SequenceEqual(tokenA), "재개 응답의 토큰은 회전된 새 토큰이다");

        // 옛 토큰 재사용 시도 — 회전이 실제로 옛 토큰을 무효화했는지 와이어로 확인한다.
        await using (FlatGameClient replayer =
            await FlatGameClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false))
        {
            (SessionResumeStatus replayStatus, _) =
                await replayer.ResumeAsync(sessionA, tokenA, ct).ConfigureAwait(false);
            ok &= Check(replayStatus == SessionResumeStatus.Rejected, "회전 전의 옛 토큰은 거부된다 (1회용)");
        }

        // 재개된 커넥션은 재로그인 없이 곧바로 앱 메시지를 쓸 수 있어야 한다 —
        // SessionResumeStateBridge 가 세션 상태에서 신원을 복원하고 상태를 전이한 결과다.
        JoinRoomReply rejoin = await a2.JoinAsync(100, ct).ConfigureAwait(false);
        ok &= Check(rejoin.Result == JoinRoomResult.Joined && rejoin.MemberCount == 2,
            "재개된 A 가 재로그인 없이 룸에 다시 입장한다 (상태 복원)");

        // ── (7) 퇴장 — 명시적 Leave + 퇴장 통지 + 멱등성.
        LeaveRoomReply leaveA = await a2.LeaveAsync(notifyOthers: true, ct).ConfigureAwait(false);
        ok &= Check(leaveA.Result == LeaveRoomResult.Left, "A 가 룸을 나간다");

        ChatBroadcast notice = await b.ReadChatBroadcastAsync(ct).ConfigureAwait(false);
        ok &= Check(notice.SenderName == "아리" && notice.Text == "룸에서 나갔다",
            "B 가 퇴장 통지를 받는다 — 발신자 이름은 세션 상태에서 복원된 것이다");

        LeaveRoomReply leaveB = await b.LeaveAsync(notifyOthers: false, ct).ConfigureAwait(false);
        LeaveRoomReply leaveDup = await b.LeaveAsync(notifyOthers: false, ct).ConfigureAwait(false);
        ok &= Check(leaveB.Result == LeaveRoomResult.Left, "B 가 룸을 나간다");
        ok &= Check(leaveDup.Result == LeaveRoomResult.NotInRoom, "중복 퇴장은 정직하게 알린다");

        ok &= Check(!a2.HasPendingFrames && !b.HasPendingFrames, "예상 밖의 프레임은 아무에게도 오지 않았다");
        ok &= Check(service.BroadcastRejected == 0, "거부된 배달이 없다");

        Console.WriteLine();

        if (ok)
        {
            Console.WriteLine($"통과 — 배달 수락 {service.BroadcastAccepted}건, 거부 0건, 세션 {store.Count}개.");
            return 0;
        }

        Console.Error.WriteLine("실패 — 위 결과를 확인한다.");
        return 1;
    }

    private static bool Check(bool condition, string description)
    {
        Console.WriteLine($"  {(condition ? "성공" : "실패")}  {description}");
        return condition;
    }

    /// <summary>조건이 참이 되거나 타임아웃될 때까지 폴링한다. 비동기 통지(커넥션 종료)를 기다릴 때만 쓴다.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// 자체 검증용 클라이언트 — FlatBuffers 요청을 보내고, 수신 프레임을 유계 채널로 모아
    /// 순서대로 검사한다. 세션 핸드셰이크(40007~40009)는 프레임워크 동결 코덱으로 말한다.
    /// </summary>
    /// <remarks>
    /// <para><b>수명 규약.</b> 수신 페이로드는 핸들러 반환 후 무효라 <see cref="Capture"/> 가
    /// 복사한다(CHSM3003). 역직렬화는 채널에서 꺼낸 복사본에 대해 수행한다.</para>
    /// <para><b>스레드 규약.</b> 한 인스턴스는 한 시나리오 스레드에서만 쓴다.
    /// 수신은 클라이언트 읽기 루프가, 소비는 시나리오가 한다(SingleReader 채널).</para>
    /// </remarks>
    private sealed class FlatGameClient : IAsyncDisposable
    {
        // 요청 직렬화기·응답 역직렬화기. 상태가 없으므로 공유해도 안전하다.
        private static readonly FlatSharpMessageSerializer<LoginRequest> LoginRequestSerializer = new(LoginRequest.Serializer);
        private static readonly FlatSharpMessageSerializer<JoinRoomRequest> JoinRequestSerializer = new(JoinRoomRequest.Serializer);
        private static readonly FlatSharpMessageSerializer<ChatSend> ChatSendSerializer = new(ChatSend.Serializer);
        private static readonly FlatSharpMessageSerializer<MoveUpdate> MoveUpdateSerializer = new(MoveUpdate.Serializer);
        private static readonly FlatSharpMessageSerializer<LeaveRoomRequest> LeaveRequestSerializer = new(LeaveRoomRequest.Serializer);
        private static readonly FlatSharpMessageSerializer<LoginReply> LoginReplySerializer = new(LoginReply.Serializer);
        private static readonly FlatSharpMessageSerializer<JoinRoomReply> JoinReplySerializer = new(JoinRoomReply.Serializer);
        private static readonly FlatSharpMessageSerializer<ChatBroadcast> ChatBroadcastSerializer = new(ChatBroadcast.Serializer);
        private static readonly FlatSharpMessageSerializer<MoveBroadcast> MoveBroadcastSerializer = new(MoveBroadcast.Serializer);
        private static readonly FlatSharpMessageSerializer<LeaveRoomReply> LeaveReplySerializer = new(LeaveRoomReply.Serializer);

        private readonly ChServerMClient _client;
        private readonly ClientSession _session;
        private readonly Channel<(ushort MessageId, byte[] Payload)> _inbox;
        private readonly IFrameEncoder _encoder;
        private uint _sequence;
        private bool _disposed;

        private FlatGameClient(
            ChServerMClient client,
            ClientSession session,
            Channel<(ushort, byte[])> inbox,
            IFrameEncoder encoder)
        {
            _client = client;
            _session = session;
            _inbox = inbox;
            _encoder = encoder;
        }

        /// <summary>아직 읽지 않은 수신 프레임이 있는가 — 음성 검증과 격리 검증에 쓴다.</summary>
        public bool HasPendingFrames => _inbox.Reader.TryPeek(out _);

        public static async Task<FlatGameClient> ConnectAsync(
            EndPoint target,
            IFrameEncoder encoder,
            IFrameDecoder decoder,
            CancellationToken cancellationToken)
        {
            // 유계 채널(9.6). Wait 모드이므로 쓰기는 반드시 WriteAsync 로 한다.
            Channel<(ushort, byte[])> inbox =
                Channel.CreateBounded<(ushort, byte[])>(new BoundedChannelOptions(64)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            ChServerMClient client = new ClientBuilder()
                .UseTransport(new TcpClientTransport(CreateTcpOptions()))
                .UseFraming(decoder, encoder)
                .ConfigureDispatcher(dispatcher => dispatcher
                    .MapRaw(FlatGameRoomProtocol.Login, context => Capture(context, inbox.Writer))
                    .MapRaw(FlatGameRoomProtocol.JoinRoom, context => Capture(context, inbox.Writer))
                    .MapRaw(FlatGameRoomProtocol.ChatBroadcast, context => Capture(context, inbox.Writer))
                    .MapRaw(FlatGameRoomProtocol.MoveBroadcast, context => Capture(context, inbox.Writer))
                    .MapRaw(FlatGameRoomProtocol.LeaveRoom, context => Capture(context, inbox.Writer))
                    // 세션 핸드셰이크 통지도 일반 프레임이다 — 클라이언트가 명시적으로 받는다.
                    .MapRaw(FrameworkMessageIds.SessionEstablished, context => Capture(context, inbox.Writer))
                    .MapRaw(FrameworkMessageIds.SessionResumed, context => Capture(context, inbox.Writer)))
                .Build();

            ClientSession session = await client.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            return new FlatGameClient(client, session, inbox, encoder);
        }

        /// <summary>로그인하고 (플레이어 번호, MOTD, 세션 번호, 최초 재개 토큰)을 돌려준다.</summary>
        /// <remarks>서버는 수립 통지(40009)를 <c>LoginReply</c> 보다 먼저 보낸다 — 그 순서로 읽는다.</remarks>
        public async Task<(long PlayerId, string Motd, long SessionId, byte[] ResumeToken)> LoginAsync(
            string displayName, string clientToken, CancellationToken cancellationToken)
        {
            LoginRequest request = new() { DisplayName = displayName, ClientToken = clientToken };
            await SendAsync(FlatGameRoomProtocol.Login, Serialize(LoginRequestSerializer, request), cancellationToken)
                .ConfigureAwait(false);

            (ushort id, byte[] payload) = await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (id != FrameworkMessageIds.SessionEstablished.Value)
            {
                throw new InvalidOperationException($"세션 수립 통지(40009)를 기대했는데 메시지 {id} 가 왔다.");
            }

            byte[] resumeToken = new byte[SessionHandshakeCodec.TokenLength];
            if (!SessionHandshakeCodec.TryReadEstablished(payload, out long sessionId, resumeToken))
            {
                throw new InvalidOperationException("세션 수립 통지의 형식이 어긋났다.");
            }

            LoginReply reply = await ReadMessageAsync(
                FlatGameRoomProtocol.LoginId, LoginReplySerializer, cancellationToken).ConfigureAwait(false);

            return (reply.PlayerId, reply.Motd ?? string.Empty, sessionId, resumeToken);
        }

        /// <summary>재개 요청(40007)을 보내고 응답(40008)의 상태·회전 토큰을 돌려준다.</summary>
        public async Task<(SessionResumeStatus Status, byte[] RotatedToken)> ResumeAsync(
            long sessionId, byte[] resumeToken, CancellationToken cancellationToken)
        {
            byte[] request = new byte[SessionHandshakeCodec.ResumeRequestSize];
            SessionHandshakeCodec.WriteResumeRequest(request, sessionId, resumeToken);
            await SendAsync(FrameworkMessageIds.SessionResume, request, cancellationToken).ConfigureAwait(false);

            (ushort id, byte[] payload) = await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (id != FrameworkMessageIds.SessionResumed.Value)
            {
                throw new InvalidOperationException($"재개 응답(40008)을 기대했는데 메시지 {id} 가 왔다.");
            }

            byte[] rotated = new byte[SessionHandshakeCodec.TokenLength];
            if (!SessionHandshakeCodec.TryReadResumeResponse(payload, out SessionResumeStatus status, rotated))
            {
                throw new InvalidOperationException("재개 응답의 형식이 어긋났다.");
            }

            return (status, rotated);
        }

        /// <summary>응답을 기다리지 않고 입장 요청만 보낸다 — 음성 검증(거부 후 종료 관측)용.</summary>
        public Task SendJoinAsync(ulong roomId, CancellationToken cancellationToken) =>
            SendAsync(
                FlatGameRoomProtocol.JoinRoom,
                Serialize(JoinRequestSerializer, new JoinRoomRequest { RoomId = roomId }),
                cancellationToken);

        public async Task<JoinRoomReply> JoinAsync(ulong roomId, CancellationToken cancellationToken)
        {
            await SendJoinAsync(roomId, cancellationToken).ConfigureAwait(false);
            return await ReadMessageAsync(FlatGameRoomProtocol.JoinRoomId, JoinReplySerializer, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task SendChatAsync(string text, CancellationToken cancellationToken) =>
            SendAsync(
                FlatGameRoomProtocol.ChatSend,
                Serialize(ChatSendSerializer, new ChatSend { Text = text }),
                cancellationToken);

        public Task SendMoveAsync(float x, float y, float heading, CancellationToken cancellationToken) =>
            SendAsync(
                FlatGameRoomProtocol.MoveUpdate,
                Serialize(MoveUpdateSerializer, new MoveUpdate { X = x, Y = y, Heading = heading }),
                cancellationToken);

        public Task<ChatBroadcast> ReadChatBroadcastAsync(CancellationToken cancellationToken) =>
            ReadMessageAsync(FlatGameRoomProtocol.ChatBroadcastId, ChatBroadcastSerializer, cancellationToken);

        public Task<MoveBroadcast> ReadMoveBroadcastAsync(CancellationToken cancellationToken) =>
            ReadMessageAsync(FlatGameRoomProtocol.MoveBroadcastId, MoveBroadcastSerializer, cancellationToken);

        public async Task<LeaveRoomReply> LeaveAsync(bool notifyOthers, CancellationToken cancellationToken)
        {
            await SendAsync(
                FlatGameRoomProtocol.LeaveRoom,
                Serialize(LeaveRequestSerializer, new LeaveRoomRequest { NotifyOthers = notifyOthers }),
                cancellationToken).ConfigureAwait(false);
            return await ReadMessageAsync(FlatGameRoomProtocol.LeaveRoomId, LeaveReplySerializer, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>서버가 먼저 커넥션을 닫을 때까지 기다린다 — 음성 검증(거부 = 종료)용.</summary>
        /// <returns>종료가 관측되면 <see langword="true"/>, 타임아웃이면 <see langword="false"/>.</returns>
        public async Task<bool> WaitClosedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (System.IO.IOException)
            {
                // 서버가 먼저 끊은 커넥션은 전송에 따라 I/O 예외로 끝난다 — 종료의 형태다.
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await _session.Connection.DisposeAsync().ConfigureAwait(false);

            try
            {
                // 읽기 루프의 실패를 관측한다. 방치하면 조용한 실패가 된다.
                await _session.Completion.ConfigureAwait(false);
            }
            catch (System.IO.IOException)
            {
                // 서버가 먼저 끊은 커넥션(음성 검증 경로)은 I/O 예외로 끝날 수 있다.
            }
            catch (System.Net.Sockets.SocketException)
            {
                // 위와 같다 — 종료의 형태이지 실패가 아니다.
            }

            await _client.DisposeAsync().ConfigureAwait(false);
        }

        private static byte[] Serialize<TMessage>(
            FlatSharpMessageSerializer<TMessage> serializer, TMessage message)
            where TMessage : class
        {
            ArrayBufferWriter<byte> buffer = new(256);
            serializer.Serialize(buffer, in message);
            return buffer.WrittenSpan.ToArray();
        }

        private async Task SendAsync(MessageId messageId, byte[] payload, CancellationToken cancellationToken)
        {
            await FrameWriter.WriteFrameAsync(
                _session.Connection.Output, _encoder, messageId, payload,
                FrameFlags.None, _sequence++, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>다음 수신 프레임이 기대한 메시지이길 요구하고 FlatBuffers 로 역직렬화한다.</summary>
        private async Task<TMessage> ReadMessageAsync<TMessage>(
            ushort expectedId,
            FlatSharpMessageSerializer<TMessage> serializer,
            CancellationToken cancellationToken)
            where TMessage : class
        {
            (ushort id, byte[] payload) = await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (id != expectedId)
            {
                throw new InvalidOperationException($"메시지 {expectedId} 를 기대했는데 {id} 가 왔다.");
            }

            if (!serializer.TryDeserialize(new ReadOnlySequence<byte>(payload), out TMessage message))
            {
                throw new InvalidOperationException($"메시지 {id} 페이로드를 역직렬화할 수 없다.");
            }

            return message;
        }

        /// <summary>도착한 프레임을 수신함으로 넘긴다. 페이로드는 핸들러 반환 후 무효라 복사가 필수다.</summary>
        private static async ValueTask<DispatchStatus> Capture(
            MessageContext context,
            ChannelWriter<(ushort MessageId, byte[] Payload)> writer)
        {
            // WriteAsync 여야 한다 — 유계 Wait 채널에 TryWrite 를 쓰면 포화 시 조용히
            // 버려진다(CLAUDE.md 9.6 의 레거시 결함 조합).
            (ushort, byte[]) frame = (context.Envelope.MessageId.Value, context.Payload.ToArray());
            await writer.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
            return DispatchStatus.Handled;
        }
    }
}
