using System;
using ChServerM.Framing;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Framing;

/// <summary>
/// ADR-0002 가 고정한 레이아웃을 값으로 못박는다. 오프셋이 조용히 바뀌면
/// 구버전 클라이언트가 새 헤더를 쓰레기로 읽는다.
/// </summary>
public sealed class FrameHeaderTests
{
    [Fact]
    public void Layout_IsSixteenBytes()
    {
        Assert.Equal(16, FrameHeader.Size);
    }

    [Fact]
    public void FieldOffsets_DoNotOverlap_AndFillTheHeader()
    {
        // 필드 크기: Version 2, MessageId 2, PayloadLength 4, Flags 2, Reserved 2, Sequence 4
        Assert.Equal(0, FrameHeader.VersionOffset);
        Assert.Equal(2, FrameHeader.MessageIdOffset);
        Assert.Equal(4, FrameHeader.PayloadLengthOffset);
        Assert.Equal(8, FrameHeader.FlagsOffset);
        Assert.Equal(10, FrameHeader.ReservedOffset);
        Assert.Equal(12, FrameHeader.SequenceOffset);
        Assert.Equal(FrameHeader.Size, FrameHeader.SequenceOffset + 4);
    }

    [Fact]
    public void Constructor_RoundTripsAllFields()
    {
        FrameHeader header = new(
            new MessageId(1234),
            payloadLength: 5678,
            flags: FrameFlags.Compressed | FrameFlags.Encrypted,
            sequence: 42,
            version: 3);

        Assert.Equal(new MessageId(1234), header.MessageId);
        Assert.Equal(5678, header.PayloadLength);
        Assert.Equal(FrameFlags.Compressed | FrameFlags.Encrypted, header.Flags);
        Assert.Equal(42u, header.Sequence);
        Assert.Equal(3, header.Version);
    }

    [Fact]
    public void Constructor_DefaultsToCurrentVersionAndNoFlags()
    {
        FrameHeader header = new(new MessageId(1), 0);

        Assert.Equal(FrameHeader.CurrentVersion, header.Version);
        Assert.Equal(FrameFlags.None, header.Flags);
        Assert.Equal(0u, header.Sequence);
    }

    [Fact]
    public void Constructor_NegativePayloadLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameHeader(new MessageId(1), -1));
    }

    [Fact]
    public void TotalLength_IncludesHeader()
    {
        Assert.Equal(FrameHeader.Size + 100, new FrameHeader(new MessageId(1), 100).TotalLength);
    }

    [Fact]
    public void ZeroPayload_IsValid()
    {
        // 페이로드 없는 프레임(하트비트)이 정상 경로다.
        FrameHeader header = new(FrameworkMessageIds.Heartbeat, 0);

        Assert.Equal(0, header.PayloadLength);
        Assert.Equal(FrameHeader.Size, header.TotalLength);
    }

    [Fact]
    public void Equality_ComparesAllFields()
    {
        FrameHeader a = new(new MessageId(1), 10, FrameFlags.Compressed, 5, 1);

        Assert.Equal(a, new FrameHeader(new MessageId(1), 10, FrameFlags.Compressed, 5, 1));
        Assert.NotEqual(a, new FrameHeader(new MessageId(2), 10, FrameFlags.Compressed, 5, 1));
        Assert.NotEqual(a, new FrameHeader(new MessageId(1), 11, FrameFlags.Compressed, 5, 1));
        Assert.NotEqual(a, new FrameHeader(new MessageId(1), 10, FrameFlags.None, 5, 1));
        Assert.NotEqual(a, new FrameHeader(new MessageId(1), 10, FrameFlags.Compressed, 6, 1));
        Assert.NotEqual(a, new FrameHeader(new MessageId(1), 10, FrameFlags.Compressed, 5, 2));
    }

    [Fact]
    public void Flags_CombineAndTestIndependently()
    {
        FrameFlags flags = FrameFlags.Fragmented | FrameFlags.EndOfMessage;

        Assert.True(flags.HasFlag(FrameFlags.Fragmented));
        Assert.True(flags.HasFlag(FrameFlags.EndOfMessage));
        Assert.False(flags.HasFlag(FrameFlags.Compressed));
    }

    [Fact]
    public void Flags_HaveDistinctBits()
    {
        // 비트가 겹치면 압축 해제와 복호화가 서로를 오인한다.
        FrameFlags[] all =
        [
            FrameFlags.Compressed,
            FrameFlags.Encrypted,
            FrameFlags.Fragmented,
            FrameFlags.EndOfMessage,
        ];

        int combined = 0;
        foreach (FrameFlags flag in all)
        {
            Assert.Equal(0, combined & (int)flag);
            combined |= (int)flag;
        }
    }
}
