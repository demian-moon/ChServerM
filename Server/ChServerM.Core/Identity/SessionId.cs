using System;
using System.Diagnostics;
using System.Globalization;

namespace ChServerM.Identity;

/// <summary>
/// 세션을 가리키는 강타입 식별자.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConnectionId"/>와 구분한다. 커넥션은 끊겼다 다시 붙을 수 있지만
/// 세션은 재접속을 가로질러 유지될 수 있다. 그래서 세션 식별자는 <b>노드를 넘어 안정</b>해야 하고
/// 영속화·로그에 남을 수 있어야 한다 — <see cref="ObjectId"/>를 기반으로 삼는 이유다.
/// </para>
/// <para>
/// 세션 저장소의 빠른 조회는 별도 슬롯 핸들로 처리한다. 이 타입은 <b>안정 식별자</b> 역할만 한다.
/// </para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>을 구현해 ZLogger 같은
/// 무할당 로깅 축과 보간 문자열 핸들러가 <b>문자열 할당 없이</b> 인라인 포맷할 수 있다
/// (감사 2026-08-18 C-4). 표기는 진단 전용 단일 형식이므로 format/provider 인자는 무시하며,
/// 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct SessionId : IEquatable<SessionId>, ISpanFormattable, IUtf8SpanFormattable
{
    private readonly ObjectId _value;

    /// <summary><see cref="ObjectId"/>로 세션 식별자를 만든다.</summary>
    public SessionId(ObjectId value) => _value = value;

    /// <summary>설정되지 않은 값.</summary>
    public static SessionId None => default;

    /// <summary>기반 <see cref="ObjectId"/>.</summary>
    public ObjectId Value => _value;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _value.IsNone;

    /// <summary>파티션 배정에 쓸 안정 해시 키를 만든다.</summary>
    public PartitionKey ToPartitionKey() => _value.ToPartitionKey();

    /// <inheritdoc />
    public bool Equals(SessionId other) => _value.Equals(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SessionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(SessionId left, SessionId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(SessionId left, SessionId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"sess:{_value.Value}");

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
        ReadOnlySpan<char> prefix = "sess:";
        if (!prefix.TryCopyTo(destination))
        {
            return false;
        }

        if (!_value.Value.TryFormat(destination[prefix.Length..], out int written, default, CultureInfo.InvariantCulture))
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
        ReadOnlySpan<byte> prefix = "sess:"u8;
        if (!prefix.TryCopyTo(utf8Destination))
        {
            return false;
        }

        if (!_value.Value.TryFormat(utf8Destination[prefix.Length..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        bytesWritten = prefix.Length + written;
        return true;
    }
}

/// <summary>
/// 예약된 작업을 가리키는 강타입 식별자.
/// </summary>
/// <remarks>
/// <para>
/// <b>소유자 범위로 한정된다.</b> 같은 키 문자열을 쓰는 서로 다른 오브젝트가 충돌하지 않도록
/// 소유자 식별자를 함께 담는다.
/// </para>
/// <para>
/// 레거시 <c>HashM</c>은 오브젝트 스코프 키(<c>"buff_speed"</c> 등)를 전역 스케줄러의
/// 문자열 작업 ID로 그대로 넘겼다. 두 오브젝트가 같은 키를 쓰면 두 번째부터 등록이 실패해
/// <b>만료가 조용히 동작하지 않았다.</b>
/// </para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>은 무할당 로깅 경로와의
/// 정합을 위한 것이다(감사 2026-08-18 C-4). format/provider 인자는 무시하며,
/// 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct JobId : IEquatable<JobId>, ISpanFormattable, IUtf8SpanFormattable
{
    private readonly ulong _owner;
    private readonly ulong _local;

    /// <summary>소유자와 지역 번호로 작업 식별자를 만든다.</summary>
    /// <param name="owner">작업을 소유한 주체(세션·오브젝트 등)의 식별자.</param>
    /// <param name="local">소유자 안에서 유일한 번호.</param>
    public JobId(ulong owner, ulong local)
    {
        _owner = owner;
        _local = local;
    }

    /// <summary>설정되지 않은 값.</summary>
    public static JobId None => default;

    /// <summary>작업을 소유한 주체의 식별자.</summary>
    public ulong Owner => _owner;

    /// <summary>소유자 안에서의 지역 번호.</summary>
    public ulong Local => _local;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _owner == 0 && _local == 0;

    /// <inheritdoc />
    public bool Equals(JobId other) => _owner == other._owner && _local == other._local;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JobId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_owner, _local);

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(JobId left, JobId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(JobId left, JobId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"job:{_owner}/{_local}");

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
        ReadOnlySpan<char> prefix = "job:";
        if (!prefix.TryCopyTo(destination))
        {
            return false;
        }

        int pos = prefix.Length;
        if (!_owner.TryFormat(destination[pos..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        pos += written;
        if ((uint)pos >= (uint)destination.Length)
        {
            return false;
        }

        destination[pos++] = '/';
        if (!_local.TryFormat(destination[pos..], out written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        charsWritten = pos + written;
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
        ReadOnlySpan<byte> prefix = "job:"u8;
        if (!prefix.TryCopyTo(utf8Destination))
        {
            return false;
        }

        int pos = prefix.Length;
        if (!_owner.TryFormat(utf8Destination[pos..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        pos += written;
        if ((uint)pos >= (uint)utf8Destination.Length)
        {
            return false;
        }

        utf8Destination[pos++] = (byte)'/';
        if (!_local.TryFormat(utf8Destination[pos..], out written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        bytesWritten = pos + written;
        return true;
    }
}

/// <summary>클러스터 노드를 가리키는 강타입 식별자. <b>번호 0은 센티넬로 예약된다.</b></summary>
/// <remarks>
/// <para><see cref="ObjectId.MaxNodeId"/> 이하여야 <see cref="ObjectId"/>에 담을 수 있다.</para>
/// <para>
/// <b>번호 0을 유효한 노드로 쓰지 않는다.</b> <see cref="None"/>(=0)과 "0번 노드"가 같은
/// 값이면 노드 번호 미기입 실수가 유효한 구성으로 통과한다 — 형제 ID 타입들과 같은
/// "미설정은 가장 제한적" 규약이다(감사 2026-08-18 C-6, 결정: 노드 번호는 1부터).
/// 생성자가 0을 거부하므로 0인 인스턴스는 <see langword="default"/>(=<see cref="None"/>)뿐이다.
/// </para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>은 무할당 로깅 경로와의
/// 정합을 위한 것이다(감사 2026-08-18 C-4). format/provider 인자는 무시하며,
/// 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다(센티넬도 <c>node:0</c> 그대로).
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct NodeId : IEquatable<NodeId>, ISpanFormattable, IUtf8SpanFormattable
{
    private readonly ushort _value;

    /// <summary>수치로 노드 식별자를 만든다. 유효 범위는 1~<see cref="ObjectId.MaxNodeId"/>다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/>가 0(<see cref="None"/> 센티넬로 예약)이거나
    /// <see cref="ObjectId.MaxNodeId"/>를 넘을 때.
    /// </exception>
    public NodeId(ushort value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ObjectId.MaxNodeId);
        _value = value;
    }

    /// <summary>설정되지 않은 값. 관측되면 노드 번호 미기입이다 — 유효한 노드가 아니다.</summary>
    public static NodeId None => default;

    /// <summary>설정되지 않은 값인지 여부.</summary>
    public bool IsNone => _value == 0;

    /// <summary>원본 수치.</summary>
    public ushort Value => _value;

    /// <inheritdoc />
    public bool Equals(NodeId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value;

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"node:{_value}");

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
        ReadOnlySpan<char> prefix = "node:";
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
        ReadOnlySpan<byte> prefix = "node:"u8;
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
