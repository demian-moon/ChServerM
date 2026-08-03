using System;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// 와이어 레이아웃을 <b>바이트 값으로</b> 못박는다. 오프셋이나 엔디안이 조용히 바뀌면
/// 구버전 상대가 새 헤더를 쓰레기로 읽는다 — 그 실패는 원인 추적이 매우 어렵다.
/// </summary>
public sealed class FrameHeaderCodecTests
{
    [Fact]
    public void Write_ProducesExactLittleEndianBytes()
    {
        // 이 테스트가 실패하면 와이어 호환성이 깨진 것이다. 값을 고치기 전에
        // ADR-0002 와 프로토콜 버전을 먼저 확인한다.
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        FrameHeader header = new(
            new MessageId(0x0201),
            payloadLength: 0x06050403,
            flags: (FrameFlags)0x0003,
            sequence: 0x0F0E0D0C,
            version: 0x0001);

        FrameHeaderCodec.Write(buffer, header);

        Assert.Equal<byte>([0x01, 0x00], buffer[0..2].ToArray());   // Version
        Assert.Equal<byte>([0x01, 0x02], buffer[2..4].ToArray());   // MessageId
        Assert.Equal<byte>([0x03, 0x04, 0x05, 0x06], buffer[4..8].ToArray());   // PayloadLength
        Assert.Equal<byte>([0x03, 0x00], buffer[8..10].ToArray());  // Flags
        Assert.Equal<byte>([0x00, 0x00], buffer[10..12].ToArray()); // Reserved
        Assert.Equal<byte>([0x0C, 0x0D, 0x0E, 0x0F], buffer[12..16].ToArray()); // Sequence
    }

    [Fact]
    public void Write_AlwaysZeroesReserved()
    {
        // 호출자가 남긴 쓰레기가 새어나가면 그 비트를 나중에 못 쓴다.
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        buffer.Fill(0xFF);

        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 0));

        Assert.Equal<byte>([0x00, 0x00], buffer[10..12].ToArray());
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        FrameHeader original = new(
            new MessageId(40001),
            payloadLength: 123456,
            flags: FrameFlags.Compressed | FrameFlags.Fragmented,
            sequence: uint.MaxValue,
            version: FrameHeader.CurrentVersion);

        FrameHeaderCodec.Write(buffer, original);

        Assert.Equal(original, FrameHeaderCodec.Read(buffer));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ushort.MaxValue)]
    public void RoundTrip_PreservesMessageIdEdges(ushort messageId)
    {
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        FrameHeader original = new(new MessageId(messageId), 0);

        FrameHeaderCodec.Write(buffer, original);

        Assert.Equal(messageId, FrameHeaderCodec.Read(buffer).MessageId.Value);
    }

    [Fact]
    public void RoundTrip_PreservesMaxPayloadLength()
    {
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        FrameHeader original = new(new MessageId(1), int.MaxValue);

        FrameHeaderCodec.Write(buffer, original);

        Assert.Equal(int.MaxValue, FrameHeaderCodec.Read(buffer).PayloadLength);
    }

    [Fact]
    public void Write_TooSmallDestination_Throws()
    {
        byte[] tooSmall = new byte[FrameHeader.Size - 1];

        Assert.Throws<ArgumentException>(
            () => FrameHeaderCodec.Write(tooSmall, new FrameHeader(new MessageId(1), 0)));
    }

    [Fact]
    public void TryRead_TooSmallSource_Throws()
    {
        byte[] tooSmall = new byte[FrameHeader.Size - 1];

        Assert.Throws<ArgumentException>(
            () => FrameHeaderCodec.TryRead(tooSmall, 1024, FrameHeader.CurrentVersion, out _));
    }

    [Fact]
    public void TryRead_ValidHeader_Decodes()
    {
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeader original = new(new MessageId(7), 64, FrameFlags.Encrypted, 9);
        FrameHeaderCodec.Write(buffer, original);

        FrameDecodeStatus status = FrameHeaderCodec.TryRead(
            buffer, maxPayloadLength: 1024, FrameHeader.CurrentVersion, out FrameHeader read);

        Assert.Equal(FrameDecodeStatus.Decoded, status);
        Assert.Equal(original, read);
    }

    [Fact]
    public void TryRead_WrongVersion_ReturnsVersionMismatch()
    {
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 0, version: 99));

        Assert.Equal(
            FrameDecodeStatus.VersionMismatch,
            FrameHeaderCodec.TryRead(buffer, 1024, FrameHeader.CurrentVersion, out _));
    }

    [Fact]
    public void TryRead_PayloadOverLimit_ReturnsTooLarge()
    {
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 1025));

        Assert.Equal(
            FrameDecodeStatus.TooLarge,
            FrameHeaderCodec.TryRead(buffer, maxPayloadLength: 1024, FrameHeader.CurrentVersion, out _));
    }

    [Fact]
    public void TryRead_PayloadExactlyAtLimit_IsAccepted()
    {
        // 경계값을 off-by-one 으로 막으면 정상 트래픽이 끊긴다.
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 1024));

        Assert.Equal(
            FrameDecodeStatus.Decoded,
            FrameHeaderCodec.TryRead(buffer, maxPayloadLength: 1024, FrameHeader.CurrentVersion, out _));
    }

    [Fact]
    public void TryRead_LengthAboveIntMaxValue_ReturnsTooLarge_NotNegative()
    {
        // 레거시는 이런 값을 그대로 int 로 캐스팅해 음수 길이를 만들 수 있었다.
        // uint 인 채로 비교하는지 검증한다.
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 0));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(FrameHeader.PayloadLengthOffset), 0xFFFF_FFFFu);

        FrameDecodeStatus status = FrameHeaderCodec.TryRead(
            buffer, maxPayloadLength: int.MaxValue, FrameHeader.CurrentVersion, out _);

        Assert.Equal(FrameDecodeStatus.TooLarge, status);
    }

    [Fact]
    public void TryRead_UnknownFlagBit_ReturnsInvalidFlags()
    {
        // 모르는 비트를 무시하면 압축된 페이로드를 원본으로 착각한다 — 조용한 오동작.
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 0));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(FrameHeader.FlagsOffset), 0x8000);

        Assert.Equal(
            FrameDecodeStatus.InvalidFlags,
            FrameHeaderCodec.TryRead(buffer, 1024, FrameHeader.CurrentVersion, out _));
    }

    [Fact]
    public void TryRead_NonZeroReserved_ReturnsMalformed()
    {
        // 예약 비트를 지금 거부해야 나중에 쓸 수 있다.
        byte[] buffer = new byte[FrameHeader.Size];
        FrameHeaderCodec.Write(buffer, new FrameHeader(new MessageId(1), 0));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(FrameHeader.ReservedOffset), 1);

        Assert.Equal(
            FrameDecodeStatus.Malformed,
            FrameHeaderCodec.TryRead(buffer, 1024, FrameHeader.CurrentVersion, out _));
    }

    [Fact]
    public void KnownFlags_CoversEveryDefinedFlag()
    {
        // 플래그를 추가하고 KnownFlags 갱신을 잊으면 그 플래그를 쓴 프레임이 전부 거부된다.
        foreach (FrameFlags flag in Enum.GetValues<FrameFlags>())
        {
            Assert.True(FrameHeaderCodec.AreFlagsKnown(flag), $"{flag}가 KnownFlags 에 없다.");
        }
    }

    [Fact]
    public void HeaderSize_MatchesCoreConstant()
    {
        Assert.Equal(FrameHeader.Size, FrameHeaderCodec.HeaderSize);
    }
}
