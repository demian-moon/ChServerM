using System;
using System.Buffers;
using System.Linq;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// 디코더의 유일한 책임은 바이트 스트림에서 프레임 경계를 복원하는 것이다.
/// 여기서 검증하는 것은 세 가지다 — 경계를 정확히 찾는가, 세그먼트에 걸쳐도 찾는가,
/// 실패를 조용히 넘기지 않는가.
/// </summary>
public sealed class FixedHeaderFrameDecoderTests
{
    private static readonly FixedHeaderFrameDecoder Decoder = new(maxPayloadLength: 1024);

    /// <summary>헤더 + 페이로드로 완전한 프레임 바이트를 만든다.</summary>
    private static byte[] BuildFrame(ushort messageId, int payloadLength, FrameFlags flags = FrameFlags.None, uint sequence = 0)
    {
        byte[] frame = new byte[FrameHeader.Size + payloadLength];
        FrameHeaderCodec.Write(frame, new FrameHeader(new MessageId(messageId), payloadLength, flags, sequence));

        // 페이로드를 식별 가능한 패턴으로 채운다 — 잘못된 슬라이스를 잡기 위해서다.
        for (int i = 0; i < payloadLength; i++)
        {
            frame[FrameHeader.Size + i] = (byte)(i & 0xFF);
        }

        return frame;
    }

    // ── 정상 경로 ──────────────────────────────────────────────

    [Fact]
    public void Decode_CompleteFrame_ReturnsDecoded()
    {
        byte[] frame = BuildFrame(42, 100);

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.True(result.IsDecoded);
        Assert.Equal(new MessageId(42), result.Header.MessageId);
        Assert.Equal(100, result.Header.PayloadLength);
        Assert.Equal(100, result.Payload.Length);
    }

    [Fact]
    public void Decode_PayloadContentIsCorrectlySliced()
    {
        // 오프셋이 하나만 밀려도 여기서 걸린다.
        byte[] frame = BuildFrame(1, 256);
        ReadOnlySequence<byte> buffer = new(frame);

        FrameDecodeResult result = Decoder.Decode(buffer);

        byte[] payload = result.Payload.ToArray();
        Assert.Equal(256, payload.Length);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), payload);
    }

    [Fact]
    public void Decode_ZeroLengthPayload_IsValid()
    {
        // 하트비트가 이 형태다. 페이로드 없는 프레임은 정상 경로다.
        byte[] frame = BuildFrame(FrameworkMessageIds.Heartbeat.Value, 0);

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame));

        Assert.True(result.IsDecoded);
        Assert.Equal(0, result.Payload.Length);
    }

    [Fact]
    public void Decode_ConsumedPointsPastTheFrame()
    {
        byte[] frame = BuildFrame(1, 50);
        ReadOnlySequence<byte> buffer = new(frame);

        FrameDecodeResult result = Decoder.Decode(buffer);

        Assert.Equal(0, buffer.Slice(result.Consumed).Length);
    }

    [Fact]
    public void Decode_MultipleFramesInOneBuffer_ReadsThemInOrder()
    {
        // 한 번의 소켓 읽기에 프레임이 여러 개 들어오는 것은 흔한 일이다.
        byte[] combined = [.. BuildFrame(1, 10), .. BuildFrame(2, 20), .. BuildFrame(3, 0)];
        ReadOnlySequence<byte> buffer = new(combined);

        ushort[] ids = new ushort[3];
        for (int i = 0; i < 3; i++)
        {
            FrameDecodeResult result = Decoder.Decode(buffer);
            Assert.True(result.IsDecoded);
            ids[i] = result.Header.MessageId.Value;
            buffer = buffer.Slice(result.Consumed);
        }

        Assert.Equal<ushort>([1, 2, 3], ids);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Decode_FrameFollowedByPartialFrame_ReadsFirstThenNeedsMore()
    {
        byte[] complete = BuildFrame(1, 10);
        byte[] partial = BuildFrame(2, 100)[..20];   // 헤더 + 페이로드 4바이트
        byte[] combined = [.. complete, .. partial];
        ReadOnlySequence<byte> buffer = new(combined);

        FrameDecodeResult first = Decoder.Decode(buffer);
        Assert.True(first.IsDecoded);

        FrameDecodeResult second = Decoder.Decode(buffer.Slice(first.Consumed));
        Assert.Equal(FrameDecodeStatus.NeedMoreData, second.Status);
    }

    // ── 부분 수신 ──────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FrameHeader.Size - 1)]
    public void Decode_IncompleteHeader_NeedsMoreData(int available)
    {
        byte[] frame = BuildFrame(1, 100);

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame, 0, available));

        Assert.Equal(FrameDecodeStatus.NeedMoreData, result.Status);
    }

    [Fact]
    public void Decode_HeaderCompleteButPayloadPartial_NeedsMoreData()
    {
        byte[] frame = BuildFrame(1, 100);

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(frame, 0, FrameHeader.Size + 99));

        Assert.Equal(FrameDecodeStatus.NeedMoreData, result.Status);
    }

    [Fact]
    public void Decode_NeedMoreData_ExaminesEntireBuffer()
    {
        // examined 가 버퍼 끝이 아니면 파이프가 더 읽지 않는다 → 교착.
        byte[] frame = BuildFrame(1, 100);
        ReadOnlySequence<byte> buffer = new(frame, 0, 20);

        FrameDecodeResult result = Decoder.Decode(buffer);

        Assert.Equal(0, buffer.Slice(result.Examined).Length);
        Assert.Equal(buffer.Length, buffer.Slice(result.Consumed).Length);
    }

    [Fact]
    public void Decode_ByteByByte_OnlySucceedsAtTheFinalByte()
    {
        // 1바이트씩 도착하는 극단적 상황. 프레임이 완성되기 전에 성공하면 안 되고,
        // 완성된 순간에는 반드시 성공해야 한다.
        byte[] frame = BuildFrame(7, 40);

        for (int available = 0; available < frame.Length; available++)
        {
            FrameDecodeResult partial = Decoder.Decode(new ReadOnlySequence<byte>(frame, 0, available));
            Assert.Equal(FrameDecodeStatus.NeedMoreData, partial.Status);
        }

        Assert.True(Decoder.Decode(new ReadOnlySequence<byte>(frame)).IsDecoded);
    }

    // ── 세그먼트 경계 ──────────────────────────────────────────

    [Theory]
    [InlineData(1)]    // 바이트마다 세그먼트 — 헤더가 16개 세그먼트에 걸친다
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(15)]   // 헤더가 딱 1바이트 모자란 경계
    [InlineData(16)]   // 헤더가 정확히 한 세그먼트
    [InlineData(17)]
    [InlineData(64)]
    public void Decode_AcrossSegmentBoundaries_StillFindsTheFrame(int segmentSize)
    {
        byte[] frame = BuildFrame(99, 200, FrameFlags.Compressed, sequence: 12345);

        FrameDecodeResult result = Decoder.Decode(SequenceFactory.Segmented(frame, segmentSize));

        Assert.True(result.IsDecoded, $"세그먼트 크기 {segmentSize}에서 실패했다.");
        Assert.Equal(new MessageId(99), result.Header.MessageId);
        Assert.Equal(200, result.Header.PayloadLength);
        Assert.Equal(FrameFlags.Compressed, result.Header.Flags);
        Assert.Equal(12345u, result.Header.Sequence);
        Assert.Equal(200, result.Payload.Length);
    }

    [Fact]
    public void Decode_HeaderSplitAtEveryOffset_AlwaysReadsTheSameHeader()
    {
        // 헤더 16바이트를 (n, 16-n) 으로 쪼개 모든 분할 지점을 시험한다.
        byte[] frame = BuildFrame(1234, 32, FrameFlags.Encrypted, sequence: 777);
        FrameHeader expected = FrameHeaderCodec.Read(frame);

        for (int split = 1; split < FrameHeader.Size; split++)
        {
            ReadOnlySequence<byte> buffer =
                SequenceFactory.Segmented(frame, [split, frame.Length - split]);

            FrameDecodeResult result = Decoder.Decode(buffer);

            Assert.True(result.IsDecoded, $"분할 지점 {split}에서 실패했다.");
            Assert.Equal(expected, result.Header);
        }
    }

    [Fact]
    public void Decode_SegmentedPayload_SlicesCorrectContent()
    {
        byte[] frame = BuildFrame(1, 300);

        FrameDecodeResult result = Decoder.Decode(SequenceFactory.Segmented(frame, 7));

        byte[] payload = result.Payload.ToArray();
        Assert.Equal(Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), payload);
    }

    [Fact]
    public void Decode_SegmentedMultipleFrames_ReadsThemInOrder()
    {
        byte[] combined = [.. BuildFrame(10, 33), .. BuildFrame(20, 5), .. BuildFrame(30, 128)];
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(combined, 11);

        ushort[] ids = new ushort[3];
        for (int i = 0; i < 3; i++)
        {
            FrameDecodeResult result = Decoder.Decode(buffer);
            Assert.True(result.IsDecoded);
            ids[i] = result.Header.MessageId.Value;
            buffer = buffer.Slice(result.Consumed);
        }

        Assert.Equal<ushort>([10, 20, 30], ids);
        Assert.Equal(0, buffer.Length);
    }

    // ── 실패 경로 ──────────────────────────────────────────────

    [Fact]
    public void Decode_PayloadOverLimit_ReturnsTooLarge_BeforeWaitingForData()
    {
        // 핵심: 1MB 라고 선언한 프레임의 데이터를 기다리지 않고 즉시 거부해야 한다.
        // 기다리면 그것이 곧 메모리 고갈 경로다.
        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(1), 1025));

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(header));

        Assert.Equal(FrameDecodeStatus.TooLarge, result.Status);
        Assert.True(result.IsFatal);
        Assert.Equal(ErrorCode.FrameTooLarge, result.ToErrorCode());
    }

    [Fact]
    public void Decode_HugeLengthField_ReturnsTooLarge()
    {
        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(1), 0));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(FrameHeader.PayloadLengthOffset), 0xFFFF_FFFFu);

        Assert.Equal(FrameDecodeStatus.TooLarge, Decoder.Decode(new ReadOnlySequence<byte>(header)).Status);
    }

    [Fact]
    public void Decode_WrongVersion_ReturnsVersionMismatch()
    {
        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(1), 0, version: 42));

        FrameDecodeResult result = Decoder.Decode(new ReadOnlySequence<byte>(header));

        Assert.Equal(FrameDecodeStatus.VersionMismatch, result.Status);
        Assert.Equal(ErrorCode.ProtocolVersionMismatch, result.ToErrorCode());
    }

    [Fact]
    public void Decode_FailureIsDetectedBeforePayloadArrives()
    {
        // 헤더만으로 판정 가능한 실패는 페이로드를 기다리지 않는다.
        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(1), 500, version: 42));

        Assert.True(Decoder.Decode(new ReadOnlySequence<byte>(header)).IsFatal);
    }

    [Fact]
    public void Decode_FatalFailure_DoesNotAdvanceConsumed()
    {
        // 실패 시 커넥션을 닫으므로 소비 위치는 의미가 없지만,
        // 실수로 재동기화를 시도하지 않도록 시작 위치를 유지한다.
        byte[] header = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(1), 0, version: 42));
        ReadOnlySequence<byte> buffer = new(header);

        FrameDecodeResult result = Decoder.Decode(buffer);

        Assert.Equal(buffer.Length, buffer.Slice(result.Consumed).Length);
    }

    // ── 설정 ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FixedHeaderFrameDecoder(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FramingOptions.AbsoluteMaxPayloadLength + 1)]
    public void Constructor_InvalidMaxPayloadLength_Throws(int maxPayloadLength)
    {
        Assert.Throws<InvalidOperationException>(() => new FixedHeaderFrameDecoder(maxPayloadLength));
    }

    [Fact]
    public void Constructor_ZeroVersion_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new FixedHeaderFrameDecoder(1024, acceptedVersion: 0));
    }

    [Fact]
    public void Constructor_CopiesOptions_SoLaterMutationHasNoEffect()
    {
        // 동작 중에 프레임 상한이 바뀌면 진행 중인 디코딩이 일관성을 잃는다.
        FramingOptions options = new() { MaxPayloadLength = 100 };
        FixedHeaderFrameDecoder decoder = new(options);

        options.MaxPayloadLength = 999999;

        Assert.Equal(100, decoder.MaxPayloadLength);
    }
}
