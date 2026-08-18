using System;
using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// varint 길이 접두사로 프레임 경계를 찾아내는 디코더 — 프레이밍 축의 두 번째 구현.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이 타입이 곧 계약의 증명이다.</b> 두 번째 구현이 나오기 전까지
/// <c>IFrameDecoder</c> 는 가설이었다(축 추가 규칙, CLAUDE.md 3절). 고정 16바이트와
/// 정반대 성질(가변 길이 헤더, 버전·플래그·일련번호 없음)의 이 구현이 같은 계약에
/// 들어온다는 것이 ADR-0010 분리의 검증이다.
/// </para>
/// <para>
/// <b>와이어 레이아웃</b> — 프레임당 오버헤드 최소가 목적인 프로필용이다.
/// 작은 프레임이면 헤더가 2바이트로 끝난다(고정 헤더의 1/8).
/// </para>
/// <code>
/// varint payloadLength   1~5 바이트 (u32, LEB128 정규형)
/// varint messageId       1~3 바이트 (u16 범위, LEB128 정규형)
/// payload                payloadLength 바이트
/// </code>
/// <para>
/// <b>없는 필드는 없다고 보고한다 (ADR-0010).</b> 이 와이어에는 버전·플래그·일련번호가
/// 없으므로 엔벨로프의 <c>Flags</c> 는 항상 <see cref="FrameFlags.None"/>, <c>Sequence</c> 는
/// 항상 0이다 — 사실의 표현이지 위조가 아니다. 압축·리플레이 방지가 필요한 프로필은
/// 이 프레이밍을 쓰면 안 되고, 그 조합은 인코더가 예외로 막는다.
/// </para>
/// <para>
/// <b>정규형이 아닌 varint 는 <see cref="FrameDecodeStatus.Malformed"/>다.</b>
/// 같은 프레임의 표현이 여럿이면 바이트 단위 검증(Phase 9 AEAD)이 흔들린다.
/// </para>
/// <para>
/// <b>상태를 갖지 않는다.</b> 부분 프레임은 <c>PipeReader</c> 버퍼가 들고 있다.
/// 인스턴스 하나를 모든 커넥션이 공유한다 (<see cref="FixedHeaderFrameDecoder"/>와 동일).
/// </para>
/// <para><b>스레드 규약.</b> 불변이므로 스레드 안전하다.</para>
/// <para><b>할당.</b> 프레임당 힙 할당 0. <see cref="SequenceReader{T}"/>는 ref struct 다.</para>
/// </remarks>
public sealed class VarintFrameDecoder : IFrameDecoder
{
    private readonly int _maxPayloadLength;

    /// <summary>디코더를 만든다.</summary>
    /// <param name="maxPayloadLength">허용하는 최대 페이로드 크기.</param>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났을 때.</exception>
    /// <remarks>
    /// <see cref="FramingOptions"/>를 받지 않는 이유: 그 타입의 <c>ProtocolVersion</c> 이
    /// 이 와이어에는 존재하지 않는다. 무시되는 설정을 받는 것은 조용한 실패의 초대장이다.
    /// </remarks>
    public VarintFrameDecoder(int maxPayloadLength = FramingOptions.DefaultMaxPayloadLength)
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
    public int MaxPayloadLength => _maxPayloadLength;

    /// <inheritdoc />
    /// <remarks>길이·메시지 ID 뿐이다 — 플래그·일련번호·버전 필드가 없다.</remarks>
    public FrameCodecCapabilities Capabilities => FrameCodecCapabilities.None;

    /// <inheritdoc />
    public FrameDecodeResult Decode(in ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> reader = new(buffer);

        // 1) 길이. 상한 검사는 페이로드 도착 전에 한다 — 4바이트짜리 거짓말로
        //    메모리를 잡게 두지 않는다 (버퍼를 잡기 전에 판정, FixedHeader 와 동일 원칙).
        switch (VarintCodec.TryReadUInt32(ref reader, out uint payloadLength))
        {
            case VarintCodec.Status.NeedMoreData:
                return FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);
            case VarintCodec.Status.Malformed:
                return FrameDecodeResult.Failed(FrameDecodeStatus.Malformed, buffer.Start);
        }

        // uint 인 채로 비교한다. int 캐스팅 후 비교하면 2GB 이상 값이 음수가 된다.
        if (payloadLength > (uint)_maxPayloadLength)
        {
            return FrameDecodeResult.Failed(FrameDecodeStatus.TooLarge, buffer.Start);
        }

        // 2) 메시지 ID.
        switch (VarintCodec.TryReadUInt32(ref reader, out uint messageId))
        {
            case VarintCodec.Status.NeedMoreData:
                return FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);
            case VarintCodec.Status.Malformed:
                return FrameDecodeResult.Failed(FrameDecodeStatus.Malformed, buffer.Start);
        }

        if (messageId > ushort.MaxValue)
        {
            return FrameDecodeResult.Failed(FrameDecodeStatus.Malformed, buffer.Start);
        }

        // 3) 페이로드가 아직이다.
        if (reader.Remaining < payloadLength)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);
        }

        ReadOnlySequence<byte> payload = buffer.Slice(reader.Position, payloadLength);
        SequencePosition consumed = buffer.GetPosition(payloadLength, reader.Position);

        MessageEnvelope envelope = new(
            new Identity.MessageId((ushort)messageId), FrameFlags.None, 0);
        return FrameDecodeResult.Decoded(envelope, payload, consumed);
    }
}
