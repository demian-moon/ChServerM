using System;
using System.Buffers;
using MemoryPack;
using Xunit;

namespace ChServerM.Serialization.MemoryPack.Tests;

/// <summary>
/// <see cref="MemoryPackMessageSerializer{TMessage}"/> 의 계약 검증.
/// </summary>
/// <remarks>
/// 검증하는 계약은 넷이다 — 왕복 보존, 분절 시퀀스 무평탄화 처리,
/// 손상 입력의 무예외 실패(<c>TryXxx</c> 규약), null 메시지 거부.
/// </remarks>
public sealed class MemoryPackMessageSerializerTests
{
    private static readonly MemoryPackMessageSerializer<ChatMessage> ClassSerializer = new();

    private static byte[] SerializeToArray(ChatMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        ClassSerializer.Serialize(writer, message);
        return writer.WrittenSpan.ToArray();
    }

    private static ChatMessage SampleMessage() => new()
    {
        Sender = "심연",
        Text = "프레이밍과 직렬화는 독립 축이다",
        Timestamp = 1_722_800_000_000,
    };

    [Fact]
    public void Roundtrip_Class_SingleSegment()
    {
        ChatMessage original = SampleMessage();
        byte[] encoded = SerializeToArray(original);

        Assert.True(ClassSerializer.TryDeserialize(new ReadOnlySequence<byte>(encoded), out ChatMessage decoded));
        Assert.Equal(original.Sender, decoded.Sender);
        Assert.Equal(original.Text, decoded.Text);
        Assert.Equal(original.Timestamp, decoded.Timestamp);
    }

    [Fact]
    public void Roundtrip_Struct()
    {
        MemoryPackMessageSerializer<MoveCommand> serializer = new();
        MoveCommand original = new(X: 12.5f, Y: -3.25f, Tick: 7777);

        ArrayBufferWriter<byte> writer = new();
        serializer.Serialize(writer, original);

        Assert.True(serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out MoveCommand decoded));
        Assert.Equal(original.X, decoded.X);
        Assert.Equal(original.Y, decoded.Y);
        Assert.Equal(original.Tick, decoded.Tick);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Roundtrip_MultiSegment(int segmentSize)
    {
        // PipeReader 가 주는 버퍼는 연속 메모리 보장이 없다. 세그먼트 크기 1까지 내려
        // 어떤 분절에서도 평탄화 없이 읽히는지 본다.
        ChatMessage original = SampleMessage();
        ReadOnlySequence<byte> fragmented = Split(SerializeToArray(original), segmentSize);
        Assert.False(fragmented.IsSingleSegment);

        Assert.True(ClassSerializer.TryDeserialize(fragmented, out ChatMessage decoded));
        Assert.Equal(original.Text, decoded.Text);
    }

    [Fact]
    public void EmptyPayload_ReturnsFalse()
    {
        Assert.False(ClassSerializer.TryDeserialize(ReadOnlySequence<byte>.Empty, out _));
    }

    [Fact]
    public void TruncatedPayload_AllPrefixes_ReturnFalse_WithoutThrowing()
    {
        // 프레이밍 퍼징과 같은 원칙 — 모든 절단 지점이 "예외 없는 실패"여야 한다.
        byte[] encoded = SerializeToArray(SampleMessage());

        for (int length = 0; length < encoded.Length; length++)
        {
            ReadOnlySequence<byte> truncated = new(encoded.AsMemory(0, length));
            Assert.False(ClassSerializer.TryDeserialize(truncated, out _));
        }
    }

    [Fact]
    public void TrailingBytes_ReturnFalse()
    {
        // 프레이밍이 길이를 정확히 알려주므로 잔여 바이트는 스키마 불일치이거나 조작이다.
        byte[] encoded = SerializeToArray(SampleMessage());
        byte[] padded = new byte[encoded.Length + 1];
        encoded.CopyTo(padded, 0);

        Assert.False(ClassSerializer.TryDeserialize(new ReadOnlySequence<byte>(padded), out _));
    }

    [Fact]
    public void NullEncodedPayload_ReturnsFalse()
    {
        // 와이어에 null 객체가 실려 오면 메시지가 아니다 — 핸들러에 null 을 넘기지 않는다.
        byte[] encodedNull = MemoryPackSerializer.Serialize<ChatMessage?>(null);

        Assert.False(ClassSerializer.TryDeserialize(new ReadOnlySequence<byte>(encodedNull), out _));
    }

    [Fact]
    public void SerializeNull_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentNullException>(() => ClassSerializer.Serialize(writer, null!));
    }

    [Fact]
    public void GarbageBytes_NeverThrow()
    {
        // 시드 고정 난수 1000회 — 파싱 결과는 묻지 않는다. 예외가 새지 않는 것만 본다.
        Random random = new(20260805);
        byte[] buffer = new byte[64];

        for (int i = 0; i < 1000; i++)
        {
            random.NextBytes(buffer);
            _ = ClassSerializer.TryDeserialize(new ReadOnlySequence<byte>(buffer), out _);
        }
    }

    /// <summary>배열을 지정 크기 세그먼트로 쪼갠 분절 시퀀스를 만든다.</summary>
    private static ReadOnlySequence<byte> Split(byte[] data, int segmentSize)
    {
        Segment first = new(data.AsMemory(0, Math.Min(segmentSize, data.Length)), runningIndex: 0);
        Segment last = first;

        for (int offset = segmentSize; offset < data.Length; offset += segmentSize)
        {
            int length = Math.Min(segmentSize, data.Length - offset);
            last = last.Append(data.AsMemory(offset, length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
