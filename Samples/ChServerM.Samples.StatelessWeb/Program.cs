using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Dispatch.Generated;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Serialization.Protobuf;
using ChServerM.Transport.Http;

namespace ChServerM.Samples.StatelessWeb;

/// <summary>
/// <c>stateless-web</c> 참조 프로필(ADR-0004)을 조립하는 예제 —
/// HTTP/2 전송 + 고정 헤더 프레이밍 + 병렬(스레드풀) 실행 + Protobuf 직렬화.
/// </summary>
/// <remarks>
/// <para>이 프로그램이 실증하는 것은 두 가지다.</para>
/// <list type="number">
///   <item><description>
///     <b>축 교체.</b> EchoServer(TCP + MemoryPack + 파티션 실행)와 핸들러 작성 방법이
///     완전히 같다 — <c>[MessageHandler]</c> 선언과 <c>MapGeneratedHandlers</c> 한 줄.
///     바뀐 것은 조립 지점의 전송·직렬화기·실행 모델뿐이다.
///   </description></item>
///   <item><description>
///     <b>병렬 실행의 계약.</b> <c>UseExecutionModel</c> 을 부르지 않으면 핸들러가
///     스레드풀에서 병렬로 돈다. 순서 보장이 없으므로 응답은 시퀀스 번호로 짝짓는다 —
///     자체 검증이 그 짝짓기를 실제로 수행한다.
///   </description></item>
/// </list>
/// <para>
/// 인자 없이 실행하면 자체 검증을 돌고 종료한다(CI 용). <c>--serve [포트]</c> 로
/// 실행하면 HTTP 서버로 계속 떠 있는다.
/// </para>
/// </remarks>
internal static class Program
{
    private const int SelfTestRequests = 100;

    /// <summary>이 샘플이 허용하는 최대 페이로드.</summary>
    /// <remarks>
    /// 기본값(1MB)을 쓰지 않고 명시한다 — 최대 프레임이 전송의 버퍼 한계
    /// (HTTP/2 스트림 수신 윈도, 기본 1MB)를 넘으면 <c>Build()</c> 가 조립을 거부한다(ADR-0007).
    /// </remarks>
    private const int MaxPayload = 64 * 1024;

    private static FramingOptions CreateFramingOptions() => new() { MaxPayloadLength = MaxPayload };

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

    /// <summary>무상태 프로필 서버를 조립한다. 자체 검증과 <c>--serve</c> 가 같은 조립을 쓴다.</summary>
    private static ChServerMServer BuildServer(IPEndPoint listen, FixedHeaderFrameEncoder encoder)
    {
        FramingOptions framing = CreateFramingOptions();

        // Protobuf 제공자에는 "요청" 메시지 타입만 등록하면 된다 — 제공자는 수신(역직렬화)
        // 경로에 쓰이고, 응답 직렬화는 핸들러가 직접 한다.
        ProtobufMessageSerializerProvider serializers = new ProtobufMessageSerializerProvider()
            .Register<SumRequest>();

        // ⚠ UseExecutionModel 이 없다 — 이것이 무상태 프로필의 핵심 선택이다(ADR-0004).
        // 커넥션 처리가 스레드풀에서 병렬로 돌고, 순서 보장은 없다.
        return new ServerBuilder()
            .UseTransport(new HttpServerTransport(listen, new HttpTransportOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .ConfigureDispatcher(dispatcher => dispatcher
                // [MessageHandler] 선언에서 생성된 등록 — EchoServer 와 같은 경로에
                // 직렬화 제공자만 Protobuf 로 갈아 끼웠다(ADR-0014).
                .MapGeneratedHandlers(serializers, handler1: new SumHandler(encoder)))
            .Build();
    }

    /// <summary>HTTP 서버로 계속 떠 있는다.</summary>
    private static async Task ServeAsync(int port)
    {
        FixedHeaderFrameEncoder encoder = new(CreateFramingOptions());

        await using ChServerMServer server = BuildServer(new IPEndPoint(IPAddress.Any, port), encoder);
        await server.StartAsync().ConfigureAwait(false);

        Console.WriteLine($"무상태 합계 서버 시작(HTTP/2): {server.LocalEndPoint}");
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
    }

    /// <summary>요청 100개를 보내고 응답을 시퀀스 번호로 짝지어 검증한다.</summary>
    /// <returns>모두 통과하면 0.</returns>
    private static async Task<int> SelfTestAsync()
    {
        Console.WriteLine("ChServerM 무상태 웹 샘플 — HTTP/2 + Protobuf + 병렬 실행.");
        Console.WriteLine();

        // 테스트 전체가 이 시간 안에 끝나야 한다. 어긋난 조립은 걸려서 멈추는 것이 아니라
        // 실패로 드러나야 한다.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        FramingOptions framing = CreateFramingOptions();
        FixedHeaderFrameEncoder encoder = new(framing);
        FixedHeaderFrameDecoder decoder = new(framing);

        // 포트 0 → OS 가 배정한다. 하드코딩하면 병렬 실행에서 충돌한다.
        await using ChServerMServer server = BuildServer(new IPEndPoint(IPAddress.Loopback, 0), encoder);
        await server.StartAsync().ConfigureAwait(false);

        EndPoint target = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        // 유계 채널(9.6). 응답 수가 요청 수(100)로 유계이지만, 교본 코드가 무제한 큐를
        // 시연하면 안 된다. Wait 모드이므로 쓰기는 반드시 WriteAsync 로 한다.
        Channel<(uint Sequence, byte[] Payload)> responses =
            Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(SelfTestRequests)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new HttpClientTransport(new HttpTransportOptions()))
            .UseFraming(decoder, encoder)
            .ConfigureDispatcher(dispatcher => dispatcher
                .MapRaw(WebProtocol.Sum, context => Capture(context, responses.Writer)))
            .Build();

        ClientSession session = await client.ConnectAsync(target, timeout.Token).ConfigureAwait(false);

        bool ok;

        try
        {
            ok = await RunSumRoundTripsAsync(session.Connection, encoder, responses.Reader, timeout.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            await session.Connection.DisposeAsync().ConfigureAwait(false);

            try
            {
                // 읽기 루프의 실패를 관측한다. 방치하면 조용한 실패가 된다.
                await session.Completion.ConfigureAwait(false);
            }
            catch (System.IO.IOException)
            {
                // HTTP/2 는 우리가 먼저 커넥션을 닫으면 진행 중 스트림을 abort 로 접는다.
                // 방금 위에서 Dispose 했으므로 이것은 종료의 형태이지 실패가 아니다.
                // (TCP 는 같은 순서에서 정상 EOF 로 끝난다 — 전송별 종료 형태의 차이다.)
            }
        }

        Console.WriteLine();

        if (ok)
        {
            Console.WriteLine("통과 — EchoServer 와 같은 핸들러 작성 방식이 HTTP/2 + Protobuf 조립에서 동작한다.");
            return 0;
        }

        Console.Error.WriteLine("실패 — 위 결과를 확인한다.");
        return 1;
    }

    /// <summary>도착한 프레임을 응답 채널로 넘긴다.</summary>
    /// <remarks>
    /// 페이로드를 복사한다. <see cref="MessageContext.Payload"/> 는 핸들러가 반환하면
    /// 무효가 되므로, <c>await</c> 너머로 들고 가려면 복사가 <b>반드시</b> 필요하다.
    /// </remarks>
    private static async ValueTask<DispatchStatus> Capture(
        MessageContext context,
        ChannelWriter<(uint Sequence, byte[] Payload)> writer)
    {
        (uint, byte[]) response = (context.Envelope.Sequence, context.Payload.ToArray());
        await writer.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);
        return DispatchStatus.Handled;
    }

    /// <summary>요청을 전부 보낸 뒤, 순서와 무관하게 도착하는 응답을 시퀀스로 짝지어 검증한다.</summary>
    /// <remarks>
    /// 보내기는 순차다 — <see cref="System.IO.Pipelines.PipeWriter"/> 는 단일 쓰기자 계약이라
    /// 한 커넥션에 병렬로 쓰면 안 된다. 병렬성은 서버 쪽(스레드풀 핸들러)에 있고,
    /// 그래서 응답 순서가 뒤섞일 수 있다 — 그것이 이 프로필의 계약이고 여기서 관측된다.
    /// </remarks>
    private static async Task<bool> RunSumRoundTripsAsync(
        ChServerM.Connections.IConnection connection,
        IFrameEncoder encoder,
        ChannelReader<(uint Sequence, byte[] Payload)> responses,
        CancellationToken cancellationToken)
    {
        ProtobufMessageSerializer<SumRequest> requestSerializer = new();
        ProtobufMessageSerializer<SumReply> replySerializer = new();

        // 요청 i 는 { i, i+1, i+2 } 의 합을 묻는다. 기대값은 3i + 3.
        long[] expectedSums = new long[SelfTestRequests];
        ArrayBufferWriter<byte> requestBuffer = new(64);

        for (int i = 0; i < SelfTestRequests; i++)
        {
            SumRequest request = new() { Values = { i, i + 1, i + 2 } };
            expectedSums[i] = 3L * i + 3;

            requestBuffer.Clear();
            requestSerializer.Serialize(requestBuffer, in request);

            await FrameWriter.WriteFrameAsync(
                connection.Output, encoder, WebProtocol.Sum, requestBuffer.WrittenSpan,
                FrameFlags.None, (uint)i, cancellationToken).ConfigureAwait(false);
        }

        int received = 0;
        int outOfOrder = 0;
        uint lastSequence = 0;

        while (received < SelfTestRequests)
        {
            (uint sequence, byte[] payload) = await responses.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (!replySerializer.TryDeserialize(new ReadOnlySequence<byte>(payload), out SumReply reply))
            {
                Console.Error.WriteLine($"  응답 역직렬화 실패 (시퀀스 {sequence})");
                return false;
            }

            if (sequence >= SelfTestRequests || reply.Sum != expectedSums[sequence] || reply.TermCount != 3)
            {
                string expected = sequence < SelfTestRequests ? expectedSums[sequence].ToString() : "?";
                Console.Error.WriteLine($"  합계 불일치 (시퀀스 {sequence}): 기대 {expected}, 실제 {reply.Sum}");
                return false;
            }

            // 순서 역전은 실패가 아니라 이 프로필의 정상 동작이다. 관측만 한다.
            if (received > 0 && sequence < lastSequence)
            {
                outOfOrder++;
            }

            lastSequence = sequence;
            received++;
        }

        Console.WriteLine(
            $"  HTTP/2   요청 {SelfTestRequests}건 왕복 — 성공, 순서 역전 {outOfOrder}건 (병렬 실행이므로 0이 아닐 수 있다)");
        return true;
    }
}
