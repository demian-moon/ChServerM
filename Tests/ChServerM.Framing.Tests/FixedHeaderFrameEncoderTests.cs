using System;
using System.Buffers;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// ?몄퐫?붽? ?섎せ???꾨젅?꾩쓣 ?대낫?대㈃ ?곷?媛 而ㅻ꽖?섏쓣 ?딅뒗?? 洹몃븣???먯씤??/// ?댁そ 肄붾뱶?쇰뒗 嫄??뚭린 ?대젮?곕?濡? 蹂대궡湲??꾩뿉 ?덉쇅濡??쒕윭?대뒗吏 寃利앺븳??
/// </summary>
public sealed class FixedHeaderFrameEncoderTests
{
    private static readonly FixedHeaderFrameEncoder Encoder = new(maxPayloadLength: 1024);

    [Fact]
    public void WriteHeader_AdvancesExactlyHeaderSize()
    {
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 0, FrameFlags.None, 0));

        Assert.Equal(FrameHeader.Size, writer.WrittenCount);
    }

    [Fact]
    public void WriteHeader_ThenPayload_ProducesDecodableFrame()
    {
        // ?몄퐫?붿? ?붿퐫?붽? 媛숈? ?덉씠?꾩썐??蹂대뒗吏媛 ???뚯뒪?몄쓽 ?붿젏?대떎.
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
            Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(id), payload.Length, FrameFlags.None, 0));
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

        Assert.Equal(7, encoder.CreateHeader(new MessageId(1), 0, FrameFlags.None, 0).Version);
    }

    [Fact]
    public void WriteHeader_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Encoder.WriteHeader(null!, Encoder.CreateHeader(new MessageId(1), 0, FrameFlags.None, 0)));
    }

    [Fact]
    public void WriteHeader_PayloadOverLimit_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 1025, FrameFlags.None, 0)));
    }

    [Fact]
    public void WriteHeader_PayloadExactlyAtLimit_IsAccepted()
    {
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 1024, FrameFlags.None, 0));

        Assert.Equal(FrameHeader.Size, writer.WrittenCount);
    }

    [Fact]
    public void WriteHeader_VersionMismatch_Throws()
    {
        // 踰꾩쟾? ?몄퐫?붿쓽 ?ㅼ젙?댁? 硫붿떆吏???띿꽦???꾨땲?? ?닿툔?섎㈃ ?꾨줈?좎퐳??議곗슜??源⑥쭊??
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
        // ?덉쇅瑜??섏?怨?諛섏? ???곹깭濡??먮㈃ ?ㅽ듃由쇱씠 ?ㅼ뿼?쒕떎.
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Encoder.WriteHeader(writer, Encoder.CreateHeader(new MessageId(1), 99999, FrameFlags.None, 0)));

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
