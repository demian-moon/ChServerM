using System;
using ChServerM.Identity;

namespace ChServerM.Framing;

/// <summary>
/// 고정 16바이트 프레임 헤더 (ADR-0002).
/// </summary>
/// <remarks>
/// <para>와이어 레이아웃 — 리틀 엔디안 고정:</para>
/// <code>
/// offset size 필드
///   0     2   Version         프로토콜 버전
///   2     2   MessageId       메시지 타입
///   4     4   PayloadLength   페이로드 바이트 수
///   8     2   Flags           적용된 변환
///  10     2   Reserved        정렬 + 확장 여지 (0으로 채운다)
///  12     4   Sequence        커넥션 내 프레임 일련번호
///        ────
///        16
/// </code>
/// <para>
/// <b>헤더는 직렬화 포맷을 쓰지 않는다</b>(ADR-0002). 레거시는 헤더까지 FlatBuffers로
/// 감쌌고, 실제 데이터 13바이트를 담는 데 <b>52바이트</b>를 썼다. 초당 10만 프레임이면
/// 헤더 오버헤드만 3.6MB/s다. 고정 레이아웃은 그 4배를 없앤다.
/// </para>
/// <para>
/// <b>구조체 메모리 레이아웃에 의존하지 않는다.</b> 읽고 쓰기는 <c>BinaryPrimitives</c>로
/// 명시적으로 한다 — 필드 정렬·패딩·호스트 엔디안이 달라도 와이어 포맷은 동일하다.
/// 그래서 이 구조체에는 <c>[StructLayout]</c>이 없고, 코덱은 별도 어셈블리에 있다.
/// </para>
/// <para>
/// <b>체크섬 필드는 없다.</b> 무결성은 Phase 9의 AEAD 태그가 담당한다.
/// 레거시의 체크섬 검증 함수는 본문이 <c>return true</c>였다 — 있는 척하는 필드보다
/// 없는 편이 정직하다.
/// </para>
/// </remarks>
public readonly struct FrameHeader : IEquatable<FrameHeader>
{
    /// <summary>헤더 크기(바이트).</summary>
    public const int Size = 16;

    /// <summary>현재 프로토콜 버전.</summary>
    public const ushort CurrentVersion = 1;

    /// <summary><see cref="Version"/> 필드의 오프셋.</summary>
    public const int VersionOffset = 0;

    /// <summary><see cref="MessageId"/> 필드의 오프셋.</summary>
    public const int MessageIdOffset = 2;

    /// <summary><see cref="PayloadLength"/> 필드의 오프셋.</summary>
    public const int PayloadLengthOffset = 4;

    /// <summary><see cref="Flags"/> 필드의 오프셋.</summary>
    public const int FlagsOffset = 8;

    /// <summary>예약 필드의 오프셋.</summary>
    public const int ReservedOffset = 10;

    /// <summary><see cref="Sequence"/> 필드의 오프셋.</summary>
    public const int SequenceOffset = 12;

    /// <summary>헤더를 만든다.</summary>
    /// <param name="messageId">메시지 타입.</param>
    /// <param name="payloadLength">페이로드 바이트 수. 음수일 수 없다.</param>
    /// <param name="flags">적용된 변환.</param>
    /// <param name="sequence">커넥션 내 프레임 일련번호.</param>
    /// <param name="version">프로토콜 버전.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/>가 음수일 때.</exception>
    public FrameHeader(
        MessageId messageId,
        int payloadLength,
        FrameFlags flags = FrameFlags.None,
        uint sequence = 0,
        ushort version = CurrentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        MessageId = messageId;
        PayloadLength = payloadLength;
        Flags = flags;
        Sequence = sequence;
        Version = version;
    }

    /// <summary>프로토콜 버전.</summary>
    /// <remarks>
    /// 버전 필드가 있어야 레이아웃을 진화시킬 수 있다. 이것 없이 배포한 프로토콜은
    /// <b>영원히 바꿀 수 없다</b> — 구버전 클라이언트가 새 헤더를 쓰레기로 읽기 때문이다.
    /// </remarks>
    public ushort Version { get; }

    /// <summary>메시지 타입.</summary>
    public MessageId MessageId { get; }

    /// <summary>페이로드 바이트 수.</summary>
    /// <remarks>
    /// 와이어에서는 부호 없는 32비트지만 여기서는 <see cref="int"/>다.
    /// 디코더가 <b>반드시</b> 최대 프레임 크기와 대조한 뒤에만 이 값을 만든다.
    /// 상한 검사 없이 이 값을 버퍼 할당에 쓰면 그것이 곧 메모리 고갈 공격 경로다.
    /// </remarks>
    public int PayloadLength { get; }

    /// <summary>페이로드에 적용된 변환.</summary>
    public FrameFlags Flags { get; }

    /// <summary>커넥션 내 프레임 일련번호.</summary>
    /// <remarks>순서 진단과 Phase 9의 리플레이 방지에 쓴다. 넘치면 0으로 돈다.</remarks>
    public uint Sequence { get; }

    /// <summary>헤더와 페이로드를 합친 전체 프레임 크기.</summary>
    public int TotalLength => Size + PayloadLength;

    /// <inheritdoc />
    public bool Equals(FrameHeader other) =>
        Version == other.Version
        && MessageId == other.MessageId
        && PayloadLength == other.PayloadLength
        && Flags == other.Flags
        && Sequence == other.Sequence;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FrameHeader other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Version, MessageId, PayloadLength, Flags, Sequence);

    /// <summary>두 헤더가 같은지 비교한다.</summary>
    public static bool operator ==(FrameHeader left, FrameHeader right) => left.Equals(right);

    /// <summary>두 헤더가 다른지 비교한다.</summary>
    public static bool operator !=(FrameHeader left, FrameHeader right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"frame[v{Version} {MessageId} len={PayloadLength} flags={Flags} seq={Sequence}]";
}
