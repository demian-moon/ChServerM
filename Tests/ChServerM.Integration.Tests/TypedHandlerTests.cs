using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 직렬화 축이 인터페이스만으로 꽂히는지 검증한다.
/// </summary>
/// <remarks>
/// <b>여기 있는 핸들러는 전송도, 프레이밍도, 직렬화 포맷도 알지 못한다.</b>
/// <c>string</c> 을 받아 <c>string</c> 을 돌려줄 뿐이다. 그것이 ADR-0004 의 합격 기준이다 —
/// 같은 핸들러 코드가 축 조합을 갈아끼워도 동작한다.
/// </remarks>
public sealed class TypedHandlerTests
{
    private const ushort GreetMessageId = 300;

    /// <summary>받은 이름에 인사를 붙여 돌려주는 핸들러.</summary>
    private sealed class GreetHandler(IFrameEncoder encoder) : IMessageHandler<string>
    {
        public ConcurrentQueue<string> Received { get; } = new();

        public async ValueTask HandleAsync(MessageContext context, string message)
        {
            Received.Enqueue(message);

            byte[] reply = Encoding.UTF8.GetBytes($"안녕, {message}");
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
    public async Task TypedHandler_ReceivesDeserializedMessage()
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        GreetHandler handler = new(encoder);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.Map(new MessageId(GreetMessageId), Utf8StringSerializer.Instance, handler));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, GreetMessageId, "세계"u8);

        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal("안녕, 세계", Encoding.UTF8.GetString(reply));
        Assert.Single(handler.Received);
        Assert.Equal("세계", handler.Received.TryDequeue(out string? first) ? first : null);
    }

    [Fact]
    public async Task TypedHandler_HandlesMultiSegmentPayload()
    {
        // 페이로드가 파이프 세그먼트를 넘으면 직렬화기가 분절된 시퀀스를 받는다.
        FixedHeaderFrameEncoder encoder = new(64 * 1024);
        GreetHandler handler = new(encoder);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.Map(new MessageId(GreetMessageId), Utf8StringSerializer.Instance, handler),
            maxPayloadLength: 64 * 1024);

        await using IConnection connection = await harness.ConnectAsync();

        string longName = new('가', 5000);
        await harness.SendAsync(connection, GreetMessageId, Encoding.UTF8.GetBytes(longName));

        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal($"안녕, {longName}", Encoding.UTF8.GetString(reply));
    }

    [Fact]
    public async Task EmptyPayload_DeserializesToEmptyString()
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        GreetHandler handler = new(encoder);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.Map(new MessageId(GreetMessageId), Utf8StringSerializer.Instance, handler));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, GreetMessageId, []);

        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal("안녕, ", Encoding.UTF8.GetString(reply));
    }

    [Fact]
    public async Task DeserializationFailure_ClosesConnectionByDefault()
    {
        // 길이와 식별자는 맞는데 내용을 읽을 수 없다 = 스키마가 어긋났거나 조작된 입력.
        // 둘 다 계속할 이유가 없다.
        FixedHeaderFrameEncoder encoder = new(4096);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.Map(
                new MessageId(GreetMessageId),
                new AlwaysFailingSerializer(),
                new GreetHandler(encoder)));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, GreetMessageId, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task DeserializationFailure_KeepsConnection_WhenConfigured()
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        GreetHandler handler = new(encoder);
        const ushort RawEchoId = 301;

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder
                .Map(new MessageId(GreetMessageId), new AlwaysFailingSerializer(), handler)
                .MapRaw(new MessageId(RawEchoId), async context =>
                {
                    await FrameWriter.WriteFrameAsync(
                        context.Connection.Output, encoder, context.Envelope.MessageId, context.Payload,
                        FrameFlags.None, context.Envelope.Sequence,
                        context.CancellationToken).ConfigureAwait(false);
                    return DispatchStatus.Handled;
                }),
            connectionOptions: new FramedConnectionOptions { CloseOnDeserializationFailure = false });

        await using IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, GreetMessageId, [1, 2, 3]);
        await harness.SendAsync(connection, RawEchoId, [8, 8]);

        (_, byte[] reply) = await harness.ReceiveAsync(connection, TestTimeout.Token);
        Assert.Equal<byte>([8, 8], reply);
        Assert.Empty(handler.Received);
    }

    /// <summary>항상 역직렬화에 실패하는 직렬화기.</summary>
    private sealed class AlwaysFailingSerializer : Serialization.IMessageSerializer<string>
    {
        public void Serialize(IBufferWriter<byte> writer, in string message) =>
            throw new NotSupportedException("이 테스트는 수신 경로만 쓴다.");

        public bool TryDeserialize(in ReadOnlySequence<byte> payload, out string message)
        {
            message = string.Empty;
            return false;
        }
    }
}
