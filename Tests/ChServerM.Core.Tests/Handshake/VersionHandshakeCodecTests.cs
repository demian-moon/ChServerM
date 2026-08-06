using System;
using System.Buffers;
using ChServerM.Diagnostics;
using ChServerM.Handshake;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Handshake;

/// <summary>
/// 동결 와이어 레이아웃의 왕복·경계·형식 위반을 고정한다.
/// </summary>
/// <remarks>
/// 이 테스트가 깨진다면 동결 계약이 흔들린 것이다 — 코덱을 고치지 말고 무엇이
/// 레이아웃을 건드렸는지부터 찾는다. 구버전 클라이언트는 새 레이아웃을 읽지 못한다.
/// </remarks>
public sealed class VersionHandshakeCodecTests
{
    // ── 동결 수치 고정 ───────────────────────────────────────────

    [Fact]
    public void FrozenConstants_MatchTheirSources()
    {
        // 코덱은 와이어 수치를 상수로 동결한다. 원천(enum·프로퍼티)과의 일치는 여기서 지킨다.
        Assert.Equal((ushort)ErrorCode.ProtocolVersionMismatch, VersionHandshakeCodec.RejectReasonVersionMismatch);
        Assert.Equal((ushort)40005, FrameworkMessageIds.ClientHello.Value);
        Assert.Equal((ushort)40006, FrameworkMessageIds.ServerHello.Value);
        Assert.Equal((ushort)40004, FrameworkMessageIds.ConnectionRejected.Value);
        Assert.True(FrameworkMessageIds.ClientHello.IsFrameworkRange);
        Assert.True(FrameworkMessageIds.ServerHello.IsFrameworkRange);
    }

    [Fact]
    public void FrameSizes_AreFrozen()
    {
        Assert.Equal(16, VersionHandshakeCodec.HeaderSize);
        Assert.Equal(20, VersionHandshakeCodec.ClientHelloFrameSize);
        Assert.Equal(18, VersionHandshakeCodec.ServerHelloFrameSize);
        Assert.Equal(22, VersionHandshakeCodec.RejectionFrameSize);
        Assert.Equal((ushort)1, VersionHandshakeCodec.BootstrapHeaderVersion);
    }

    // ── 왕복 ─────────────────────────────────────────────────────

    [Fact]
    public void ClientHello_RoundTrips()
    {
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(2, 7));

        VersionHandshakeStatus status = VersionHandshakeCodec.TryReadClientHello(
            new ReadOnlySequence<byte>(frame), out ProtocolVersionRange parsed);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.Equal(new ProtocolVersionRange(2, 7), parsed);
    }

    [Fact]
    public void ServerHello_RoundTrips()
    {
        byte[] frame = new byte[VersionHandshakeCodec.ServerHelloFrameSize];
        VersionHandshakeCodec.WriteServerHello(frame, 3);

        VersionHandshakeStatus status = VersionHandshakeCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame), out VersionHandshakeResponse response);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.True(response.IsAccepted);
        Assert.Equal((ushort)3, response.SelectedVersion);
        Assert.Equal(VersionHandshakeCodec.ServerHelloFrameSize, response.FrameSize);
    }

    [Fact]
    public void Rejection_RoundTrips_WithServerRange()
    {
        byte[] frame = new byte[VersionHandshakeCodec.RejectionFrameSize];
        VersionHandshakeCodec.WriteRejection(frame, new ProtocolVersionRange(2, 4));

        VersionHandshakeStatus status = VersionHandshakeCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame), out VersionHandshakeResponse response);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.False(response.IsAccepted);
        Assert.Equal(VersionHandshakeCodec.RejectReasonVersionMismatch, response.RejectReason);
        Assert.Equal(new ProtocolVersionRange(2, 4), response.ServerSupported);
        Assert.Equal(VersionHandshakeCodec.RejectionFrameSize, response.FrameSize);
    }

    [Fact]
    public void ClientHello_RoundTrips_AcrossSegmentBoundary()
    {
        // 파이프는 프레임을 세그먼트 경계에서 자를 수 있다 — 단일 세그먼트 가정 금지.
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(1, 5));

        for (int split = 1; split < frame.Length; split++)
        {
            ReadOnlySequence<byte> segmented = Segmented(frame, split);

            VersionHandshakeStatus status =
                VersionHandshakeCodec.TryReadClientHello(segmented, out ProtocolVersionRange parsed);

            Assert.Equal(VersionHandshakeStatus.Success, status);
            Assert.Equal(new ProtocolVersionRange(1, 5), parsed);
        }
    }

    // ── 부분 수신 ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]  // 헤더 미만
    [InlineData(16)]  // 헤더만
    [InlineData(19)]  // 페이로드 1바이트 부족
    public void ClientHello_PartialBuffer_NeedsMoreData(int available)
    {
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(1, 1));

        VersionHandshakeStatus status = VersionHandshakeCodec.TryReadClientHello(
            new ReadOnlySequence<byte>(frame.AsMemory(0, available)), out _);

        Assert.Equal(VersionHandshakeStatus.NeedMoreData, status);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]  // ServerHello 1바이트 부족
    public void ServerResponse_PartialBuffer_NeedsMoreData(int available)
    {
        byte[] frame = new byte[VersionHandshakeCodec.ServerHelloFrameSize];
        VersionHandshakeCodec.WriteServerHello(frame, 1);

        VersionHandshakeStatus status = VersionHandshakeCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame.AsMemory(0, available)), out _);

        Assert.Equal(VersionHandshakeStatus.NeedMoreData, status);
    }

    // ── 형식 위반 — 엄격 파싱 ────────────────────────────────────

    [Theory]
    [InlineData(0, (byte)2)]   // 헤더 버전 ≠ 1
    [InlineData(2, (byte)0x25)] // 메시지 ID 변조 (40005 → 0x9C25 계열)
    [InlineData(4, (byte)5)]   // 페이로드 길이 ≠ 4
    [InlineData(8, (byte)1)]   // 플래그 ≠ 0
    [InlineData(10, (byte)1)]  // 예약 ≠ 0
    [InlineData(12, (byte)1)]  // 일련번호 ≠ 0
    public void ClientHello_HeaderFieldViolation_IsMalformed(int offset, byte corrupted)
    {
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(1, 1));
        frame[offset] = corrupted;

        VersionHandshakeStatus status = VersionHandshakeCodec.TryReadClientHello(
            new ReadOnlySequence<byte>(frame), out _);

        Assert.Equal(VersionHandshakeStatus.Malformed, status);
    }

    [Fact]
    public void ClientHello_ZeroMin_IsMalformed()
    {
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(1, 1));
        frame[16] = 0;
        frame[17] = 0; // Min = 0 (센티넬)

        Assert.Equal(
            VersionHandshakeStatus.Malformed,
            VersionHandshakeCodec.TryReadClientHello(new ReadOnlySequence<byte>(frame), out _));
    }

    [Fact]
    public void ClientHello_MinAboveMax_IsMalformed()
    {
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(1, 1));
        frame[16] = 9; // Min = 9 > Max = 1

        Assert.Equal(
            VersionHandshakeStatus.Malformed,
            VersionHandshakeCodec.TryReadClientHello(new ReadOnlySequence<byte>(frame), out _));
    }

    [Fact]
    public void ServerResponse_UnknownMessageId_IsMalformed()
    {
        // ClientHello 를 응답 자리에서 받으면(역방향 오조립) 형식 위반이다.
        byte[] frame = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(frame, new ProtocolVersionRange(1, 1));

        Assert.Equal(
            VersionHandshakeStatus.Malformed,
            VersionHandshakeCodec.TryReadServerResponse(new ReadOnlySequence<byte>(frame), out _));
    }

    [Fact]
    public void ServerHello_ZeroSelectedVersion_IsMalformed()
    {
        byte[] frame = new byte[VersionHandshakeCodec.ServerHelloFrameSize];
        VersionHandshakeCodec.WriteServerHello(frame, 1);
        frame[16] = 0; // SelectedVersion = 0 (센티넬)

        Assert.Equal(
            VersionHandshakeStatus.Malformed,
            VersionHandshakeCodec.TryReadServerResponse(new ReadOnlySequence<byte>(frame), out _));
    }

    [Fact]
    public void Rejection_InvalidServerRange_IsMalformed()
    {
        byte[] frame = new byte[VersionHandshakeCodec.RejectionFrameSize];
        VersionHandshakeCodec.WriteRejection(frame, new ProtocolVersionRange(1, 1));
        frame[18] = 7; // ServerMin = 7 > ServerMax = 1

        Assert.Equal(
            VersionHandshakeStatus.Malformed,
            VersionHandshakeCodec.TryReadServerResponse(new ReadOnlySequence<byte>(frame), out _));
    }

    // ── 쓰기 가드 ────────────────────────────────────────────────

    [Fact]
    public void Write_RejectsShortDestination()
    {
        Assert.Throws<ArgumentException>(
            () => VersionHandshakeCodec.WriteClientHello(
                new byte[VersionHandshakeCodec.ClientHelloFrameSize - 1], new ProtocolVersionRange(1, 1)));
        Assert.Throws<ArgumentException>(
            () => VersionHandshakeCodec.WriteServerHello(
                new byte[VersionHandshakeCodec.ServerHelloFrameSize - 1], 1));
        Assert.Throws<ArgumentException>(
            () => VersionHandshakeCodec.WriteRejection(
                new byte[VersionHandshakeCodec.RejectionFrameSize - 1], new ProtocolVersionRange(1, 1)));
    }

    [Fact]
    public void Write_RejectsSentinelInputs()
    {
        Assert.Throws<ArgumentException>(
            () => VersionHandshakeCodec.WriteClientHello(
                new byte[VersionHandshakeCodec.ClientHelloFrameSize], default));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VersionHandshakeCodec.WriteServerHello(
                new byte[VersionHandshakeCodec.ServerHelloFrameSize], 0));
        Assert.Throws<ArgumentException>(
            () => VersionHandshakeCodec.WriteRejection(
                new byte[VersionHandshakeCodec.RejectionFrameSize], default));
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────

    /// <summary>배열을 두 세그먼트로 잘라 <see cref="ReadOnlySequence{T}"/>를 만든다.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] data, int splitAt)
    {
        TestSegment first = new(data.AsMemory(0, splitAt), 0);
        TestSegment second = first.Append(data.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            TestSegment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
