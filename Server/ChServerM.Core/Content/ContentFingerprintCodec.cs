using System;
using System.Buffers;
using System.Buffers.Binary;
using ChServerM.Handshake;

namespace ChServerM.Content;

/// <summary>
/// 콘텐츠 지문 교환 프레임의 <b>영구 동결</b> 와이어 코덱.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 버전 핸드셰이크에 필드를 더할 수 없다.</b>
/// <see cref="VersionHandshakeCodec"/> 의 <c>ClientHello</c> 페이로드는 영구 동결이다(R-2).
/// 거기에 지문 16바이트를 끼워 넣으면 구버전 클라이언트가 새 핸드셰이크를 못 읽는다 —
/// 동결의 존재 이유가 정확히 그것이다. 그래서 <b>새 메시지 ID 를 예약</b>한다(같은 문서가
/// "바꿔야 하면 새 메시지 ID 를 예약한다" 고 적어 둔 길이다).
/// </para>
///
/// <para>
/// <b>⚠ 왕복은 늘지 않는다.</b> 클라이언트는 <c>ClientHello</c> 와 <c>ContentOffer</c> 를
/// <b>한 번에 플러시</b>하고, 서버는 <c>ServerHello</c> 를 보낸 뒤 이미 버퍼에 와 있는
/// 지문을 읽는다. 바이트만 늘고 왕복은 그대로다 — 협상 데코레이터가 <b>소비한 바이트까지만</b>
/// <c>AdvanceTo</c> 하기 때문에 뒤따르는 지문 프레임이 그대로 다음 단계로 넘어간다.
/// </para>
///
/// <para>
/// <b>⚠ 이 게이트는 양쪽 모두의 스위치다.</b> 서버만 켜면 지문을 기다리다 제한 시간에
/// 걸리고, 클라이언트만 켜면 지문 프레임이 프레이밍 단계로 흘러 들어가 형식 오류가 된다.
/// 배포 단위로 함께 켜고 끄며, 섞어야 한다면 프로토콜 버전을 올려 구분한다.
/// </para>
///
/// <para>
/// <b>불일치는 새 응답 형식을 만들지 않는다.</b> 기존
/// <see cref="ChServerM.Identity.FrameworkMessageIds.ConnectionRejected"/> 프레임에
/// <see cref="VersionHandshakeCodec.RejectReasonContentMismatch"/> 사유를 실어 보낸다 —
/// 클라이언트가 이미 읽을 줄 아는 형식이라 <b>거부 사유를 잃지 않는다</b>(R-3).
/// <b>서버의 지문을 알려 주지 않는 것은 의도다</b>: 실행 가능한 조치는 "데이터를 갱신하라"
/// 하나뿐이고, 불투명한 128비트 값은 그 조치를 앞당기지 못한다.
/// </para>
///
/// <para><b>스레드 규약.</b> 상태 없는 정적 클래스.</para>
/// <para><b>할당.</b> 힙 할당 0. 프레임 상한이 32바이트라 스택 복사로 충분하다.</para>
/// </remarks>
public static class ContentFingerprintCodec
{
    /// <summary>핸드셰이크 프레임 헤더 크기. <see cref="VersionHandshakeCodec.HeaderSize"/> 와 같다.</summary>
    public const int HeaderSize = VersionHandshakeCodec.HeaderSize;

    private const int MessageIdOffset = 2;

    // 동결 수치. FrameworkMessageIds 를 참조하지 않고 상수로 박는 이유는
    // VersionHandshakeCodec 과 같다 — 이 파일이 와이어의 정본이고, 상수라야 switch 에 쓴다.
    private const ushort ContentOfferId = 40010;      // = FrameworkMessageIds.ContentOffer
    private const ushort ContentAcceptedId = 40011;   // = FrameworkMessageIds.ContentAccepted
    private const ushort ConnectionRejectedId = 40004; // = FrameworkMessageIds.ConnectionRejected

    /// <summary><c>ContentOffer</c> 페이로드: 지문 16바이트.</summary>
    public const int OfferPayloadSize = ContentFingerprint.ByteLength;

    /// <summary><c>ContentOffer</c> 프레임 전체 크기.</summary>
    public const int OfferFrameSize = HeaderSize + OfferPayloadSize;

    /// <summary><c>ContentAccepted</c> 페이로드는 비어 있다.</summary>
    /// <remarks>
    /// 수락에 실을 정보가 없다. <b>그래도 프레임을 보낸다</b> — 침묵으로 수락을 표현하면
    /// 클라이언트는 "수락됐다" 와 "아직 안 왔다" 를 구분할 수 없어 제한 시간까지 기다려야 한다.
    /// </remarks>
    public const int AcceptedPayloadSize = 0;

    /// <summary><c>ContentAccepted</c> 프레임 전체 크기.</summary>
    public const int AcceptedFrameSize = HeaderSize + AcceptedPayloadSize;

    // ── 쓰기 ─────────────────────────────────────────────────────

    /// <summary><c>ContentOffer</c> 프레임을 쓴다 (클라이언트 측).</summary>
    /// <param name="destination">쓸 대상. <see cref="OfferFrameSize"/> 이상.</param>
    /// <param name="fingerprint">클라이언트가 들고 있는 콘텐츠의 지문.</param>
    /// <exception cref="ArgumentException">대상이 짧거나 지문이 설정되지 않았다.</exception>
    public static void WriteOffer(Span<byte> destination, ContentFingerprint fingerprint)
    {
        EnsureLength(destination, OfferFrameSize);

        if (!fingerprint.IsSet)
        {
            throw new ArgumentException(
                "설정되지 않은 지문이다. 0 은 '설정되지 않음' 센티넬이라 와이어에 실을 수 없다.",
                nameof(fingerprint));
        }

        WriteHeader(destination, ContentOfferId, OfferPayloadSize);
        fingerprint.WriteTo(destination[HeaderSize..]);
    }

    /// <summary><c>ContentAccepted</c> 프레임을 쓴다 (서버 측).</summary>
    /// <param name="destination">쓸 대상. <see cref="AcceptedFrameSize"/> 이상.</param>
    /// <exception cref="ArgumentException">대상이 짧다.</exception>
    public static void WriteAccepted(Span<byte> destination)
    {
        EnsureLength(destination, AcceptedFrameSize);
        WriteHeader(destination, ContentAcceptedId, AcceptedPayloadSize);
    }

    // ── 읽기 ─────────────────────────────────────────────────────

    /// <summary>수신 버퍼에서 <c>ContentOffer</c> 를 읽어낸다 (서버 측).</summary>
    /// <param name="buffer">파이프에서 읽은 수신 버퍼.</param>
    /// <param name="fingerprint">성공하면 클라이언트가 제시한 지문.</param>
    /// <returns>
    /// <see cref="VersionHandshakeStatus.Success"/> 면 호출자는 정확히
    /// <see cref="OfferFrameSize"/> 바이트를 소비한다. 그 뒤는 프레이밍의 몫이다.
    /// </returns>
    /// <remarks>
    /// 지문이 <see cref="ContentFingerprint.None"/> 이면 <see cref="VersionHandshakeStatus.Malformed"/>
    /// 다 — 0 을 유효 값으로 받으면 초기화되지 않은 클라이언트가 <b>우연히 통과</b>할 수 있다.
    /// </remarks>
    public static VersionHandshakeStatus TryReadOffer(
        in ReadOnlySequence<byte> buffer, out ContentFingerprint fingerprint)
    {
        fingerprint = default;

        Span<byte> frame = stackalloc byte[OfferFrameSize];
        VersionHandshakeStatus status = TryCopyValidatedFrame(buffer, ContentOfferId, OfferPayloadSize, frame);
        if (status != VersionHandshakeStatus.Success)
        {
            return status;
        }

        ContentFingerprint offered = ContentFingerprint.ReadFrom(frame[HeaderSize..]);
        if (!offered.IsSet)
        {
            return VersionHandshakeStatus.Malformed;
        }

        fingerprint = offered;
        return VersionHandshakeStatus.Success;
    }

    /// <summary>수신 버퍼에서 서버의 지문 응답(수락 또는 거부)을 읽어낸다 (클라이언트 측).</summary>
    /// <param name="buffer">파이프에서 읽은 수신 버퍼.</param>
    /// <param name="accepted">수락이면 <see langword="true"/>.</param>
    /// <param name="rejectReason">거부면 사유 코드. 수락이면 0.</param>
    /// <param name="consumed">성공 시 소비해야 할 바이트 수.</param>
    /// <returns>판정 상태.</returns>
    /// <remarks>
    /// 거부는 <see cref="VersionHandshakeCodec"/> 의 동결 거부 레이아웃 그대로다 —
    /// 사유 코드만 다르다. 형식을 하나 더 만들지 않는 것이 요점이다.
    /// </remarks>
    public static VersionHandshakeStatus TryReadServerResponse(
        in ReadOnlySequence<byte> buffer, out bool accepted, out ushort rejectReason, out int consumed)
    {
        accepted = false;
        rejectReason = 0;
        consumed = 0;

        if (buffer.Length < HeaderSize)
        {
            return VersionHandshakeStatus.NeedMoreData;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        ushort messageId = BinaryPrimitives.ReadUInt16LittleEndian(header[MessageIdOffset..]);
        switch (messageId)
        {
            case ContentAcceptedId:
            {
                Span<byte> frame = stackalloc byte[AcceptedFrameSize];
                VersionHandshakeStatus status =
                    TryCopyValidatedFrame(buffer, ContentAcceptedId, AcceptedPayloadSize, frame);
                if (status != VersionHandshakeStatus.Success)
                {
                    return status;
                }

                accepted = true;
                consumed = AcceptedFrameSize;
                return VersionHandshakeStatus.Success;
            }

            case ConnectionRejectedId:
            {
                Span<byte> frame = stackalloc byte[VersionHandshakeCodec.RejectionFrameSize];
                VersionHandshakeStatus status = TryCopyValidatedFrame(
                    buffer, ConnectionRejectedId, VersionHandshakeCodec.RejectionPayloadSize, frame);
                if (status != VersionHandshakeStatus.Success)
                {
                    return status;
                }

                rejectReason = BinaryPrimitives.ReadUInt16LittleEndian(frame[HeaderSize..]);
                consumed = VersionHandshakeCodec.RejectionFrameSize;
                return VersionHandshakeStatus.Success;
            }

            default:
                return VersionHandshakeStatus.Malformed;
        }
    }

    // ── 내부 ─────────────────────────────────────────────────────

    /// <summary>동결 헤더를 쓴다. 플래그·예약·일련번호는 항상 0.</summary>
    private static void WriteHeader(Span<byte> destination, ushort messageId, int payloadLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, VersionHandshakeCodec.BootstrapHeaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], messageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], 0);
    }

    /// <summary>
    /// 기대하는 메시지의 프레임 전체를 검증하며 복사한다. 헤더의 고정 필드가 하나라도 다르면
    /// <see cref="VersionHandshakeStatus.Malformed"/> — 부트스트랩에 관대한 수신은 없다.
    /// </summary>
    private static VersionHandshakeStatus TryCopyValidatedFrame(
        in ReadOnlySequence<byte> buffer, ushort expectedMessageId, int expectedPayloadLength, Span<byte> frame)
    {
        if (buffer.Length < HeaderSize)
        {
            return VersionHandshakeStatus.NeedMoreData;
        }

        Span<byte> header = frame[..HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != VersionHandshakeCodec.BootstrapHeaderVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) != expectedMessageId
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != (uint)expectedPayloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != 0)
        {
            return VersionHandshakeStatus.Malformed;
        }

        int frameSize = HeaderSize + expectedPayloadLength;
        if (buffer.Length < frameSize)
        {
            return VersionHandshakeStatus.NeedMoreData;
        }

        if (expectedPayloadLength > 0)
        {
            buffer.Slice(HeaderSize, expectedPayloadLength).CopyTo(frame[HeaderSize..]);
        }

        return VersionHandshakeStatus.Success;
    }

    private static void EnsureLength(Span<byte> destination, int required)
    {
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"지문 프레임을 쓰려면 {required}바이트가 필요하다. 받은 크기: {destination.Length}",
                nameof(destination));
        }
    }
}
