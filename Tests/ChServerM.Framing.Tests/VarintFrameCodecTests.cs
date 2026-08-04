using System;
using System.Buffers;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// varint 프레이밍(프레이밍 축의 두 번째 구현) 검증.
/// 고정 헤더와 정반대 성질(가변 길이 헤더, 버전·플래그·일련번호 없음)이
/// 같은 <c>IFrameDecoder</c>/<c>IFrameEncoder</c> 계약에 들어오는지가 요점이다 (ADR-0010).
/// </summary>
public sealed class VarintFrameCodecTests
{
    private const int MaxPayloadLength = 4096;
    private static readonly VarintFrameDecoder Decoder = new(MaxPayloadLength);
    private static readonly VarintFrameEncoder Encoder = new(MaxPayloadLength);

    /// <summary>정규형 varint 프레임 하나를 손으로 조립한다.</summary>
    private static byte[] BuildFrame(ushort messageId, int payloadLength)
    {
        ArrayBufferWriter<byte> writer = new();
        Encoder.WriteHeader(writer, new MessageEnvelope(new MessageId(messageId), FrameFlags.None, 0), payloadLength);

        byte[] payload = new byte[payloadLength];
        for (int i = 0; i < payloadLength; i++)
        {
            payload[i] = (byte)i;
        }

        writer.Write(payload);
        return writer.WrittenSpan.ToArray();
    }

    // ── 정상 경로 ──────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0)]          // 최소 헤더 2바이트
    [InlineData(1, 127)]        // 길이 varint 1바이트 경계
    [InlineData(1, 128)]        // 길이 varint 2바이트 시작
    [InlineData(127, 300)]      // ID varint 1바이트 경계
    [InlineData(128, 300)]      // ID varint 2바이트 시작
    [InlineData(999, 2048)]
    [InlineData(ushort.MaxValue, 4096)] // ID varint 3바이트 + 페이로드 상한
    public void RoundTrip_AcrossVarintBoundaries(ushort messageId, int payloadLength)
    {
        byte[] frame = BuildFrame(messageId, payloadLength);

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.True(result.IsDecoded);
        Assert.Equal(new MessageId(messageId), result.Envelope.MessageId);
        Assert.Equal(FrameFlags.None, result.Envelope.Flags);
        Assert.Equal(0u, result.Envelope.Sequence);
        Assert.Equal(payloadLength, result.Payload.Length);
    }

    [Fact]
    public void SmallFrame_HeaderIsTwoBytes()
    {
        // 이 프레이밍의 존재 이유 — 작은 프레임의 오버헤드가 고정 헤더의 1/8이다.
        ArrayBufferWriter<byte> writer = new();

        Encoder.WriteHeader(writer, new MessageEnvelope(new MessageId(7), FrameFlags.None, 0), 42);

        Assert.Equal(2, writer.WrittenCount);
    }

    [Fact]
    public void Decode_PayloadContentIsCorrectlySliced()
    {
        // 가변 헤더에서는 오프셋 계산이 프레임마다 다르다. 내용까지 대조한다.
        byte[] frame = BuildFrame(300, 256);

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        byte[] expected = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            expected[i] = (byte)i;
        }

        Assert.Equal(expected, result.Payload.ToArray());
    }

    [Fact]
    public void Decode_MultipleFramesInOneBuffer_ReadsThemInOrder()
    {
        byte[] combined = [.. BuildFrame(1, 10), .. BuildFrame(200, 130), .. BuildFrame(3, 0)];
        ReadOnlySequence<byte> buffer = new(combined);

        ushort[] ids = new ushort[3];
        for (int i = 0; i < 3; i++)
        {
            FrameDecodeResult result = Decoder.Decode(buffer);
            Assert.True(result.IsDecoded);
            ids[i] = result.Envelope.MessageId.Value;
            buffer = buffer.Slice(result.Consumed);
        }

        Assert.Equal<ushort>([1, 200, 3], ids);
        Assert.Equal(0, buffer.Length);
    }

    // ── 부분 수신 ──────────────────────────────────────────────

    [Fact]
    public void Decode_EveryTruncationPoint_ReturnsNeedMoreData()
    {
        // varint 중간 절단(헤더가 가변이라 고정 헤더보다 절단 지점 종류가 많다)까지 포함해
        // 모든 접두사가 NeedMoreData 여야 한다.
        byte[] frame = BuildFrame(300, 200); // 길이 2바이트 + ID 2바이트 헤더

        for (int cut = 0; cut < frame.Length; cut++)
        {
            FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame.AsMemory(0, cut)));

            Assert.Equal(FrameDecodeStatus.NeedMoreData, result.Status);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(64)]
    public void Decode_AcrossSegmentBoundaries_StillFindsTheFrame(int segmentSize)
    {
        byte[] frame = BuildFrame(999, 200);

        FrameDecodeResult result = Decoder.Decode(SequenceFactory.Segmented(frame, segmentSize));

        Assert.True(result.IsDecoded, $"세그먼트 크기 {segmentSize}에서 실패했다.");
        Assert.Equal(new MessageId(999), result.Envelope.MessageId);
        Assert.Equal(200, result.Payload.Length);
    }

    // ── 실패 경로 ──────────────────────────────────────────────

    [Fact]
    public void Decode_NonCanonicalVarint_IsMalformed()
    {
        // 0x80 0x00 은 0 의 비정규 표현. 같은 프레임의 표현이 여럿이면
        // 바이트 단위 검증(Phase 9 AEAD)이 흔들린다.
        byte[] frame = [0x80, 0x00, 0x01];

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.Equal(FrameDecodeStatus.Malformed, result.Status);
    }

    [Fact]
    public void Decode_VarintOverflowingUInt32_IsMalformed()
    {
        // 다섯째 바이트의 상위 4비트가 켜져 있으면 u32 를 넘는다.
        byte[] frame = [0xFF, 0xFF, 0xFF, 0xFF, 0x1F, 0x01];

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.Equal(FrameDecodeStatus.Malformed, result.Status);
    }

    [Fact]
    public void Decode_SixByteVarint_IsMalformed()
    {
        // u32 varint 는 최대 5바이트다. 연장 비트가 5바이트를 넘게 이어지면 잘못된 입력이다.
        byte[] frame = [0x80, 0x80, 0x80, 0x80, 0x80, 0x01];

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.Equal(FrameDecodeStatus.Malformed, result.Status);
    }

    [Fact]
    public void Decode_LengthOverLimit_IsTooLarge_BeforePayloadArrives()
    {
        // 페이로드가 한 바이트도 오기 전에 판정해야 한다 — 길이 필드는 상대의 주장일 뿐이다.
        // 4097(상한 4096 + 1)의 정규형 varint: 0x81 0x20.
        byte[] frame = [0x81, 0x20];

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.Equal(FrameDecodeStatus.TooLarge, result.Status);
    }

    [Fact]
    public void Decode_MessageIdOverUInt16_IsMalformed()
    {
        // 길이 0 + ID 0x10000 (ushort 초과).
        byte[] frame = [0x00, 0x80, 0x80, 0x04];

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.Equal(FrameDecodeStatus.Malformed, result.Status);
    }

    [Fact]
    public void Decode_RandomBytes_NeverThrows_AndAlwaysMakesProgress()
    {
        // 퍼징 축약판 — 어떤 입력에도 예외 없이 상태로만 답해야 한다.
        Random random = new(20260804); // 시드 고정 — 실패 재현 가능해야 한다
        byte[] scratch = new byte[64];

        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(0, scratch.Length);
            random.NextBytes(scratch.AsSpan(0, length));
            ReadOnlySequence<byte> buffer = new(scratch.AsMemory(0, length));

            FrameDecodeResult result = Decoder.Decode(buffer);

            if (result.Status == FrameDecodeStatus.NeedMoreData)
            {
                // 버퍼 끝까지 검사했어야 파이프가 더 읽는다.
                Assert.Equal(0, buffer.Slice(result.Examined).Length);
            }
            else if (result.IsDecoded)
            {
                // 반드시 전진한다 — 헤더 최소 2바이트는 소비해야 한다.
                Assert.True(buffer.Slice(result.Consumed).Length <= buffer.Length - 2);
            }
            else
            {
                Assert.True(result.IsFatal);
            }
        }
    }

    // ── 인코더 검증 ────────────────────────────────────────────

    [Fact]
    public void WriteHeader_CompressedFlag_ThrowsNotSupported()
    {
        // 조용히 버리면 압축 표시가 유실된 페이로드를 상대가 원본으로 해석한다 (ADR-0010).
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<NotSupportedException>(() => Encoder.WriteHeader(
            writer, new MessageEnvelope(new MessageId(1), FrameFlags.Compressed, 0), 0));
    }

    [Fact]
    public void WriteHeader_NonZeroSequence_ThrowsNotSupported()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<NotSupportedException>(() => Encoder.WriteHeader(
            writer, new MessageEnvelope(new MessageId(1), FrameFlags.None, 42), 0));
    }

    [Fact]
    public void WriteHeader_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Encoder.WriteHeader(
            null!, new MessageEnvelope(new MessageId(1), FrameFlags.None, 0), 0));
    }

    [Fact]
    public void WriteHeader_NegativePayloadLength_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => Encoder.WriteHeader(
            writer, new MessageEnvelope(new MessageId(1), FrameFlags.None, 0), -1));
    }

    [Fact]
    public void WriteHeader_PayloadOverLimit_Throws()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => Encoder.WriteHeader(
            writer, new MessageEnvelope(new MessageId(1), FrameFlags.None, 0), MaxPayloadLength + 1));
    }

    [Fact]
    public void WriteHeader_FailedValidation_WritesNothing()
    {
        // 예외를 던지고도 반쯤 쓴 상태로 남으면 스트림이 오염된다.
        ArrayBufferWriter<byte> writer = new();

        Assert.Throws<NotSupportedException>(() => Encoder.WriteHeader(
            writer, new MessageEnvelope(new MessageId(1), FrameFlags.Encrypted, 0), 0));

        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void MaxHeaderSize_IsWorstCaseBound()
    {
        // 길이 u32 varint 5바이트 + ID u16 varint 3바이트.
        Assert.Equal(8, Encoder.MaxHeaderSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FramingOptions.AbsoluteMaxPayloadLength + 1)]
    public void Constructor_InvalidLimit_Throws(int limit)
    {
        Assert.Throws<InvalidOperationException>(() => new VarintFrameDecoder(limit));
        Assert.Throws<InvalidOperationException>(() => new VarintFrameEncoder(limit));
    }

    // ── 할당 검증 ──────────────────────────────────────────────

    [Fact]
    public void DecodeAndWrite_AllocateNothing()
    {
        // "프레임당 힙 할당 0"은 이 축의 합격 기준이다. 주장으로 두지 않고 측정한다.
        byte[] frame = BuildFrame(300, 256);
        ReadOnlySequence<byte> single = new(frame);
        ReadOnlySequence<byte> segmented = SequenceFactory.Segmented(frame, 3);
        ArrayBufferWriter<byte> writer = new(initialCapacity: 64);
        MessageEnvelope envelope = new(new MessageId(300), FrameFlags.None, 0);
        long sink = 0;

        for (int i = 0; i < 1_000; i++)
        {
            sink += (int)Decoder.Decode(single).Status;
            sink += (int)Decoder.Decode(segmented).Status;
            writer.ResetWrittenCount();
            Encoder.WriteHeader(writer, envelope, 256);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            sink += (int)Decoder.Decode(single).Status;
            sink += (int)Decoder.Decode(segmented).Status;
            writer.ResetWrittenCount();
            Encoder.WriteHeader(writer, envelope, 256);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _ = sink;
        Assert.Equal(0, allocated);
    }
}
