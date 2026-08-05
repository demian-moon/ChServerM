using System.Buffers;
using System.Text;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Dispatch.Generated;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Serialization.MemoryPack;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 소스 제너레이터 종단 검증 — <c>[MessageHandler]</c> 선언만으로 등록 코드가 생성되고,
/// 그 코드가 실제 파이프라인(프레이밍·디스패치·전송)에서 동작한다.
/// </summary>
/// <remarks>
/// 이 어셈블리는 <c>ChServerM.SourceGen</c> 을 <b>분석기로만</b> 참조한다.
/// <see cref="GeneratedMessageHandlerMap.MapGeneratedHandlers"/> 가 컴파일된다는 사실
/// 자체가 생성이 성립했다는 증거이고, 직렬화기는 제공자(<see cref="MemoryPackMessageSerializerProvider"/>)
/// 경유로 조립 시점에 해석된다 — 리플렉션 0.
/// </remarks>
public sealed class GeneratedDispatchTests
{
    private const ushort GeneratedGreetId = 410;

    /// <summary>[MessageHandler] 로 선언된 핸들러. 등록 코드는 제너레이터가 만든다.</summary>
    [MessageHandler(GeneratedGreetId)]
    internal sealed class GeneratedGreetHandler(IFrameEncoder encoder) : IMessageHandler<string>
    {
        public async ValueTask HandleAsync(MessageContext context, string message)
        {
            byte[] reply = Encoding.UTF8.GetBytes($"생성:{message}");
            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Envelope.MessageId,
                reply,
                FrameFlags.None,
                context.Envelope.Sequence,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task GeneratedMap_RegistersHandler_EndToEnd()
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        MemoryPackMessageSerializer<string> serializer = new();

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapGeneratedHandlers(
                MemoryPackMessageSerializerProvider.Instance,
                handler410: new GeneratedGreetHandler(encoder)));

        await using IConnection connection = await harness.ConnectAsync();

        ArrayBufferWriter<byte> request = new();
        serializer.Serialize(request, "세계");
        await harness.SendAsync(connection, GeneratedGreetId, request.WrittenSpan);

        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal("생성:세계", Encoding.UTF8.GetString(reply));
    }
}
