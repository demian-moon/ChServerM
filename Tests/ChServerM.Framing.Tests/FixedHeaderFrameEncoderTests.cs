using System;
using System.Buffers;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// 인코더가 잘못된 프레임을 내보내면 상대가 커넥션을 끊는다. 그때는 원인이
/// 이쪽 코드라는 걸 알기 어려우므로, 보내기 전에 예외로 드러내는지 검증한다.
/// </summary>
public sealed class FixedHeaderFrameEncoderTests
{
    private static readonly FixedHeaderFrameEncoder Encoder = new(maxPayloadLength: 1024);

    [Fact]
    public void WriteHeader_AdvancesExactlyHeaderSize()
    {
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 0));

        Assert.Equal(FrameHeader.Size, writer.WrittenCount);
    }

    [Fact]
    public void WriteHeader_ThenPayload_ProducesDecodableFrame()
    {
        // 인코더와 디코더가 같은 레이아웃을 보는지가 이 테스트의 요점이다.
        ArrayBufferWriter<byte> writer = new();
        byte[] payload = [1, 2, 3, 4, 5];

        Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(77), payload.Length, FrameFlags.Compressed, 5));
        writer.Write(payload);

        FrameDecodeResult result = new FixedHeaderFrameDecoder(1024)
            .Decode(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Assert.True(result.IsDecoded);
        Assert.Equal(new MessageId(77), result.Header.MessageId);
        Assert.Equal(FrameFlags.Compressed, result.Header.Flags);
        Assert.Equal(5u, result.Header.Sequence);
        Assert.Equal(payload, result.Payload.ToArray());
    }

    [Fact]
    public void WriteHeader_MultipleFrames_AreIndependentlyDecodable()
    {
        ArrayBufferWriter<byte> writer = new();
        FixedHeaderFrameDecoder decoder = new(1024);

        for (ushort id = 1; id <= 3; id++)
        {
            byte[] payload = new byte[id * 10];
            Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(id), payload.Length));
            writer.Write(payload);
        }

        ReadOnlySequence<byte> buffer = new(writer.WrittenMemory);
        for (ushort id = 1; id <= 3; id++)
        {
            FrameDecodeResult result = decoder.Decode(buffer);
            Assert.True(result.IsDecoded);
            Assert.Equal(new MessageId(id), result.Header.MessageId);
            Assert.Equal(id * 10, result.Payload.Length);
            buffer = buffer.Slice(result.Consumed);
        }

        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void CreateHeader_StampsTheEncoderVersion()
    {
        FixedHeaderFrameEncoder encoder = new(1024, protocolVersion: 7);

        Assert.Equal(7, encoder.CreateHeader(new MessageId(1), 0).Version);
    }

    [Fact]
    public void WriteHeader_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Encoder.WriteHeader(null!, Encoder.CreateHeader(new MessageId(1), 0)));
    }

    [Fact]
    public void WriteHeader_PayloadOverLimit_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 1025)));
    }

    [Fact]
    public void WriteHeader_PayloadExactlyAtLimit_IsAccepted()
    {
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 1024));

        Assert.Equal(FrameHeader.Size, writer.WrittenCount);
    }

    [Fact]
    public void WriteHeader_VersionMismatch_Throws()
    {
        // 버전은 인코더의 설정이지 메시지의 속성이 아니다. 어긋나면 프로토콜이 조용히 깨진다.
        ArrayBufferWriter<byte> writer = new();
        FrameHeader wrongVersion = new(new MessageId(1), 0, version: 99);

        Assert.Throws<ArgumentException>(() => Encoder.WriteHeader(writer, wrongVersion));
    }

    [Fact]
    public void WriteHeader_UnknownFlagBit_Throws()
    {
        ArrayBufferWriter<byte> writer = new();
        FrameHeader badFlags = new(new MessageId(1), 0, (FrameFlags)0x8000);

        Assert.Throws<ArgumentException>(() => Encoder.WriteHeader(writer, badFlags));
    }

    [Fact]
    public void WriteHeader_FailedValidation_WritesNothing()
    {
        // 예외를 던지고 반쯤 쓴 상태로 두면 스트림이 오염된다.
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 99999)));

        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void HeaderSize_MatchesCoreConstant()
    {
        Assert.Equal(FrameHeader.Size, Encoder.HeaderSize);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FixedHeaderFrameEncoder(null!));
    }

    [Fact]
    public void Constructor_InvalidOptions_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new FixedHeaderFrameEncoder(maxPayloadLength: 0));
    }
}
