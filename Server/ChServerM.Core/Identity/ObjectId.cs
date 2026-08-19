using System;
using System.Diagnostics;
using System.Globalization;

namespace ChServerM.Identity;

/// <summary>
/// 노드 성분을 포함하는 64비트 분산 식별자.
/// </summary>
/// <remarks>
/// <para>비트 배치 (Snowflake 계열):</para>
/// <code>
/// 63     62                          22        12          0
///  │      │                           │         │          │
///  0 │ timestamp (41b) │ nodeId (10b) │ sequence (12b) │
/// </code>
/// <list type="bullet">
///   <item><description><b>부호 비트 0 고정</b> — <see cref="long"/>으로 다뤄도 항상 양수. 나머지 연산·정렬·직렬화가 안전하다</description></item>
///   <item><description><b>타임스탬프 41비트</b> — 사용자 지정 epoch 기준 밀리초. 약 69년</description></item>
///   <item><description><b>노드 10비트</b> — 최대 1024개 노드</description></item>
///   <item><description><b>시퀀스 12비트</b> — 노드·밀리초당 4096개</description></item>
/// </list>
/// <para>
/// <b>노드 성분이 있는 이유.</b> 레거시 <c>GlobalM.MakeGameOid()</c>는 프로세스 전역
/// <c>Interlocked.Increment</c> 카운터였다. 노드가 둘 이상이면 같은 ID가 두 번 발급되고,
/// 프로세스를 재시작하면 1부터 다시 시작해 영속화된 데이터와 겹친다.
/// 이 결함은 다중 노드 배포를 구조적으로 막는다 — 그래서 <b>지금</b> 노드 성분을 넣는다.
/// </para>
/// <para>대략적인 시간순 정렬이 보장되므로 데이터베이스 인덱스에 유리하다.</para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>을 구현해 ZLogger 같은
/// 무할당 로깅 축과 보간 문자열 핸들러가 <b>문자열 할당 없이</b> 인라인 포맷할 수 있다
/// (감사 2026-08-18 C-4). 표기는 진단 전용 단일 형식이므로 format/provider 인자는 무시하며,
/// 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>타임스탬프 비트 수.</summary>
    public const int TimestampBits = 41;

    /// <summary>노드 식별자 비트 수.</summary>
    public const int NodeIdBits = 10;

    /// <summary>시퀀스 비트 수.</summary>
    public const int SequenceBits = 12;

    /// <summary>표현 가능한 최대 노드 식별자.</summary>
    public const int MaxNodeId = (1 << NodeIdBits) - 1;

    /// <summary>노드·밀리초당 최대 시퀀스 값.</summary>
    public const int MaxSequence = (1 << SequenceBits) - 1;

    private const int NodeIdShift = SequenceBits;
    private const int TimestampShift = SequenceBits + NodeIdBits;
    private const long TimestampMask = (1L << TimestampBits) - 1;

    private readonly long _value;

    /// <summary>원본 수치로 식별자를 만든다.</summary>
    /// <remarks>영속화된 값을 되살릴 때 쓴다. 새로 발급할 때는 <see cref="Create"/>를 쓴다.</remarks>
    public ObjectId(long value) => _value = value;

    /// <summary>설정되지 않은 값.</summary>
    public static ObjectId None => default;

    /// <summary>구성 요소로 식별자를 조립한다.</summary>
    /// <param name="timestampMs">epoch 기준 경과 밀리초. <see cref="TimestampBits"/>비트에 맞게 잘린다.</param>
    /// <param name="nodeId">노드 식별자. <c>0</c>~<see cref="MaxNodeId"/>.</param>
    /// <param name="sequence">같은 밀리초 안의 일련번호. <c>0</c>~<see cref="MaxSequence"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">인자가 표현 범위를 벗어났을 때.</exception>
    public static ObjectId Create(long timestampMs, int nodeId, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timestampMs, TimestampMask);
        ArgumentOutOfRangeException.ThrowIfNegative(nodeId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeId, MaxNodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, MaxSequence);

        long value = (timestampMs << TimestampShift)
                   | ((long)nodeId << NodeIdShift)
                   | (uint)sequence;
        return new ObjectId(value);
    }

    /// <summary>원본 수치.</summary>
    public long Value => _value;

    /// <summary>epoch 기준 경과 밀리초.</summary>
    public long TimestampMs => (_value >> TimestampShift) & TimestampMask;

    /// <summary>이 식별자를 발급한 노드.</summary>
    public int NodeId => (int)((_value >> NodeIdShift) & MaxNodeId);

    /// <summary>같은 밀리초 안의 일련번호.</summary>
    public int Sequence => (int)(_value & MaxSequence);

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _value == 0;

    /// <summary>파티션 배정에 쓸 안정 해시 키를 만든다.</summary>
    public PartitionKey ToPartitionKey() => PartitionKey.FromValue((ulong)_value);

    /// <inheritdoc />
    public bool Equals(ObjectId other) => _value == other._value;

    /// <inheritdoc />
    public int CompareTo(ObjectId other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);

    /// <summary>왼쪽이 더 먼저 발급됐는지 비교한다.</summary>
    public static bool operator <(ObjectId left, ObjectId right) => left._value < right._value;

    /// <summary>왼쪽이 더 나중에 발급됐는지 비교한다.</summary>
    public static bool operator >(ObjectId left, ObjectId right) => left._value > right._value;

    /// <summary>왼쪽이 같거나 더 먼저 발급됐는지 비교한다.</summary>
    public static bool operator <=(ObjectId left, ObjectId right) => left._value <= right._value;

    /// <summary>왼쪽이 같거나 더 나중에 발급됐는지 비교한다.</summary>
    public static bool operator >=(ObjectId left, ObjectId right) => left._value >= right._value;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"oid:{_value}");

    /// <summary><see cref="ISpanFormattable"/> 계약용 오버로드. 인자를 무시하고 <see cref="ToString()"/>과 같은 표기를 돌려준다.</summary>
    /// <param name="format">무시한다 — 진단 전용 단일 표기다.</param>
    /// <param name="formatProvider">무시한다 — 표기는 항상 인바리언트다.</param>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>진단 표기를 문자 버퍼에 쓴다. 출력은 <see cref="ToString()"/>과 동일하다.</summary>
    /// <param name="destination">쓸 버퍼.</param>
    /// <param name="charsWritten">성공 시 쓴 문자 수. 실패 시 0.</param>
    /// <param name="format">무시한다 — 진단 전용 단일 표기다.</param>
    /// <param name="provider">무시한다 — 표기는 항상 인바리언트다.</param>
    /// <returns>버퍼가 충분하면 <see langword="true"/>.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = 0;
        ReadOnlySpan<char> prefix = "oid:";
        if (!prefix.TryCopyTo(destination))
        {
            return false;
        }

        if (!_value.TryFormat(destination[prefix.Length..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        charsWritten = prefix.Length + written;
        return true;
    }

    /// <summary>진단 표기를 UTF-8 버퍼에 쓴다. 출력은 <see cref="ToString()"/>의 UTF-8 인코딩과 동일하다.</summary>
    /// <param name="utf8Destination">쓸 버퍼.</param>
    /// <param name="bytesWritten">성공 시 쓴 바이트 수. 실패 시 0.</param>
    /// <param name="format">무시한다 — 진단 전용 단일 표기다.</param>
    /// <param name="provider">무시한다 — 표기는 항상 인바리언트다.</param>
    /// <returns>버퍼가 충분하면 <see langword="true"/>.</returns>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        bytesWritten = 0;
        ReadOnlySpan<byte> prefix = "oid:"u8;
        if (!prefix.TryCopyTo(utf8Destination))
        {
            return false;
        }

        if (!_value.TryFormat(utf8Destination[prefix.Length..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        bytesWritten = prefix.Length + written;
        return true;
    }
}

/// <summary>
/// <see cref="ObjectId"/>를 발급한다.
/// </summary>
/// <remarks>
/// 구현체는 <b>스레드 안전해야 한다.</b> 같은 노드에서 같은 값이 두 번 나오면 안 된다.
/// </remarks>
public interface IObjectIdGenerator
{
    /// <summary>이 생성기가 발급하는 식별자의 노드 성분.</summary>
    int NodeId { get; }

    /// <summary>새 식별자를 발급한다.</summary>
    ObjectId NextId();
}
