using System;
using System.Buffers;
using System.Diagnostics;
using ChServerM.Diagnostics;

namespace ChServerM.Framing;

/// <summary>
/// <see cref="IFrameDecoder.Decode"/> 한 번의 결과.
/// </summary>
/// <remarks>
/// <para>
/// <c>out</c> 매개변수 네 개 대신 결과 구조체를 돌려준다. 반환값이 커도 JIT이
/// 호출자 지역 변수에 직접 써넣으므로 <c>out</c> 방식과 비용이 같고,
/// <b>호출자가 부분적으로만 처리하고 지나가기 어렵다</b> — 특히
/// <see cref="Consumed"/>/<see cref="Examined"/>를 빠뜨리면 파이프가 멈춰버린다.
/// </para>
/// <para>
/// <b><see cref="Payload"/>의 수명.</b> 호출자가 <c>PipeReader.AdvanceTo</c>를 부르는
/// 순간 무효가 된다. 저장하지 말고, 필요하면 <b>그 전에</b> 복사한다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct FrameDecodeResult : IEquatable<FrameDecodeResult>
{
    private FrameDecodeResult(
        FrameDecodeStatus status,
        in MessageEnvelope envelope,
        in ReadOnlySequence<byte> payload,
        SequencePosition consumed,
        SequencePosition examined)
    {
        Status = status;
        Envelope = envelope;
        Payload = payload;
        Consumed = consumed;
        Examined = examined;
    }

    /// <summary>디코딩 결과.</summary>
    public FrameDecodeStatus Status { get; }

    /// <summary>읽어낸 논리 엔벨로프. <see cref="Status"/>가 <see cref="FrameDecodeStatus.Decoded"/>일 때만 유효하다.</summary>
    /// <remarks>
    /// 와이어 헤더 자체가 아니라 그 <b>투영</b>이다(ADR-0010). 디코더는 자기 와이어
    /// 포맷을 검증한 뒤, 프레임워크가 소비하는 논리 정보만 여기에 담는다.
    /// </remarks>
    public MessageEnvelope Envelope { get; }

    /// <summary>읽어낸 페이로드. <see cref="Status"/>가 <see cref="FrameDecodeStatus.Decoded"/>일 때만 유효하다.</summary>
    /// <remarks><c>AdvanceTo</c> 호출 이후에는 접근하면 안 된다.</remarks>
    public ReadOnlySequence<byte> Payload { get; }

    /// <summary><c>PipeReader.AdvanceTo</c>에 넘길 소비 위치.</summary>
    public SequencePosition Consumed { get; }

    /// <summary><c>PipeReader.AdvanceTo</c>에 넘길 검사 위치.</summary>
    /// <remarks>
    /// <see cref="FrameDecodeStatus.NeedMoreData"/>일 때 이 값이 버퍼 끝이어야
    /// 파이프가 "더 읽어야 한다"는 것을 안다. 여기를 틀리면 <b>교착</b>이다.
    /// </remarks>
    public SequencePosition Examined { get; }

    /// <summary>프레임 하나를 온전히 읽었는지 여부.</summary>
    public bool IsDecoded => Status == FrameDecodeStatus.Decoded;

    /// <summary>커넥션을 닫아야 하는 실패인지 여부.</summary>
    public bool IsFatal => Status is not (FrameDecodeStatus.Decoded or FrameDecodeStatus.NeedMoreData);

    /// <summary>프레임을 온전히 읽었을 때의 결과를 만든다.</summary>
    /// <param name="envelope">읽어낸 논리 엔벨로프.</param>
    /// <param name="payload">읽어낸 페이로드.</param>
    /// <param name="consumed">프레임 끝 위치.</param>
    public static FrameDecodeResult Decoded(in MessageEnvelope envelope, in ReadOnlySequence<byte> payload, SequencePosition consumed) =>
        new(FrameDecodeStatus.Decoded, envelope, payload, consumed, consumed);

    /// <summary>데이터가 더 필요할 때의 결과를 만든다.</summary>
    /// <param name="consumed">지금까지 소비한 위치. 보통 버퍼 시작이다.</param>
    /// <param name="examined">검사를 마친 위치. 보통 버퍼 끝이다.</param>
    public static FrameDecodeResult NeedMoreData(SequencePosition consumed, SequencePosition examined) =>
        new(FrameDecodeStatus.NeedMoreData, default, default, consumed, examined);

    /// <summary>커넥션을 닫아야 하는 실패 결과를 만든다.</summary>
    /// <param name="status">실패 종류.</param>
    /// <param name="position">실패를 판정한 위치.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/>가 실패를 나타내지 않을 때.
    /// </exception>
    public static FrameDecodeResult Failed(FrameDecodeStatus status, SequencePosition position)
    {
        if (status is FrameDecodeStatus.Decoded or FrameDecodeStatus.NeedMoreData)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status,
                "실패 상태가 아니다. Decoded 는 Decoded(...) 팩토리로, NeedMoreData 는 NeedMoreData(...) 팩토리로 만든다.");
        }

        return new FrameDecodeResult(status, default, default, position, position);
    }

    /// <summary>이 결과에 대응하는 오류 코드를 구한다.</summary>
    /// <returns>실패가 아니면 <see cref="ErrorCode.None"/>.</returns>
    public ErrorCode ToErrorCode() => Status switch
    {
        FrameDecodeStatus.Malformed => ErrorCode.MalformedFrame,
        FrameDecodeStatus.TooLarge => ErrorCode.FrameTooLarge,
        FrameDecodeStatus.VersionMismatch => ErrorCode.ProtocolVersionMismatch,
        FrameDecodeStatus.InvalidFlags => ErrorCode.InvalidFrameFlags,
        _ => ErrorCode.None,
    };

    /// <inheritdoc />
    public bool Equals(FrameDecodeResult other) =>
        Status == other.Status
        && Envelope.Equals(other.Envelope)
        && Payload.Equals(other.Payload)
        && Consumed.Equals(other.Consumed)
        && Examined.Equals(other.Examined);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FrameDecodeResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Status, Envelope);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    public static bool operator ==(FrameDecodeResult left, FrameDecodeResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    public static bool operator !=(FrameDecodeResult left, FrameDecodeResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => IsDecoded ? $"{Status} {Envelope}" : Status.ToString();
}
