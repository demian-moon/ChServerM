using System;
using System.Buffers;
using ChServerM.Serialization;
using Xunit;

namespace ChServerM.Serialization.Protobuf.Tests;

/// <summary>
/// <see cref="ProtobufMessageSerializer{TMessage}"/> 의 계약 검증.
/// </summary>
/// <remarks>
/// MemoryPack 테스트와 달리 "모든 절단 지점 실패"를 주장하지 않는다 — proto3 는
/// 필드 경계에서 잘리면 유효한 더 짧은 메시지로 읽히는 관대한 포맷이다.
/// 여기서 고정하는 것은 "예외가 새지 않는다"와 "명백한 손상은 실패한다"까지다.
/// </remarks>
public sealed class ProtobufMessageSerializerTests
{
    private static readonly ProtobufMessageSerializer<ProtoChatMessage> Serializer = new();

    private static ProtoChatMessage SampleMessage() => new()
    {
        Sender = "심연",
        Text = "프레이밍과 직렬화는 독립 축이다",
        Timestamp = 1_722_800_000_000,
    };

    private static byte[] SerializeToArray(ProtoChatMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        Serializer.Serialize(writer, message);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void Roundtrip_SingleSegment()
    {
        ProtoChatMessage original = SampleMessage();
        byte[] encoded = SerializeToArray(original);

        Assert.True(Serializer.TryDeserialize(new ReadOnlySequence<byte>(encoded), out ProtoChatMessage decoded));
        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Roundtrip_MultiSegment(int segmentSize)
    {
        ProtoChatMessage original = SampleMessage();
        ReadOnlySequence<byte> fragmented = SequenceFactory.Split(SerializeToArray(original), segmentSize);
        Assert.False(fragmented.IsSingleSegment);

        Assert.True(Serializer.TryDeserialize(fragmented, out ProtoChatMessage decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void EmptyPayload_IsValidDefaultInstance()
    {
        // proto3 특성: 빈 바이트열은 "모든 필드가 기본값인 메시지"다. 실패가 아니다.
        // MemoryPack 과 다른 지점이므로 테스트로 명시해 둔다.
        Assert.True(Serializer.TryDeserialize(ReadOnlySequence<byte>.Empty, out ProtoChatMessage decoded));
        Assert.Equal(string.Empty, decoded.Sender);
    }

    [Fact]
    public void CorruptedTag_ReturnsFalse_WithoutThrowing()
    {
        // 태그 0 은 proto 에서 항상 무효다.
        byte[] invalid = [0x00, 0x01, 0x02];

        Assert.False(Serializer.TryDeserialize(new ReadOnlySequence<byte>(invalid), out _));
    }

    [Fact]
    public void TruncatedInsideField_ReturnsFalse_WithoutThrowing()
    {
        // 문자열 길이 헤더 뒤에서 자르면 필드 경계가 아니므로 반드시 실패한다.
        byte[] encoded = SerializeToArray(SampleMessage());
        ReadOnlySequence<byte> truncated = new(encoded.AsMemory(0, 3));

        Assert.False(Serializer.TryDeserialize(truncated, out _));
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
}
