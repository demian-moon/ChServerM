using System;
using System.Buffers;
using System.Buffers.Binary;

namespace ChServerM.Handshake;

/// <summary>
/// 버전 협상 핸드셰이크 프레임의 <b>영구 동결</b> 와이어 코덱 (ADR-0017 결정 3, THREAT-MODEL R-2).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 협상 이전에는 합의된 버전이 없다. 그래서 첫 왕복(<c>ClientHello</c> →
/// <c>ServerHello</c> / <c>ConnectionRejected</c>)은 <b>모든 버전이 파싱할 수 있는 최저 공통
/// 형식</b>이어야 하고, 그 형식은 사실상 영구 동결이다(R-2). 이 코덱이 그 동결 형식의
/// 유일한 정본이다.
/// </para>
/// <para>
/// <b>왜 Core 에 있는가.</b> 부트스트랩을 교체 가능한 축 위에 얹으면 축 교체가 와이어
/// 호환성을 깨는 모순이 생긴다(R-2). 프레이밍 축(<c>ChServerM.Framing</c> 어댑터)은
/// 교체 대상이고 — varint 와이어에는 버전 필드조차 없다 — 직렬화 축도 마찬가지다.
/// 핸드셰이크는 그래서 어느 축에도 얹지 않고, 호스팅이 커넥션 파이프에서 이 코덱으로
/// <b>직접</b> 읽고 쓴다. 메시지 ID 대역(<c>MessageId</c>)과 같은 "프레임워크 영구 계약"이라
/// Core 소속이다.
/// </para>
/// <para>
/// <b>헤더 레이아웃을 의도적으로 중복한다.</b> 아래 오프셋 상수는
/// <c>ChServerM.Framing.FrameHeaderCodec</c> 의 고정 16바이트 헤더 v1 과 같은 값이다.
/// 그쪽을 참조하면 Core → 어댑터 역방향 의존이 생기므로 중복이 불가피한데,
/// <b>이 레이아웃은 영구 동결이므로 중복이 어긋날 방법이 없다</b> — 동결을 어기는 순간
/// 구버전 클라이언트가 새 핸드셰이크를 못 읽는다. 두 정의의 일치는 통합 테스트가
/// 교차 검증한다(<c>VersionNegotiationTests</c>).
/// </para>
/// <para>
/// <b>파싱은 엄격하다.</b> 핸드셰이크 프레임의 모든 필드(버전 = 1, 플래그 = 0, 예약 = 0,
/// 일련번호 = 0, 페이로드 길이 = 정확한 값)를 검증하고, 하나라도 다르면
/// <see cref="VersionHandshakeStatus.Malformed"/> 다. 부트스트랩 서브셋은 최소·고정이므로
/// "관대한 수신"이 설 자리가 없다 — 관대함은 곧 동결 위반의 은폐다.
/// </para>
/// <para><b>스레드 규약.</b> 상태 없는 정적 클래스. 어디서 불러도 안전하다.</para>
/// <para><b>할당.</b> 힙 할당 0. 프레임 상한이 22바이트라 스택 복사로 충분하다.</para>
/// </remarks>
public static class VersionHandshakeCodec
{
    // ── 동결 상수 — 고정 16바이트 헤더 v1 (FrameHeaderCodec 과 의도적 중복, 위 remarks) ──

    /// <summary>핸드셰이크 프레임 헤더 크기(바이트). 영구 동결.</summary>
    public const int HeaderSize = 16;

    /// <summary>핸드셰이크 프레임이 쓰는 헤더 버전. 영구 동결 — 협상 결과와 무관하게 항상 1.</summary>
    public const ushort BootstrapHeaderVersion = 1;

    private const int VersionOffset = 0;
    private const int MessageIdOffset = 2;
    private const int PayloadLengthOffset = 4;
    private const int FlagsOffset = 8;
    private const int ReservedOffset = 10;
    private const int SequenceOffset = 12;

    // 프레임워크 메시지 ID 의 동결 수치. FrameworkMessageIds 프로퍼티를 쓰지 않고 상수로
    // 박는 이유: 이 파일 하나가 와이어의 정본이어야 하고, 상수라야 switch 에 쓸 수 있다.
    private const ushort ClientHelloId = 40005;   // = FrameworkMessageIds.ClientHello
    private const ushort ServerHelloId = 40006;   // = FrameworkMessageIds.ServerHello
    private const ushort ConnectionRejectedId = 40004; // = FrameworkMessageIds.ConnectionRejected

    /// <summary><c>ClientHello</c> 페이로드: <c>Min(u16) + Max(u16)</c>.</summary>
    public const int ClientHelloPayloadSize = 4;

    /// <summary><c>ClientHello</c> 프레임 전체 크기.</summary>
    public const int ClientHelloFrameSize = HeaderSize + ClientHelloPayloadSize;

    /// <summary><c>ServerHello</c> 페이로드: <c>SelectedVersion(u16)</c>.</summary>
    public const int ServerHelloPayloadSize = 2;

    /// <summary><c>ServerHello</c> 프레임 전체 크기.</summary>
    public const int ServerHelloFrameSize = HeaderSize + ServerHelloPayloadSize;

    /// <summary>거부 페이로드: <c>Reason(u16) + ServerMin(u16) + ServerMax(u16)</c>.</summary>
    public const int RejectionPayloadSize = 6;

    /// <summary>거부 프레임 전체 크기.</summary>
    public const int RejectionFrameSize = HeaderSize + RejectionPayloadSize;

    /// <summary>
    /// 거부 사유 "지원 버전 교집합 없음"의 동결 수치.
    /// </summary>
    /// <remarks>
    /// <c>ErrorCode.ProtocolVersionMismatch</c>(2002)의 수치를 동결한 것이다.
    /// enum 을 직접 캐스팅하지 않는 이유: enum 값이 리팩터링되면 와이어가 조용히 바뀐다.
    /// 와이어 수치는 이 상수가 정본이고, enum 과의 일치는 테스트가 지킨다.
    /// </remarks>
    public const ushort RejectReasonVersionMismatch = 2002;

    // ── 쓰기 ─────────────────────────────────────────────────────

    /// <summary><c>ClientHello</c> 프레임(<see cref="ClientHelloFrameSize"/> 바이트)을 쓴다.</summary>
    /// <param name="destination">쓸 대상. <see cref="ClientHelloFrameSize"/> 이상이어야 한다.</param>
    /// <param name="supported">클라이언트가 지원하는 버전 구간.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/>이 짧거나
    /// <paramref name="supported"/>가 설정되지 않은 센티넬일 때.</exception>
    public static void WriteClientHello(Span<byte> destination, ProtocolVersionRange supported)
    {
        EnsureLength(destination, ClientHelloFrameSize);
        EnsureRangeSet(supported, nameof(supported));

        WriteHeader(destination, ClientHelloId, ClientHelloPayloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[HeaderSize..], supported.Min);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(HeaderSize + 2)..], supported.Max);
    }

    /// <summary><c>ServerHello</c> 프레임(<see cref="ServerHelloFrameSize"/> 바이트)을 쓴다.</summary>
    /// <param name="destination">쓸 대상. <see cref="ServerHelloFrameSize"/> 이상이어야 한다.</param>
    /// <param name="selectedVersion">확정한 버전. 0(센티넬)일 수 없다.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/>이 짧을 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="selectedVersion"/>이 0일 때.</exception>
    public static void WriteServerHello(Span<byte> destination, ushort selectedVersion)
    {
        EnsureLength(destination, ServerHelloFrameSize);
        ArgumentOutOfRangeException.ThrowIfZero(selectedVersion);

        WriteHeader(destination, ServerHelloId, ServerHelloPayloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[HeaderSize..], selectedVersion);
    }

    /// <summary>버전 거부 프레임(<see cref="RejectionFrameSize"/> 바이트)을 쓴다.</summary>
    /// <param name="destination">쓸 대상. <see cref="RejectionFrameSize"/> 이상이어야 한다.</param>
    /// <param name="serverSupported">서버가 지원하는 버전 구간 — 사유에 포함시킨다(R-3).</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/>이 짧거나
    /// <paramref name="serverSupported"/>가 설정되지 않은 센티넬일 때.</exception>
    /// <remarks>
    /// 기존 <c>ConnectionRejected</c>(40004) 경로를 재사용한다 — 그냥 끊으면 클라이언트는
    /// "서버가 내 버전을 거부했다"와 "네트워크가 끊겼다"를 구분할 수 없다(조용한 유실 금지).
    /// </remarks>
    public static void WriteRejection(Span<byte> destination, ProtocolVersionRange serverSupported)
    {
        EnsureLength(destination, RejectionFrameSize);
        EnsureRangeSet(serverSupported, nameof(serverSupported));

        WriteHeader(destination, ConnectionRejectedId, RejectionPayloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[HeaderSize..], RejectReasonVersionMismatch);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(HeaderSize + 2)..], serverSupported.Min);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(HeaderSize + 4)..], serverSupported.Max);
    }

    // ── 읽기 ─────────────────────────────────────────────────────

    /// <summary>수신 버퍼에서 <c>ClientHello</c> 를 읽어낸다 (서버 측).</summary>
    /// <param name="buffer">파이프에서 읽은 수신 버퍼.</param>
    /// <param name="clientSupported">성공하면 클라이언트가 제시한 구간.</param>
    /// <returns>
    /// <see cref="VersionHandshakeStatus.Success"/> 면 호출자는 정확히
    /// <see cref="ClientHelloFrameSize"/> 바이트를 소비한다. 그 뒤의 바이트는
    /// 협상 후 프레이밍의 몫이므로 건드리지 않는다.
    /// </returns>
    public static VersionHandshakeStatus TryReadClientHello(
        in ReadOnlySequence<byte> buffer,
        out ProtocolVersionRange clientSupported)
    {
        clientSupported = default;

        Span<byte> frame = stackalloc byte[ClientHelloFrameSize];
        VersionHandshakeStatus status =
            TryCopyValidatedFrame(buffer, ClientHelloId, ClientHelloPayloadSize, frame);
        if (status != VersionHandshakeStatus.Success)
        {
            return status;
        }

        ushort min = BinaryPrimitives.ReadUInt16LittleEndian(frame[HeaderSize..]);
        ushort max = BinaryPrimitives.ReadUInt16LittleEndian(frame[(HeaderSize + 2)..]);
        if (min == 0 || min > max)
        {
            return VersionHandshakeStatus.Malformed;
        }

        clientSupported = new ProtocolVersionRange(min, max);
        return VersionHandshakeStatus.Success;
    }

    /// <summary>수신 버퍼에서 서버 응답(확정 또는 거부)을 읽어낸다 (클라이언트 측).</summary>
    /// <param name="buffer">파이프에서 읽은 수신 버퍼.</param>
    /// <param name="response">성공하면 판별된 응답.</param>
    /// <returns>
    /// <see cref="VersionHandshakeStatus.Success"/> 면 호출자는 정확히
    /// <see cref="VersionHandshakeResponse.FrameSize"/> 바이트를 소비한다.
    /// </returns>
    /// <remarks>
    /// 주의: 서버는 협상 밖에서도 <c>ConnectionRejected</c>(40004)를 보낼 수 있다
    /// (동시 접속 상한 — 그 통지는 조립하는 쪽의 인코더 형식이다). 그 바이트가 이 동결
    /// 레이아웃(페이로드 6바이트)과 다르면 <see cref="VersionHandshakeStatus.Malformed"/> 로
    /// 판정된다 — 어느 쪽이든 연결 수립은 실패이고, 사유 구분만 잃는다.
    /// </remarks>
    public static VersionHandshakeStatus TryReadServerResponse(
        in ReadOnlySequence<byte> buffer,
        out VersionHandshakeResponse response)
    {
        response = default;

        if (buffer.Length < HeaderSize)
        {
            return VersionHandshakeStatus.NeedMoreData;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        // 헤더의 메시지 ID 로 갈래를 정한 뒤, 갈래별 정확한 크기로 재검증한다.
        ushort messageId = BinaryPrimitives.ReadUInt16LittleEndian(header[MessageIdOffset..]);
        switch (messageId)
        {
            case ServerHelloId:
            {
                Span<byte> frame = stackalloc byte[ServerHelloFrameSize];
                VersionHandshakeStatus status =
                    TryCopyValidatedFrame(buffer, ServerHelloId, ServerHelloPayloadSize, frame);
                if (status != VersionHandshakeStatus.Success)
                {
                    return status;
                }

                ushort selected = BinaryPrimitives.ReadUInt16LittleEndian(frame[HeaderSize..]);
                if (selected == 0)
                {
                    return VersionHandshakeStatus.Malformed;
                }

                response = new VersionHandshakeResponse(
                    isAccepted: true, selected, rejectReason: 0, default, ServerHelloFrameSize);
                return VersionHandshakeStatus.Success;
            }

            case ConnectionRejectedId:
            {
                Span<byte> frame = stackalloc byte[RejectionFrameSize];
                VersionHandshakeStatus status =
                    TryCopyValidatedFrame(buffer, ConnectionRejectedId, RejectionPayloadSize, frame);
                if (status != VersionHandshakeStatus.Success)
                {
                    return status;
                }

                ushort reason = BinaryPrimitives.ReadUInt16LittleEndian(frame[HeaderSize..]);
                ushort min = BinaryPrimitives.ReadUInt16LittleEndian(frame[(HeaderSize + 2)..]);
                ushort max = BinaryPrimitives.ReadUInt16LittleEndian(frame[(HeaderSize + 4)..]);
                if (min == 0 || min > max)
                {
                    return VersionHandshakeStatus.Malformed;
                }

                response = new VersionHandshakeResponse(
                    isAccepted: false, selectedVersion: 0, reason,
                    new ProtocolVersionRange(min, max), RejectionFrameSize);
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
        BinaryPrimitives.WriteUInt16LittleEndian(destination[VersionOffset..], BootstrapHeaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[MessageIdOffset..], messageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[PayloadLengthOffset..], (uint)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[FlagsOffset..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[ReservedOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[SequenceOffset..], 0);
    }

    /// <summary>
    /// 기대하는 메시지의 프레임 전체를 검증하며 <paramref name="frame"/>에 복사한다.
    /// 헤더의 고정 필드(버전·플래그·예약·일련번호·페이로드 길이·메시지 ID)가 하나라도
    /// 다르면 <see cref="VersionHandshakeStatus.Malformed"/>.
    /// </summary>
    private static VersionHandshakeStatus TryCopyValidatedFrame(
        in ReadOnlySequence<byte> buffer,
        ushort expectedMessageId,
        int expectedPayloadLength,
        Span<byte> frame)
    {
        if (buffer.Length < HeaderSize)
        {
            return VersionHandshakeStatus.NeedMoreData;
        }

        Span<byte> header = frame[..HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[VersionOffset..]) != BootstrapHeaderVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(header[MessageIdOffset..]) != expectedMessageId
            || BinaryPrimitives.ReadUInt32LittleEndian(header[PayloadLengthOffset..]) != (uint)expectedPayloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(header[FlagsOffset..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header[ReservedOffset..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(header[SequenceOffset..]) != 0)
        {
            return VersionHandshakeStatus.Malformed;
        }

        int frameSize = HeaderSize + expectedPayloadLength;
        if (buffer.Length < frameSize)
        {
            return VersionHandshakeStatus.NeedMoreData;
        }

        buffer.Slice(HeaderSize, expectedPayloadLength).CopyTo(frame[HeaderSize..]);
        return VersionHandshakeStatus.Success;
    }

    private static void EnsureLength(Span<byte> destination, int required)
    {
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"핸드셰이크 프레임을 쓰려면 {required}바이트가 필요하다. 받은 크기: {destination.Length}",
                nameof(destination));
        }
    }

    private static void EnsureRangeSet(ProtocolVersionRange range, string paramName)
    {
        if (range.Min == 0)
        {
            throw new ArgumentException(
                "설정되지 않은(default) 버전 구간이다. 생성자로 만든 구간을 넘긴다.", paramName);
        }
    }
}
