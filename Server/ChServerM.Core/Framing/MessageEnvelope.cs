using System;
using ChServerM.Identity;

namespace ChServerM.Framing;

/// <summary>
/// 프레임 하나의 논리 메타데이터 — 디스패치·미들웨어가 소비하는 전부 (ADR-0010).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 이 타입이 없으면 Core 의 프레이밍·디스패치 계약이 특정 와이어
/// 포맷(16바이트 고정 헤더)에 결박된다 — 실제로 그랬고, varint 프레이밍과
/// <c>stateless-web</c> 프로필이 구조적으로 막혀 있었다(2026-08-04 감사 H4).
/// 와이어에 바이트가 어떻게 놓이는가(<c>FrameHeader</c>, 프레이밍 어댑터 소유)와
/// 프레임워크가 소비하는 논리 정보(이 타입)를 분리한다.
/// </para>
/// <para>
/// <b>담는 것과 담지 않는 것.</b> <see cref="MessageId"/>는 디스패치 라우팅이,
/// <see cref="Flags"/>는 페이로드 코덱(압축·암호화 축)이, <see cref="Sequence"/>는
/// 리플레이 방지가 소비한다 — 전부 횡단 축이라 Core 에 있어야 한다.
/// 프로토콜 버전·헤더 크기·필드 오프셋은 와이어 포맷 소유물이므로 여기 없다.
/// 페이로드 길이도 없다 — 페이로드 자체(<c>ReadOnlySequence&lt;byte&gt;</c>)가 항상
/// 함께 다니므로 중복이다.
/// </para>
/// <para>
/// <b>표현 불가 값의 규약 (ADR-0010).</b> 와이어에 해당 필드가 없는 프레이밍(varint 등)의
/// 디코더가 <see cref="FrameFlags.None"/>·<c>Sequence = 0</c> 을 채우는 것은 "그 와이어에
/// 그 개념이 없다"는 사실의 표현이라 정당하다. 반대 방향은 다르다 — 그런 프레이밍의
/// <b>인코더는 기본값이 아닌 값을 받으면 예외를 던져야 한다</b>. 조용히 버리면
/// 압축 플래그가 유실되는 조용한 실패가 된다(레거시의 "압축이 한 번도 실행되지 않음").
/// </para>
/// <para><b>스레드 규약.</b> 불변 값 타입이다. 어디서든 안전하다.</para>
/// </remarks>
public readonly struct MessageEnvelope : IEquatable<MessageEnvelope>
{
    /// <summary>엔벨로프를 만든다.</summary>
    /// <param name="messageId">메시지 타입.</param>
    /// <param name="flags">페이로드에 적용된 변환.</param>
    /// <param name="sequence">커넥션 내 프레임 일련번호.</param>
    /// <remarks>
    /// <paramref name="flags"/>·<paramref name="sequence"/> 에 기본값을 두지 않는다 —
    /// 기본값이 있으면 압축·리플레이 방지가 조용히 무력화되는 우회로가 된다
    /// (CLAUDE.md 8.1 의 RS0026 사례와 같은 원리).
    /// </remarks>
    public MessageEnvelope(MessageId messageId, FrameFlags flags, uint sequence)
    {
        MessageId = messageId;
        Flags = flags;
        Sequence = sequence;
    }

    /// <summary>메시지 타입. 디스패치 라우팅의 유일한 키다.</summary>
    public MessageId MessageId { get; }

    /// <summary>페이로드에 적용된 변환.</summary>
    /// <remarks>와이어에 플래그 개념이 없는 프레이밍에서는 항상 <see cref="FrameFlags.None"/>이다.</remarks>
    public FrameFlags Flags { get; }

    /// <summary>커넥션 내 프레임 일련번호.</summary>
    /// <remarks>
    /// 순서 진단과 Phase 9 리플레이 방지에 쓴다. 넘치면 0으로 돈다.
    /// 와이어에 일련번호 개념이 없는 프레이밍에서는 항상 0이다.
    /// </remarks>
    public uint Sequence { get; }

    /// <inheritdoc />
    public bool Equals(MessageEnvelope other) =>
        MessageId == other.MessageId
        && Flags == other.Flags
        && Sequence == other.Sequence;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MessageEnvelope other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(MessageId, Flags, Sequence);

    /// <summary>두 엔벨로프가 같은지 비교한다.</summary>
    public static bool operator ==(MessageEnvelope left, MessageEnvelope right) => left.Equals(right);

    /// <summary>두 엔벨로프가 다른지 비교한다.</summary>
    public static bool operator !=(MessageEnvelope left, MessageEnvelope right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"envelope[{MessageId} flags={Flags} seq={Sequence}]";
}
