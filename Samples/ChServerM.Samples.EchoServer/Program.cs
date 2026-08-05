using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Dispatch.Generated;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Serialization.MemoryPack;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;

namespace ChServerM.Samples.EchoServer;

/// <summary>
/// 축을 조립해 에코 서버를 세우는 최소 예제.
/// </summary>
/// <remarks>
/// <para>이 프로그램은 두 가지를 한다.</para>
/// <list type="number">
///   <item><description>
///     <b>같은 핸들러를 두 전송(TCP / 인메모리)에 꽂아 돌린다.</b> 조립 가능성의
///     합격 기준을 코드로 보인다(ADR-0004)
///   </description></item>
///   <item><description>
///     <b>Native AOT 로 링크되는 실행 가능 대상을 제공한다.</b> 라이브러리의
///     <c>IsAotCompatible</c> 은 분석기만 켤 뿐, 실제 링크는 publish 해봐야 안다
///   </description></item>
/// </list>
/// <para>
/// 인자 없이 실행하면 자체 검증을 돌고 종료한다(CI 용). <c>--serve [포트]</c> 로
/// 실행하면 TCP 서버로 계속 떠 있는다.
/// </para>
/// </remarks>
internal static class Program
{
    private const int SelfTestFrames = 1000;

    /// <summary>이 샘플이 허용하는 최대 페이로드.</summary>
    private const int MaxPayload = 64 * 1024;

    /// <summary>헤더까지 포함한 최대 프레임 크기.</summary>
    private const int MaxFrame = MaxPayload + FrameHeader.Size;

    /// <summary>
    /// 전송의 쓰기 일시정지 임계값.
    /// </summary>
    /// <remarks>
    /// <b>최대 프레임보다 커야 한다.</b> 작으면 그 크기의 프레임에서 커넥션이 조용히
    /// 교착한다 — 프레임 디코더는 완전한 프레임이 오기 전에 아무것도 소비할 수 없고,
    /// 버퍼가 차면 쓰기가 멈추기 때문이다. 어긋난 조합은 <c>Build()</c> 가 예외로 막는다.
    /// </remarks>
    private const long PauseThreshold = 2L * MaxFrame;

    /// <summary>전송의 쓰기 재개 임계값.</summary>
    private const long ResumeThreshold = MaxFrame;

    private static FramingOptions CreateFramingOptions() => new() { MaxPayloadLength = MaxPayload };

    private static TcpTransportOptions CreateTcpOptions() => new()
    {
        PauseWriterThreshold = PauseThreshold,
        ResumeWriterThreshold = ResumeThreshold,
    };

    private static InMemoryTransportOptions CreateInMemoryOptions() => new()
    {
        PauseWriterThreshold = PauseThreshold,
        ResumeWriterThreshold = ResumeThreshold,
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

    /// <summary>콘솔 출력을 UTF-8 로 맞춘다.</summary>
    /// <remarks>
    /// <para>
    /// <b>Native AOT 로 게시하면 필요하다.</b> JIT 실행에서는 호스트가 콘솔 인코딩을
    /// 정리해 주지만, AOT 바이너리는 Windows 의 기본 ANSI 코드 페이지(예: CP949)를 쓴다.
    /// 그러면 UTF-8 로 저장된 한글 문자열이 깨져 나온다.
    /// </para>
    /// <para>
    /// 출력이 리다이렉트됐거나 콘솔이 없는 환경에서는 던질 수 있다. 샘플 출력이
    /// 예쁘게 나오지 않는 것 때문에 프로그램이 죽을 이유는 없으므로 삼킨다.
    /// </para>
    /// </remarks>
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

    /// <summary>TCP 서버로 계속 떠 있는다.</summary>
    private static async Task ServeAsync(int port)
    {
        FramingOptions framing = CreateFramingOptions();
        FixedHeaderFrameEncoder encoder = new(framing);
        EchoHandler echo = new(encoder);

        // 이 조합이 realtime-stateful 참조 프로필이다(ADR-0004).
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Any, port), CreateTcpOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .UseExecutionModel(new PartitionedExecutionModel())
            .ConfigureDispatcher(dispatcher => dispatcher
                .MapRaw(EchoProtocol.Echo, echo.HandleEchoAsync)
                .MapRaw(EchoProtocol.Stats, echo.HandleStatsAsync)
                // [MessageHandler] 선언에서 생성된 등록 — 손으로 쓴 Map 이 아니다(ADR-0014).
                .MapGeneratedHandlers(
                    MemoryPackMessageSerializerProvider.Instance,
                    handler3: new GreetHandler(encoder)))
            .Build();

        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"에코 서버 시작: {server.LocalEndPoint}");
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

        Console.WriteLine("종료 중 — 신규 수용 중단 후 드레인한다.");
        await server.UnbindAsync().ConfigureAwait(false);

        using CancellationTokenSource drain = new(TimeSpan.FromSeconds(10));
        await server.StopAsync(drain.Token).ConfigureAwait(false);

        Console.WriteLine($"처리한 프레임: {echo.FramesHandled}");
    }

    /// <summary>같은 핸들러를 두 전송에서 돌려보고 결과를 검증한다.</summary>
    /// <returns>모두 통과하면 0.</returns>
    private static async Task<int> SelfTestAsync()
    {
        Console.WriteLine("ChServerM 에코 샘플 — 같은 핸들러를 두 전송에서 돌린다.");
        Console.WriteLine();

        bool tcpOk = await RunProfileAsync("TCP", CreateTcpTransports).ConfigureAwait(false);
        bool inMemoryOk = await RunProfileAsync("InMemory", CreateInMemoryTransports).ConfigureAwait(false);

        Console.WriteLine();

        if (tcpOk && inMemoryOk)
        {
            Console.WriteLine("통과 — 동일한 핸들러 코드가 두 전송에서 모두 동작한다.");
            return 0;
        }

        Console.Error.WriteLine("실패 — 위 결과를 확인한다.");
        return 1;
    }

    private static (IServerTransport Server, IClientTransport Client, EndPoint EndPoint) CreateTcpTransports()
    {
        // 포트 0 → OS 가 배정한다. 하드코딩하면 병렬 실행에서 충돌한다.
        TcpTransportOptions options = CreateTcpOptions();
        TcpServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        return (server, new TcpClientTransport(options), new IPEndPoint(IPAddress.Loopback, 0));
    }

    private static (IServerTransport Server, IClientTransport Client, EndPoint EndPoint) CreateInMemoryTransports()
    {
        InMemoryTransportOptions options = CreateInMemoryOptions();
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new("echo-sample");
        return (
            new InMemoryServerTransport(hub, endPoint, options),
            new InMemoryClientTransport(hub, null, options),
            endPoint);
    }

    /// <summary>전송 하나를 세우고 에코를 왕복시킨다.</summary>
    private static async Task<bool> RunProfileAsync(
        string name,
        Func<(IServerTransport Server, IClientTransport Client, EndPoint EndPoint)> createTransports)
    {
        FramingOptions framing = CreateFramingOptions();
        FixedHeaderFrameEncoder encoder = new(framing);
        FixedHeaderFrameDecoder decoder = new(framing);

        // 핸들러는 전송을 알지 못한다. 두 프로필이 같은 타입을 쓴다.
        EchoHandler echo = new(encoder);

        (IServerTransport transport, IClientTransport clientTransport, EndPoint endPoint) = createTransports();

        await using PartitionedExecutionModel executionModel = new(new PartitionedExecutionOptions
        {
            PartitionCount = Math.Min(4, Environment.ProcessorCount),
        });

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(decoder, encoder)
            .UseExecutionModel(executionModel)
            .ConfigureDispatcher(dispatcher => dispatcher
                .MapRaw(EchoProtocol.Echo, echo.HandleEchoAsync)
                .MapRaw(EchoProtocol.Stats, echo.HandleStatsAsync)
                // [MessageHandler] 선언에서 생성된 등록 — 이 경로가 AOT publish 를
                // 통과하는 것이 Phase 7 게이트의 절반이다(ADR-0014).
                .MapGeneratedHandlers(
                    MemoryPackMessageSerializerProvider.Instance,
                    handler3: new GreetHandler(encoder)))
            .Build();

        await server.StartAsync().ConfigureAwait(false);

        // TCP 는 포트 0 으로 바인드했으므로 실제 주소를 다시 읽는다.
        EndPoint target = server.LocalEndPoint ?? endPoint;

        // 클라이언트도 서버와 같은 디스패치 파이프라인을 쓴다. 응답은 여기로 들어온다.
        //
        // 커넥션의 Input 을 직접 읽지 않는 것이 중요하다 — IConnection 계약상
        // Input 은 읽기 루프 하나가 소유한다. 두 곳에서 읽으면 PipeReader 가
        // "Concurrent reads are not supported" 로 던진다.
        // 유계 채널(9.6). 교본 코드가 무제한 큐를 시연하면 안 된다 — 소비자(아래 왕복
        // 루프)가 멈추면 무제한 채널은 메모리로 갚는다. Wait 모드이므로 쓰기는 반드시
        // WriteAsync 로 한다(TryWrite 는 Wait 설정을 무시하고 조용히 버린다 — 레거시 결함).
        Channel<(uint Sequence, byte[] Payload)> responses =
            Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(clientTransport)
            .UseFraming(decoder, encoder)
            .ConfigureDispatcher(dispatcher => dispatcher
                .MapRaw(EchoProtocol.Echo, context => Capture(context, responses.Writer))
                .MapRaw(EchoProtocol.Stats, context => Capture(context, responses.Writer))
                .MapRaw(EchoProtocol.Greet, context => Capture(context, responses.Writer)))
            .Build();

        ClientSession session = await client.ConnectAsync(target).ConfigureAwait(false);

        long serverHandled;
        bool ok;

        try
        {
            ok = await RunEchoRoundTripsAsync(session.Connection, encoder, responses.Reader).ConfigureAwait(false);
            ok &= await RunGeneratedGreetAsync(session.Connection, encoder, responses.Reader).ConfigureAwait(false);
            serverHandled = await ReadStatsAsync(session.Connection, encoder, responses.Reader).ConfigureAwait(false);
        }
        finally
        {
            await session.Connection.DisposeAsync().ConfigureAwait(false);

            // 읽기 루프의 실패를 관측한다. 방치하면 조용한 실패가 된다.
            await session.Completion.ConfigureAwait(false);
        }

        Console.WriteLine(
            $"  {name,-9} 프레임 {SelfTestFrames}회 왕복 — {(ok ? "성공" : "실패")}, 서버 처리 {serverHandled}건");

        return ok && serverHandled >= SelfTestFrames;
    }

    /// <summary>도착한 프레임을 응답 채널로 넘긴다.</summary>
    /// <remarks>
    /// 페이로드를 복사한다. <see cref="MessageContext.Payload"/> 는 핸들러가 반환하면
    /// 무효가 되므로, <c>await</c> 너머로 들고 가려면 복사가 <b>반드시</b> 필요하다.
    /// 프로덕션 코드라면 여기서 역직렬화까지 끝내 복사를 피한다.
    /// </remarks>
    private static async ValueTask<DispatchStatus> Capture(
        MessageContext context,
        ChannelWriter<(uint Sequence, byte[] Payload)> writer)
    {
        // WriteAsync 여야 한다 — 유계 Wait 채널에 TryWrite 를 쓰면 포화 시 조용히
        // 버려진다(CLAUDE.md 9.6 의 레거시 결함 조합).
        // 페이로드 복사는 await 너머로 들고 가기 위한 필수 조치다(위 remarks).
        (uint, byte[]) response = (context.Envelope.Sequence, context.Payload.ToArray());
        await writer.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);
        return DispatchStatus.Handled;
    }

    private static async Task<bool> RunEchoRoundTripsAsync(
        IConnection connection,
        IFrameEncoder encoder,
        ChannelReader<(uint Sequence, byte[] Payload)> responses)
    {
        byte[] payload = Encoding.UTF8.GetBytes("ChServerM 에코 페이로드");

        for (int i = 0; i < SelfTestFrames; i++)
        {
            await FrameWriter.WriteFrameAsync(
                connection.Output, encoder, EchoProtocol.Echo, payload,
                FrameFlags.None, (uint)i, connection.ConnectionClosed).ConfigureAwait(false);

            (uint sequence, byte[] echoed) = await responses.ReadAsync(connection.ConnectionClosed)
                .ConfigureAwait(false);

            if (sequence != (uint)i || !payload.AsSpan().SequenceEqual(echoed))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>소스 제너레이터가 등록한 타입 있는 핸들러를 한 번 왕복시킨다.</summary>
    /// <remarks>
    /// 요청은 MemoryPack(기본 직렬화기, ADR-0013)으로 직렬화한 <c>string</c> 이고,
    /// 서버 쪽 역직렬화·핸들러 등록은 전부 생성 코드다. 이 왕복이 성공하면
    /// "생성 코드 경로가 동작한다"가 종단으로 실증된다 — AOT publish 에서도 같은 코드가 돈다.
    /// </remarks>
    private static async Task<bool> RunGeneratedGreetAsync(
        IConnection connection,
        IFrameEncoder encoder,
        ChannelReader<(uint Sequence, byte[] Payload)> responses)
    {
        MemoryPackMessageSerializer<string> serializer = new();
        ArrayBufferWriter<byte> request = new();
        serializer.Serialize(request, "세계");

        await FrameWriter.WriteFrameAsync(
            connection.Output, encoder, EchoProtocol.Greet, request.WrittenSpan,
            FrameFlags.None, sequence: 0, connection.ConnectionClosed).ConfigureAwait(false);

        (_, byte[] reply) = await responses.ReadAsync(connection.ConnectionClosed).ConfigureAwait(false);

        return Encoding.UTF8.GetString(reply) == "안녕, 세계";
    }

    private static async Task<long> ReadStatsAsync(
        IConnection connection,
        IFrameEncoder encoder,
        ChannelReader<(uint Sequence, byte[] Payload)> responses)
    {
        await FrameWriter.WriteFrameAsync(
            connection.Output, encoder, EchoProtocol.Stats, ReadOnlySpan<byte>.Empty,
            FrameFlags.None, sequence: 0, connection.ConnectionClosed).ConfigureAwait(false);

        (_, byte[] payload) = await responses.ReadAsync(connection.ConnectionClosed).ConfigureAwait(false);

        return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload);
    }
}
