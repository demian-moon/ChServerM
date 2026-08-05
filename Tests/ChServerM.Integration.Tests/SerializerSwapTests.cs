using System;
using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Serialization;
using ChServerM.Serialization.FlatBuffers;
using ChServerM.Serialization.MemoryPack;
using ChServerM.Serialization.Protobuf;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 직렬화 축 교체 테스트 — <b>같은 핸들러 코드</b>가 직렬화기 교체만으로 동작한다.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync{TMessage}"/> 하나가 전체 시나리오다. 각 테스트의 차이는 넘기는
/// <see cref="IMessageSerializer{TMessage}"/> 와 메시지 타입뿐이고, 핸들러·프레이밍·전송·
/// 디스패치 코드는 한 글자도 다르지 않다. 이것이 축 교체 가능성의 증명 형식이다
/// (ADR-0004, ROADMAP DoD-5).
/// </para>
/// <para>
/// 메시지 <b>타입</b>은 직렬화기와 함께 바뀐다 — protobuf·FlatBuffers 는 스키마 생성
/// 타입만 다루므로 "같은 T, 다른 직렬화기"는 포맷 우주가 겹치는 경우(문자열 등)에만
/// 성립한다. 계약이 보장하는 것은 (T, 직렬화기) 쌍의 교체이지 T 의 불변이 아니다.
/// </para>
/// </remarks>
public sealed class SerializerSwapTests
{
    private const ushort EchoMessageId = 320;

    /// <summary>받은 메시지를 변환해 되돌리는 핸들러. 직렬화 포맷을 알지 못한다.</summary>
    private sealed class ReplyHandler<TMessage>(
        IFrameEncoder encoder,
        IMessageSerializer<TMessage> serializer,
        Func<TMessage, TMessage> makeReply) : IMessageHandler<TMessage>
    {
        public async ValueTask HandleAsync(MessageContext context, TMessage message)
        {
            ArrayBufferWriter<byte> buffer = new();
            serializer.Serialize(buffer, makeReply(message));

            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Envelope.MessageId,
                buffer.WrittenSpan,
                FrameFlags.None,
                context.Envelope.Sequence,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public Task Utf8Serializer_RoundTrips() => RunAsync(
        Utf8StringSerializer.Instance,
        request: "축교체",
        makeReply: static m => $"응답:{m}",
        assertReply: static m => Assert.Equal("응답:축교체", m));

    [Fact]
    public Task MemoryPackSerializer_SameHandlerCode_RoundTrips() => RunAsync(
        new MemoryPackMessageSerializer<string>(),
        request: "축교체",
        makeReply: static m => $"응답:{m}",
        assertReply: static m => Assert.Equal("응답:축교체", m));

    [Fact]
    public Task ProtobufSerializer_SameHandlerCode_RoundTrips() => RunAsync(
        new ProtobufMessageSerializer<SwapChatMessage>(),
        request: new SwapChatMessage { Text = "축교체" },
        makeReply: static m => new SwapChatMessage { Text = $"응답:{m.Text}" },
        assertReply: static m => Assert.Equal("응답:축교체", m.Text));

    [Fact]
    public Task FlatSharpSerializer_SameHandlerCode_RoundTrips() => RunAsync(
        new FlatSharpMessageSerializer<SwapFbMessage>(SwapFbMessage.Serializer),
        request: new SwapFbMessage { Text = "축교체" },
        makeReply: static m => new SwapFbMessage { Text = $"응답:{m.Text}" },
        assertReply: static m => Assert.Equal("응답:축교체", m.Text));

    private static async Task RunAsync<TMessage>(
        IMessageSerializer<TMessage> serializer,
        TMessage request,
        Func<TMessage, TMessage> makeReply,
        Action<TMessage> assertReply)
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        ReplyHandler<TMessage> handler = new(encoder, serializer, makeReply);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.Map(new MessageId(EchoMessageId), serializer, handler));

        await using IConnection connection = await harness.ConnectAsync();

        ArrayBufferWriter<byte> encodedRequest = new();
        serializer.Serialize(encodedRequest, request);
        await harness.SendAsync(connection, EchoMessageId, encodedRequest.WrittenSpan);

        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.True(serializer.TryDeserialize(new ReadOnlySequence<byte>(reply), out TMessage? decoded));
        assertReply(decoded!);
    }
}
