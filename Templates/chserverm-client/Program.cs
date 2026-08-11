using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;

// 서버(chserverm-server 템플릿)와 같은 프레이밍 설정이어야 한다.
const int MaxPayload = 64 * 1024;
const int MaxFrame = MaxPayload + FrameHeader.Size;

FramingOptions framing = new() { MaxPayloadLength = MaxPayload };
FixedHeaderFrameEncoder encoder = new(framing);

TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

// 클라이언트도 같은 조립 검증을 받는다 — 서버와 같은 임계값을 준다.
await using ChServerMClient client = new ClientBuilder()
    .UseTransport(new TcpClientTransport(new TcpTransportOptions
    {
        PauseWriterThreshold = 2L * MaxFrame,
        ResumeWriterThreshold = MaxFrame,
    }))
    .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
    .ConfigureDispatcher(dispatcher => dispatcher
        .MapRaw(new MessageId(1), context =>
        {
            // Payload 는 핸들러가 반환하면 무효가 된다 — await 너머로 들고 가려면 복사한다.
            echoed.TrySetResult(context.Payload.ToArray());
            return new ValueTask<DispatchStatus>(DispatchStatus.Handled);
        }))
    .Build();

ClientSession session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

await FrameWriter.WriteFrameAsync(
    session.Connection.Output, encoder, new MessageId(1),
    "안녕, ChServerM"u8, FrameFlags.None, sequence: 0, session.Connection.ConnectionClosed);

Console.WriteLine($"에코: {Encoding.UTF8.GetString(await echoed.Task)}");

await session.Connection.DisposeAsync();
