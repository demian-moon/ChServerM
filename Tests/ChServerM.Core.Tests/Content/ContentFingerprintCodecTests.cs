using System;
using System.Buffers;
using ChServerM.Content;
using ChServerM.Diagnostics;
using ChServerM.Handshake;
using Xunit;

namespace ChServerM.Core.Tests.Content;

/// <summary>
/// 콘텐츠 지문 코덱의 <b>동결 와이어</b> 검증 (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// <b>동결을 지키는 것이 이 테스트의 일이다.</b> 프레임 크기와 사유 코드 수치를 여기서
/// 못 박아 두면, 레이아웃을 바꾸는 변경이 <b>이 파일의 diff</b>로 리뷰에 드러난다 —
/// 클라이언트와 서버가 다른 버전으로 배포될 수 있으므로 조용한 변경이 가장 위험하다.
/// </para>
/// <para>
/// <b>파싱이 엄격하다는 것도 계약이다.</b> 부트스트랩 서브셋은 최소·고정이므로 "관대한
/// 수신" 이 설 자리가 없다 — 관대함은 곧 동결 위반의 은폐다.
/// </para>
/// </remarks>
public sealed class ContentFingerprintCodecTests
{
    private static readonly ContentFingerprint Sample = new(0x0123456789ABCDEF, 0xFEDCBA9876543210);

    [Fact]
    public void FrameSizes_areFrozen()
    {
        Assert.Equal(16, ContentFingerprintCodec.HeaderSize);
        Assert.Equal(16, ContentFingerprintCodec.OfferPayloadSize);
        Assert.Equal(32, ContentFingerprintCodec.OfferFrameSize);
        Assert.Equal(0, ContentFingerprintCodec.AcceptedPayloadSize);
        Assert.Equal(16, ContentFingerprintCodec.AcceptedFrameSize);
    }

    [Fact]
    public void RejectReason_matchesErrorCode()
    {
        // 와이어 수치와 enum 이 어긋나면 클라이언트가 사유를 잘못 해석한다.
        // 수치는 코덱이 정본이고, 일치는 이 테스트가 지킨다(버전 사유와 같은 규약).
        Assert.Equal(
            (ushort)ErrorCode.ContentFingerprintMismatch,
            VersionHandshakeCodec.RejectReasonContentMismatch);
    }

    [Fact]
    public void Offer_roundTrips()
    {
        byte[] frame = new byte[ContentFingerprintCodec.OfferFrameSize];
        ContentFingerprintCodec.WriteOffer(frame, Sample);

        VersionHandshakeStatus status = ContentFingerprintCodec.TryReadOffer(
            new ReadOnlySequence<byte>(frame), out ContentFingerprint read);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.Equal(Sample, read);
    }

    [Fact]
    public void Offer_acrossSegmentedBuffer_roundTrips()
    {
        // 파이프는 프레임을 조각내 줄 수 있다. 단일 세그먼트만 다루면 실전에서 깨진다.
        byte[] frame = new byte[ContentFingerprintCodec.OfferFrameSize];
        ContentFingerprintCodec.WriteOffer(frame, Sample);

        VersionHandshakeStatus status = ContentFingerprintCodec.TryReadOffer(
            Segmented(frame, splitAt: 9), out ContentFingerprint read);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.Equal(Sample, read);
    }

    [Fact]
    public void Offer_shortBuffer_needsMoreData()
    {
        byte[] frame = new byte[ContentFingerprintCodec.OfferFrameSize];
        ContentFingerprintCodec.WriteOffer(frame, Sample);

        VersionHandshakeStatus status = ContentFingerprintCodec.TryReadOffer(
            new ReadOnlySequence<byte>(frame, 0, ContentFingerprintCodec.OfferFrameSize - 1), out _);

        Assert.Equal(VersionHandshakeStatus.NeedMoreData, status);
    }

    [Fact]
    public void Offer_zeroFingerprint_isMalformed()
    {
        // 0 을 유효 값으로 받으면 **초기화되지 않은 클라이언트가 우연히 통과**할 수 있다.
        byte[] frame = new byte[ContentFingerprintCodec.OfferFrameSize];
        ContentFingerprintCodec.WriteOffer(frame, Sample);
        frame.AsSpan(ContentFingerprintCodec.HeaderSize).Clear();

        VersionHandshakeStatus status =
            ContentFingerprintCodec.TryReadOffer(new ReadOnlySequence<byte>(frame), out _);

        Assert.Equal(VersionHandshakeStatus.Malformed, status);
    }

    [Fact]
    public void WriteOffer_rejectsUnsetFingerprint() =>
        Assert.Throws<ArgumentException>(
            () => ContentFingerprintCodec.WriteOffer(
                new byte[ContentFingerprintCodec.OfferFrameSize], ContentFingerprint.None));

    [Theory]
    [InlineData(0, 99)]    // 헤더 버전
    [InlineData(2, 99)]    // 메시지 ID
    [InlineData(4, 99)]    // 페이로드 길이
    [InlineData(8, 99)]    // 플래그
    [InlineData(10, 99)]   // 예약
    [InlineData(12, 99)]   // 일련번호
    public void Offer_anyAlteredHeaderField_isMalformed(int offset, byte value)
    {
        byte[] frame = new byte[ContentFingerprintCodec.OfferFrameSize];
        ContentFingerprintCodec.WriteOffer(frame, Sample);
        frame[offset] = value;

        VersionHandshakeStatus status =
            ContentFingerprintCodec.TryReadOffer(new ReadOnlySequence<byte>(frame), out _);

        Assert.Equal(VersionHandshakeStatus.Malformed, status);
    }

    [Fact]
    public void Accepted_roundTrips()
    {
        byte[] frame = new byte[ContentFingerprintCodec.AcceptedFrameSize];
        ContentFingerprintCodec.WriteAccepted(frame);

        VersionHandshakeStatus status = ContentFingerprintCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame), out bool accepted, out ushort reason, out int consumed);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.True(accepted);
        Assert.Equal(0, reason);
        Assert.Equal(ContentFingerprintCodec.AcceptedFrameSize, consumed);
    }

    [Fact]
    public void Rejection_reusesTheFrozenVersionRejectionLayout()
    {
        // 새 응답 형식을 만들지 않는 것이 요점이다 — 형식을 늘리면 그것을 모르는
        // 클라이언트에게는 해석 불가능한 바이트가 되어 거부 사유가 사라진다(R-3).
        byte[] frame = new byte[VersionHandshakeCodec.RejectionFrameSize];
        VersionHandshakeCodec.WriteRejection(
            frame, new ProtocolVersionRange(1, 3), VersionHandshakeCodec.RejectReasonContentMismatch);

        VersionHandshakeStatus status = ContentFingerprintCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame), out bool accepted, out ushort reason, out int consumed);

        Assert.Equal(VersionHandshakeStatus.Success, status);
        Assert.False(accepted);
        Assert.Equal(VersionHandshakeCodec.RejectReasonContentMismatch, reason);
        Assert.Equal(VersionHandshakeCodec.RejectionFrameSize, consumed);

        // 같은 바이트를 버전 협상 리더도 읽을 수 있어야 한다 — 레이아웃이 하나라는 증거.
        Assert.Equal(
            VersionHandshakeStatus.Success,
            VersionHandshakeCodec.TryReadServerResponse(
                new ReadOnlySequence<byte>(frame), out VersionHandshakeResponse response));
        Assert.False(response.IsAccepted);
        Assert.Equal(VersionHandshakeCodec.RejectReasonContentMismatch, response.RejectReason);
    }

    [Fact]
    public void WriteRejection_defaultOverload_keepsVersionMismatchReason()
    {
        byte[] frame = new byte[VersionHandshakeCodec.RejectionFrameSize];
        VersionHandshakeCodec.WriteRejection(frame, new ProtocolVersionRange(1, 1));

        ContentFingerprintCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame), out _, out ushort reason, out _);

        Assert.Equal(VersionHandshakeCodec.RejectReasonVersionMismatch, reason);
    }

    [Fact]
    public void WriteRejection_rejectsZeroReason() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VersionHandshakeCodec.WriteRejection(
                new byte[VersionHandshakeCodec.RejectionFrameSize], new ProtocolVersionRange(1, 1), 0));

    [Fact]
    public void ServerResponse_unknownMessageId_isMalformed()
    {
        byte[] frame = new byte[ContentFingerprintCodec.AcceptedFrameSize];
        ContentFingerprintCodec.WriteAccepted(frame);
        frame[2] = 1; // 메시지 ID 를 알 수 없는 값으로

        VersionHandshakeStatus status = ContentFingerprintCodec.TryReadServerResponse(
            new ReadOnlySequence<byte>(frame), out _, out _, out _);

        Assert.Equal(VersionHandshakeStatus.Malformed, status);
    }

    [Fact]
    public void Fingerprint_writeRead_roundTrips()
    {
        Span<byte> buffer = stackalloc byte[ContentFingerprint.ByteLength];
        Sample.WriteTo(buffer);

        Assert.Equal(Sample, ContentFingerprint.ReadFrom(buffer));
    }

    [Fact]
    public void Fingerprint_noneIsNotSet_andRendersReadably()
    {
        Assert.False(ContentFingerprint.None.IsSet);
        Assert.True(Sample.IsSet);
        Assert.Equal("(none)", ContentFingerprint.None.ToString());
        Assert.Equal("0123456789abcdeffedcba9876543210", Sample.ToString());
    }

    /// <summary>두 세그먼트로 쪼갠 시퀀스를 만든다.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] data, int splitAt)
    {
        Segment first = new(data.AsMemory(0, splitAt));
        Segment second = first.Append(data.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
