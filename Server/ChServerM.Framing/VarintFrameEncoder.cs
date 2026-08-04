using System;
using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// varint 길이 접두사 헤더를 쓰는 인코더.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="VarintFrameDecoder"/>의 송신 짝이다. 헤더만 쓰고
/// 페이로드는 호출자가 같은 버퍼에 이어서 쓴다 (<see cref="FixedHeaderFrameEncoder"/>와
/// 동일한 무복사 규약).
/// </para>
/// <para>
/// <b>표현 불가 값은 예외다 (ADR-0010).</b> 이 와이어에는 플래그·일련번호 필드가 없다.
/// 기본값이 아닌 <see cref="MessageEnvelope.Flags"/>/<see cref="MessageEnvelope.Sequence"/>를
/// 받으면 <see cref="NotSupportedException"/>을 던진다 — 조용히 버리면 압축 표시가
/// 유실된 페이로드를 상대가 원본으로 해석하는 조용한 실패가 된다(레거시의
/// "압축이 한 번도 실행되지 않음"과 같은 유형). 압축·리플레이 방지를 쓰는 프로필은
/// 고정 헤더 프레이밍을 조립해야 한다.
/// </para>
/// <para><b>스레드 규약.</b> 불변이라 스레드 안전하다. 단 <see cref="IBufferWriter{T}"/>는
/// 대개 아니다 — 하나의 <c>PipeWriter</c>에 여러 스레드가 동시에 쓰면 안 된다.</para>
/// <para><b>할당.</b> 프레임당 힙 할당 0.</para>
/// </remarks>
public sealed class VarintFrameEncoder : IFrameEncoder
{
    private readonly int _maxPayloadLength;

    /// <summary>인코더를 만든다.</summary>
    /// <param name="maxPayloadLength">허용하는 최대 페이로드 크기.</param>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났을 때.</exception>
    /// <remarks>
    /// <see cref="FramingOptions"/>를 받지 않는 이유는 <see cref="VarintFrameDecoder"/>와
    /// 같다 — 이 와이어에 프로토콜 버전이 없다.
    /// </remarks>
    public VarintFrameEncoder(int maxPayloadLength = FramingOptions.DefaultMaxPayloadLength)
    {
        if (maxPayloadLength <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(maxPayloadLength)}는 1 이상이어야 한다. 현재 값: {maxPayloadLength}");
        }

        if (maxPayloadLength > FramingOptions.AbsoluteMaxPayloadLength)
        {
            throw new InvalidOperationException(
                $"{nameof(maxPayloadLength)}({maxPayloadLength})가 절대 상한" +
                $"({FramingOptions.AbsoluteMaxPayloadLength})을 넘는다. 이 크기가 필요하면 조각화나 스트리밍을 쓴다.");
        }

        _maxPayloadLength = maxPayloadLength;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 길이 u32 최대 5바이트 + 메시지 ID u16 최대 3바이트 = 8. 실제 헤더는 프레임마다
    /// 다르며 작은 프레임이면 2바이트다 — 상한은 조립 검사(ADR-0007)의 최악 조건 계산용이다.
    /// </remarks>
    public int MaxHeaderSize => VarintCodec.MaxUInt32Bytes + 3;

    /// <summary>허용하는 최대 페이로드 크기.</summary>
    public int MaxPayloadLength => _maxPayloadLength;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="writer"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 페이로드 길이가 음수이거나 <see cref="MaxPayloadLength"/>를 넘을 때.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="envelope"/>에 이 와이어가 표현할 수 없는 값
    /// (기본값이 아닌 <c>Flags</c>/<c>Sequence</c>)이 있을 때.
    /// </exception>
    public void WriteHeader(IBufferWriter<byte> writer, in MessageEnvelope envelope, int payloadLength)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        if (payloadLength > _maxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                payloadLength,
                $"페이로드가 상한({_maxPayloadLength})을 넘는다. 상대가 이 프레임을 거부하고 커넥션을 닫는다.");
        }

        if (envelope.Flags != FrameFlags.None)
        {
            throw new NotSupportedException(
                $"varint 프레이밍에는 플래그 필드가 없다({nameof(envelope.Flags)}={envelope.Flags}). " +
                "조용히 버리면 압축·암호화 표시가 유실되므로 예외로 막는다. " +
                "페이로드 변환이 필요한 프로필은 고정 헤더 프레이밍을 쓴다 (ADR-0010).");
        }

        if (envelope.Sequence != 0)
        {
            throw new NotSupportedException(
                $"varint 프레이밍에는 일련번호 필드가 없다({nameof(envelope.Sequence)}={envelope.Sequence}). " +
                "리플레이 방지가 필요한 프로필은 고정 헤더 프레이밍을 쓴다 (ADR-0010).");
        }

        Span<byte> destination = writer.GetSpan(MaxHeaderSize);
        int written = VarintCodec.Write(destination, (uint)payloadLength);
        written += VarintCodec.Write(destination[written..], envelope.MessageId.Value);
        writer.Advance(written);
    }
}
