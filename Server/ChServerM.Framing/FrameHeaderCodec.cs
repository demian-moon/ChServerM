using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ChServerM.Framing;

/// <summary>
/// 16바이트 프레임 헤더의 와이어 표현을 읽고 쓴다 (ADR-0002).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 와이어 레이아웃을 아는 코드가 <b>이 한 곳뿐</b>이어야 한다.
/// 오프셋 계산이 디코더와 인코더에 각각 있으면 한쪽만 고치는 사고가 난다.
/// 그런 불일치는 "수신 측 프레임 경계가 통째로 밀리는" 형태로 나타나고,
/// 커넥션이 끊길 때까지 이어지므로 디버깅이 극히 어렵다.
/// </para>
/// <para>
/// <b>왜 <c>MemoryMarshal</c>이 아니라 <c>BinaryPrimitives</c>인가.</b>
/// 구조체를 그대로 캐스팅하면 필드 정렬·패딩·호스트 엔디안에 와이어 포맷이 끌려간다.
/// 지금은 x64 리틀 엔디안만 쓰지만, ARM 서버나 빅 엔디안 장비가 섞이는 순간
/// <b>조용히 다른 바이트</b>가 나간다. 명시적 리틀 엔디안 읽기/쓰기는 그 위험이 0이고,
/// JIT이 단일 <c>mov</c>로 접는다 — 비용 차이가 없다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 상태가 없는 정적 클래스다. 어디서 불러도 안전하다.
/// </para>
/// <para>
/// <b>할당.</b> 힙 할당이 없다. 호출자가 준 스팬 위에서만 동작한다.
/// </para>
/// </remarks>
public static class FrameHeaderCodec
{
    /// <summary>이 코덱이 다루는 헤더 크기(바이트).</summary>
    public const int HeaderSize = FrameHeader.Size;

    /// <summary>정의된 모든 플래그 비트의 합집합.</summary>
    /// <remarks>
    /// 여기 없는 비트가 켜져 있으면 <see cref="FrameDecodeStatus.InvalidFlags"/>다.
    /// <b>플래그를 추가하면 이 상수도 반드시 갱신한다</b> — 잊으면 새 플래그를 쓴 프레임이
    /// 전부 거부된다. 그 실패는 시끄럽고 즉시 드러나므로, 조용히 무시되는 것보다 낫다.
    /// </remarks>
    public const FrameFlags KnownFlags =
        FrameFlags.Compressed | FrameFlags.Encrypted | FrameFlags.Fragmented | FrameFlags.EndOfMessage;

    /// <summary>헤더를 와이어 형식으로 쓴다.</summary>
    /// <param name="destination">쓸 대상. <see cref="HeaderSize"/> 이상이어야 한다.</param>
    /// <param name="header">쓸 헤더.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/>이 너무 짧을 때.</exception>
    /// <remarks>
    /// 예약 필드는 항상 0으로 채운다. 호출자가 남긴 쓰레기 값이 새어나가면
    /// 나중에 그 비트를 쓸 수 없게 된다.
    /// </remarks>
    public static void Write(Span<byte> destination, in FrameHeader header)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"헤더를 쓰려면 {HeaderSize}바이트가 필요하다. 받은 크기: {destination.Length}",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[FrameHeader.VersionOffset..], header.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[FrameHeader.MessageIdOffset..], header.MessageId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[FrameHeader.PayloadLengthOffset..], (uint)header.PayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[FrameHeader.FlagsOffset..], (ushort)header.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[FrameHeader.ReservedOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[FrameHeader.SequenceOffset..], header.Sequence);
    }

    /// <summary>와이어 형식에서 헤더를 읽고 검증한다.</summary>
    /// <param name="source">읽을 원본. <see cref="HeaderSize"/> 이상이어야 한다.</param>
    /// <param name="maxPayloadLength">허용하는 최대 페이로드 크기.</param>
    /// <param name="acceptedVersion">받아들일 프로토콜 버전.</param>
    /// <param name="header">성공하면 읽어낸 헤더.</param>
    /// <returns>
    /// <see cref="FrameDecodeStatus.Decoded"/>이면 성공.
    /// 그 외에는 전부 커넥션을 닫아야 하는 실패다.
    /// <see cref="FrameDecodeStatus.NeedMoreData"/>는 <b>반환하지 않는다</b> —
    /// 길이 판단은 호출자(디코더)의 몫이다.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="source"/>가 너무 짧을 때.</exception>
    /// <remarks>
    /// <para><b>검증 순서가 중요하다.</b></para>
    /// <list type="number">
    ///   <item><description>버전 — 다르면 나머지 필드의 의미를 알 수 없다</description></item>
    ///   <item><description>길이 상한 — <b>버퍼를 잡기 전에</b> 걸러야 의미가 있다</description></item>
    ///   <item><description>플래그 — 모르는 비트를 무시하면 조용한 오동작이 된다</description></item>
    ///   <item><description>예약 필드 — 0이 아니면 거부한다. 그래야 나중에 쓸 수 있다</description></item>
    /// </list>
    /// <para>
    /// 와이어의 길이 필드는 부호 없는 32비트라 <c>int</c>로 표현 못 하는 값이 올 수 있다.
    /// <paramref name="maxPayloadLength"/>가 <c>int</c>이므로 <c>uint</c>인 채로 비교해
    /// <b>부호 있는 정수 오버플로를 원천 차단</b>한다. 레거시는 이런 값을 그대로
    /// <c>int</c>로 캐스팅해 음수 길이를 만들 수 있었다.
    /// </para>
    /// </remarks>
    public static FrameDecodeStatus TryRead(
        ReadOnlySpan<byte> source,
        int maxPayloadLength,
        ushort acceptedVersion,
        out FrameHeader header)
    {
        if (source.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"헤더를 읽으려면 {HeaderSize}바이트가 필요하다. 받은 크기: {source.Length}",
                nameof(source));
        }

        header = default;

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.VersionOffset..]);
        if (version != acceptedVersion)
        {
            return FrameDecodeStatus.VersionMismatch;
        }

        // uint 인 채로 비교한다. int 로 캐스팅한 뒤 비교하면 2GB 이상 값이 음수가 된다.
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(source[FrameHeader.PayloadLengthOffset..]);
        if (payloadLength > (uint)maxPayloadLength)
        {
            return FrameDecodeStatus.TooLarge;
        }

        ushort rawFlags = BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.FlagsOffset..]);
        if ((rawFlags & ~(ushort)KnownFlags) != 0)
        {
            return FrameDecodeStatus.InvalidFlags;
        }

        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.ReservedOffset..]);
        if (reserved != 0)
        {
            return FrameDecodeStatus.Malformed;
        }

        ushort messageId = BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.MessageIdOffset..]);
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[FrameHeader.SequenceOffset..]);

        header = new FrameHeader(
            new Identity.MessageId(messageId),
            (int)payloadLength,
            (FrameFlags)rawFlags,
            sequence,
            version);

        return FrameDecodeStatus.Decoded;
    }

    /// <summary>검증 없이 헤더를 읽는다.</summary>
    /// <param name="source">읽을 원본. <see cref="HeaderSize"/> 이상이어야 한다.</param>
    /// <returns>읽어낸 헤더.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/>가 너무 짧을 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException">길이 필드가 <see cref="int"/> 범위를 넘을 때.</exception>
    /// <remarks>
    /// <b>신뢰할 수 없는 입력에 쓰지 않는다.</b> 왕복 테스트와 자기가 쓴 바이트를 다시 읽는
    /// 경우를 위한 것이다. 네트워크에서 온 바이트에는 <see cref="TryRead"/>를 쓴다.
    /// </remarks>
    public static FrameHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"헤더를 읽으려면 {HeaderSize}바이트가 필요하다. 받은 크기: {source.Length}",
                nameof(source));
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(source[FrameHeader.PayloadLengthOffset..]);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, (uint)int.MaxValue);

        return new FrameHeader(
            new Identity.MessageId(BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.MessageIdOffset..])),
            (int)payloadLength,
            (FrameFlags)BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.FlagsOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[FrameHeader.SequenceOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[FrameHeader.VersionOffset..]));
    }

    /// <summary>플래그 조합에 정의되지 않은 비트가 있는지 검사한다.</summary>
    /// <param name="flags">검사할 플래그.</param>
    /// <returns>모두 정의된 비트면 <see langword="true"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreFlagsKnown(FrameFlags flags) => (flags & ~KnownFlags) == 0;
}
