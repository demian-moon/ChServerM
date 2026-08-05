using System;
using System.Buffers;
using Xunit;

namespace ChServerM.Serialization.FlatBuffers.Tests;

/// <summary>
/// <see cref="FlatSharpMessageSerializer{TMessage}"/> 의 계약 검증.
/// </summary>
/// <remarks>
/// FlatBuffers 는 오프셋 기반 포맷이라 "임의 바이트 거부"를 포맷이 보장하지 않는다.
/// 여기서 고정하는 것은 왕복 보존, 분절 복사 경로, 예외 무유출, 그리고
/// <b>Lazy 직렬화기의 조립 시점 거부</b>(버퍼 수명 계약)다.
/// </remarks>
public sealed class FlatSharpMessageSerializerTests
{
    private static readonly FlatSharpMessageSerializer<FbChatMessage> Serializer =
        new(FbChatMessage.Serializer);

    private static FbChatMessage SampleMessage() => new()
    {
        Sender = "심연",
        Text = "프레이밍과 직렬화는 독립 축이다",
        Timestamp = 1_722_800_000_000,
    };

    private static byte[] SerializeToArray(FbChatMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        Serializer.Serialize(writer, message);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void Roundtrip_SingleSegment()
    {
        FbChatMessage original = SampleMessage();
        byte[] encoded = SerializeToArray(original);

        Assert.True(Serializer.TryDeserialize(new ReadOnlySequence<byte>(encoded), out FbChatMessage decoded));
        Assert.Equal(original.Sender, decoded.Sender);
        Assert.Equal(original.Text, decoded.Text);
        Assert.Equal(original.Timestamp, decoded.Timestamp);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Roundtrip_MultiSegment_CopiesAndParses(int segmentSize)
    {
        FbChatMessage original = SampleMessage();
        ReadOnlySequence<byte> fragmented = SequenceFactory.Split(SerializeToArray(original), segmentSize);
        Assert.False(fragmented.IsSingleSegment);

        Assert.True(Serializer.TryDeserialize(fragmented, out FbChatMessage decoded));
        Assert.Equal(original.Text, decoded.Text);
    }

    [Fact]
    public void EmptyPayload_ReturnsFalse()
    {
        Assert.False(Serializer.TryDeserialize(ReadOnlySequence<byte>.Empty, out _));
    }

    [Fact]
    public void GarbageBytes_NeverThrow()
    {
        Random random = new(20260805);
        byte[] buffer = new byte[64];

        for (int i = 0; i < 1000; i++)
        {
            random.NextBytes(buffer);
            _ = Serializer.TryDeserialize(new ReadOnlySequence<byte>(buffer), out _);
        }
    }

    [Fact]
    public void SerializeNull_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentNullException>(() => Serializer.Serialize(writer, null!));
    }

    [Fact]
    public void LazySerializer_RejectedAtConstruction()
    {
        // Lazy 는 반환 객체가 페이로드 버퍼를 참조한다 — 계약(호출 후 페이로드 무효) 위반.
        // 런타임 use-after-free 류 버그가 되기 전에 조립 시점에 거부한다.
        Assert.Throws<ArgumentException>(
            () => new FlatSharpMessageSerializer<FbLazyMessage>(FbLazyMessage.Serializer));
    }
}
