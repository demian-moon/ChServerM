using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.RealTime.Rooms;
using ChServerM.Transport.Tcp;

namespace ChServerM.Samples.GameRoom;

/// <summary>
/// 게임 룸 예제 — 룸/브로드캐스트 축(Phase 18)을 핸들러에 조립하는 방법을 보인다.
/// </summary>
/// <remarks>
/// <para>조합: TCP + 고정 헤더 프레이밍 + 파티션 실행 모델 + <c>ChServerM.RealTime.Rooms</c>.</para>
/// <para>이 프로그램이 실증하는 것.</para>
/// <list type="number">
///   <item><description><b>1회 인코딩 브로드캐스트</b> — 채팅 하나가 룸 멤버 수만큼
///     직렬화되지 않는다(ADR-0064). 조립 방법은 <see cref="RoomChatService"/> 참조.</description></item>
///   <item><description><b>룸 격리</b> — 다른 룸에는 한 바이트도 새지 않는다.</description></item>
///   <item><description><b>세 갈래 퇴장 경로</b> — 명시 Leave · 커넥션 종료 · 배달 실패가
///     한 지점으로 모여 유령 멤버를 남기지 않는다.</description></item>
/// </list>
/// <para>
/// 인자 없이 실행하면 자체 검증(클라이언트 3개 시나리오)을 돌고 종료한다(CI 용).
/// <c>--serve [포트]</c> 로 실행하면 TCP 채팅 서버로 계속 떠 있는다.
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

    /// <summary>룸 채팅 서버를 조립한다. 자체 검증과 <c>--serve</c> 가 같은 조립을 쓴다.</summary>
    private static ChServerMServer BuildServer(
        IPEndPoint listen,
        FixedHeaderFrameEncoder encoder,
        FixedHeaderFrameDecoder decoder,
        PartitionedExecutionModel executionModel,
        RoomChatService rooms)
    {
        return new ServerBuilder()
            .UseTransport(new TcpServerTransport(listen, CreateTcpOptions()))
            .UseFraming(decoder, encoder)
            // 룸 축은 파티션 실행 모델을 전제한다 — 커넥션 파티션의 배타 슬롯이
            // 브로드캐스트 쓰기의 소유권 근거다(ADR-0064).
            .UseExecutionModel(executionModel)
            .ConfigureDispatcher(dispatcher => dispatcher
                .MapRaw(GameRoomProtocol.Join, rooms.HandleJoinAsync)
                .MapRaw(GameRoomProtocol.Chat, rooms.HandleChatAsync)
                .MapRaw(GameRoomProtocol.Leave, rooms.HandleLeaveAsync))
            .Build();
    }

    /// <summary>TCP 룸 채팅 서버로 계속 떠 있는다.</summary>
    private static async Task ServeAsync(int port)
    {
        FramingOptions framing = CreateFramingOptions();
        FixedHeaderFrameEncoder encoder = new(framing);
        FixedHeaderFrameDecoder decoder = new(framing);

        await using PartitionedExecutionModel executionModel = new();
        RoomChatService rooms = new(encoder, executionModel);

        await using ChServerMServer server = BuildServer(
            new IPEndPoint(IPAddress.Any, port), encoder, decoder, executionModel, rooms);

        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"룸 채팅 서버 시작: {server.LocalEndPoint}");
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
            $"룸 {rooms.Directory.RoomCount}개, 배달 수락 {rooms.BroadcastAccepted}건 / 거부 {rooms.BroadcastRejected}건");
    }

    /// <summary>클라이언트 3개(A·B 는 룸 100, C 는 룸 200)로 룸 시나리오를 검증한다.</summary>
    /// <returns>모두 통과하면 0.</returns>
    private static async Task<int> SelfTestAsync()
    {
        Console.WriteLine("ChServerM 게임 룸 샘플 — 1회 인코딩 브로드캐스트와 룸 격리.");
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

        RoomChatService rooms = new(encoder, executionModel);

        // 포트 0 → OS 가 배정한다. 하드코딩하면 병렬 실행에서 충돌한다.
        await using ChServerMServer server = BuildServer(
            new IPEndPoint(IPAddress.Loopback, 0), encoder, decoder, executionModel, rooms);
        await server.StartAsync().ConfigureAwait(false);

        EndPoint target = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        await using ChatClient a = await ChatClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false);
        await using ChatClient b = await ChatClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false);
        await using ChatClient c = await ChatClient.ConnectAsync(target, encoder, decoder, ct).ConfigureAwait(false);

        bool ok = true;

        // 입장 — A·B 는 같은 룸, C 는 다른 룸.
        ok &= Check(await a.JoinAsync(100, ct).ConfigureAwait(false) == JoinResult.Joined, "A 가 룸 100 에 입장한다");
        ok &= Check(await b.JoinAsync(100, ct).ConfigureAwait(false) == JoinResult.Joined, "B 가 룸 100 에 입장한다");
        ok &= Check(await c.JoinAsync(200, ct).ConfigureAwait(false) == JoinResult.Joined, "C 가 룸 200 에 입장한다");
        ok &= Check(await a.JoinAsync(100, ct).ConfigureAwait(false) == JoinResult.AlreadyInRoom, "중복 입장은 거부된다");

        // 브로드캐스트 — 발신자는 제외되고, 같은 룸의 나머지가 받는다.
        await a.SendChatAsync("안녕, 룸 100", ct).ConfigureAwait(false);
        ok &= Check(await b.ReadChatAsync(ct).ConfigureAwait(false) == "안녕, 룸 100", "B 가 A 의 채팅을 받는다");

        await b.SendChatAsync("반가워", ct).ConfigureAwait(false);
        ok &= Check(await a.ReadChatAsync(ct).ConfigureAwait(false) == "반가워", "A 가 B 의 채팅을 받는다");

        // 명시적 퇴장(퇴장 경로 1) — 멱등성까지 확인한다.
        ok &= Check(await b.LeaveAsync(ct).ConfigureAwait(false) == LeaveResult.Left, "B 가 룸을 나간다");
        ok &= Check(await b.LeaveAsync(ct).ConfigureAwait(false) == LeaveResult.NotInRoom, "중복 퇴장은 정직하게 알린다");

        // B 의 퇴장 응답을 받았으므로 서버 상태는 확정됐다 — 인프로세스라 직접 단언한다.
        ok &= Check(
            rooms.Directory.TryGet(new RoomId(100), out Room? room100) && room100!.MemberCount == 1,
            "룸 100 에는 A 만 남는다");
        ok &= Check(rooms.Directory.RoomCount == 2, "룸은 2개다");

        // A 혼자 남은 룸에 채팅 — 발신자는 제외되므로 받을 사람이 없다.
        await a.SendChatAsync("아무도 없나요", ct).ConfigureAwait(false);

        // 동기화: 같은 커넥션 = 같은 파티션 = 순차이므로, A 의 다음 응답이 오면
        // 위 채팅의 브로드캐스트 처리도 이미 끝나 있다.
        ok &= Check(await a.JoinAsync(100, ct).ConfigureAwait(false) == JoinResult.AlreadyInRoom, "(브로드캐스트 완료 동기화)");

        ok &= Check(!b.HasPendingFrames && !c.HasPendingFrames, "룸 밖(B)과 다른 룸(C)에는 아무것도 오지 않았다");

        // 커넥션 종료 = 자동 퇴장(퇴장 경로 2). 종료 통지는 비동기이므로 관측될 때까지 짧게 기다린다.
        await a.DisposeAsync().ConfigureAwait(false);
        ok &= Check(
            await WaitForAsync(() => room100!.MemberCount == 0, ct).ConfigureAwait(false),
            "A 의 접속 종료로 룸 100 이 빈다");

        ok &= Check(rooms.BroadcastRejected == 0, "거부된 배달이 없다");

        Console.WriteLine();

        if (ok)
        {
            Console.WriteLine($"통과 — 배달 수락 {rooms.BroadcastAccepted}건, 거부 0건.");
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

    /// <summary>자체 검증용 채팅 클라이언트 — 수신 프레임을 유계 채널로 모아 순서대로 검사한다.</summary>
    private sealed class ChatClient : IAsyncDisposable
    {
        private readonly ChServerMClient _client;
        private readonly ClientSession _session;
        private readonly Channel<(ushort MessageId, byte[] Payload)> _inbox;
        private readonly IFrameEncoder _encoder;
        private uint _sequence;
        private bool _disposed;

        private ChatClient(
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

        /// <summary>아직 읽지 않은 수신 프레임이 있는가 — 룸 격리 검증에 쓴다.</summary>
        public bool HasPendingFrames => _inbox.Reader.TryPeek(out _);

        public static async Task<ChatClient> ConnectAsync(
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
                    .MapRaw(GameRoomProtocol.Join, context => Capture(context, inbox.Writer))
                    .MapRaw(GameRoomProtocol.Chat, context => Capture(context, inbox.Writer))
                    .MapRaw(GameRoomProtocol.Leave, context => Capture(context, inbox.Writer)))
                .Build();

            ClientSession session = await client.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            return new ChatClient(client, session, inbox, encoder);
        }

        public async Task<byte> JoinAsync(ulong roomId, CancellationToken cancellationToken)
        {
            byte[] payload = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, roomId);

            await SendAsync(GameRoomProtocol.Join, payload, cancellationToken).ConfigureAwait(false);
            return await ReadStatusAsync(GameRoomProtocol.Join, cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte> LeaveAsync(CancellationToken cancellationToken)
        {
            await SendAsync(GameRoomProtocol.Leave, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            return await ReadStatusAsync(GameRoomProtocol.Leave, cancellationToken).ConfigureAwait(false);
        }

        public Task SendChatAsync(string text, CancellationToken cancellationToken) =>
            SendAsync(GameRoomProtocol.Chat, Encoding.UTF8.GetBytes(text), cancellationToken);

        /// <summary>다음 수신 프레임이 채팅이길 기대하고 텍스트를 돌려준다.</summary>
        public async Task<string> ReadChatAsync(CancellationToken cancellationToken)
        {
            (ushort messageId, byte[] payload) = await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (messageId != GameRoomProtocol.Chat.Value)
            {
                throw new InvalidOperationException($"채팅을 기대했는데 메시지 {messageId} 가 왔다.");
            }

            return Encoding.UTF8.GetString(payload);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await _session.Connection.DisposeAsync().ConfigureAwait(false);

            // 읽기 루프의 실패를 관측한다. 방치하면 조용한 실패가 된다.
            await _session.Completion.ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        private async Task SendAsync(
            ChServerM.Identity.MessageId messageId, byte[] payload, CancellationToken cancellationToken)
        {
            await FrameWriter.WriteFrameAsync(
                _session.Connection.Output, _encoder, messageId, payload,
                FrameFlags.None, _sequence++, cancellationToken).ConfigureAwait(false);
        }

        private async Task<byte> ReadStatusAsync(
            ChServerM.Identity.MessageId expected, CancellationToken cancellationToken)
        {
            (ushort messageId, byte[] payload) = await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (messageId != expected.Value || payload.Length != 1)
            {
                throw new InvalidOperationException(
                    $"메시지 {expected.Value} 의 1바이트 상태 응답을 기대했는데, 메시지 {messageId} ({payload.Length}바이트)가 왔다.");
            }

            return payload[0];
        }

        /// <summary>도착한 프레임을 수신함으로 넘긴다. 페이로드는 핸들러 반환 후 무효라 복사가 필수다.</summary>
        private static async ValueTask<DispatchStatus> Capture(
            MessageContext context,
            ChannelWriter<(ushort MessageId, byte[] Payload)> writer)
        {
            (ushort, byte[]) frame = (context.Envelope.MessageId.Value, context.Payload.ToArray());
            await writer.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
            return DispatchStatus.Handled;
        }
    }
}
