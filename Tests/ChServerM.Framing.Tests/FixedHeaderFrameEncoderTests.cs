using System;
using System.Buffers;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// 인코더가 잘못된 프레임을 내보내면 상대가 커넥션을 닫는다. 그때는 원인이
/// 이쪽 코드라는 걸 알기 어려우므로, 보내기 전에 예외로 드러나는지 검증한다.
/// </summary>
/// <remarks>
/// 원본 파일의 한글 주석이 인코딩 손상(PS5.1 ANSI 재저장 사고)으로 깨져 있던 것을
/// ADR-0010 개정과 함께 재작성했다.
/// </remarks>
public sealed class FixedHeaderFrameEncoderTests
{
    private static readonly FixedHeaderFrameEncoder Encoder = new(maxPayloadLength: 1024);

    private static MessageEnvelope Envelope(ushort id, FrameFlags flags = FrameFlags.None, uint sequence = 0) =>
        new(new MessageId(id), flags, sequence);

    [Fact]
    public void WriteHeader_AdvancesExactlyHeaderSize()
    {
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, Envelope(1), 0);

        Assert.Equal(FrameHeader.Size, writer.WrittenCount);
    }

    [Fact]
    public void WriteHeader_ThenPayload_ProducesDecodableFrame()
    {
        // 인코더와 디코더가 같은 레이아웃을 보는지가 이 테스트의 요점이다.
        ArrayBufferWriter<byte> writer = new();
        byte[] payload = [1, 2, 3, 4, 5];

        Encoder.WriteHeader(writer, Envelope(77, FrameFlags.Compressed, 5), payload.Length);
        writer.Write(payload);

        FrameDecodeResult result = new FixedHeaderFrameDecoder(1024)
            .Decode(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Assert.True(result.IsDecoded);
        Assert.Equal(new MessageId(77), result.Envelope.MessageId);
        Assert.Equal(FrameFlags.Compressed, result.Envelope.Flags);
        Assert.Equal(5u, result.Envelope.Sequence);
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
            Encoder.WriteHeader(writer, Envelope(id), payload.Length);
            writer.Write(payload);
        }

        ReadOnlySequence<byte> buffer = new(writer.WrittenMemory);
        for (ushort id = 1; id <= 3; id++)
        {
            FrameDecodeResult result = decoder.Decode(buffer);
            Assert.True(result.IsDecoded);
            Assert.Equal(new MessageId(id), result.Envelope.MessageId);
            Assert.Equal(id * 10, result.Payload.Length);
            buffer = buffer.Slice(result.Consumed);
        }

        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void WriteHeader_StampsTheEncoderVersion()
    {
        // 버전은 인코더의 설정이지 메시지의 속성이 아니다 (ADR-0010).
        // 같은 버전의 디코더만 이 인코더의 출력을 받아들여야 한다.
        FixedHeaderFrameEncoder encoder = new(1024, protocolVersion: 7);
        ArrayBufferWriter<byte> writer = new();

        encoder.WriteHeader(writer, Envelope(1), 0);

        FrameDecodeResult sameVersion = new FixedHeaderFrameDecoder(1024, acceptedVersion: 7)
            .Decode(new ReadOnlySequence<byte>(writer.WrittenMemory));
        FrameDecodeResult otherVersion = new FixedHeaderFrameDecoder(1024, acceptedVersion: 1)
            .Decode(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Assert.True(sameVersion.IsDecoded);
        Assert.Equal(FrameDecodeStatus.VersionMismatch, otherVersion.Status);
    }

    [Fact]
    public void WriteHeader_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Encoder.WriteHeader(null!, Envelope(1), 0));
    }

    [Fact]
    public void WriteHeader_NegativePayloadLength_Throws()
    {
        // 음수 길이는 uint 캐스팅을 거치며 거대한 양수가 된다. 쓰기 전에 걸러야 한다.
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Envelope(1), -1));
    }

    [Fact]
    public void WriteHeader_PayloadOverLimit_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Envelope(1), 1025));
    }

    [Fact]
    public void WriteHeader_PayloadExactlyAtLimit_IsAccepted()
    {
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, Envelope(1), 1024);

        Assert.Equal(FrameHeader.Size, writer.WrittenCount);
    }

    [Fact]
    public void WriteHeader_UnknownFlagBit_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentException>(
            () => Encoder.WriteHeader(writer, Envelope(1, (FrameFlags)0x8000), 0));
    }

    [Fact]
    public void WriteHeader_FailedValidation_WritesNothing()
    {
        // 예외를 던지고도 반쯤 쓴 상태로 남으면 스트림이 오염된다.
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Envelope(1), 99999));

        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void MaxHeaderSize_MatchesWireConstant()
    {
        // 고정 헤더이므로 상한이 곧 정확한 크기다.
        Assert.Equal(FrameHeader.Size, Encoder.MaxHeaderSize);
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
