using System;
using System.Diagnostics;
using System.Globalization;

namespace ChServerM.Identity;

/// <summary>
/// 커넥션을 가리키는 강타입 핸들.
/// </summary>
/// <remarks>
/// <para>
/// 슬롯 번호와 <b>세대(generation)</b>를 함께 담는다. 커넥션 슬롯은 재사용되므로,
/// 세대가 없으면 이미 끊긴 커넥션의 ID가 <b>같은 슬롯을 차지한 새 커넥션으로 해석</b>된다.
/// 세대를 비교하면 그 오해석을 할당 없이 O(1)로 차단할 수 있다.
/// </para>
/// <para>
/// 레거시는 <c>TcpClient</c> 객체 자체를 딕셔너리 키로 썼다. 참조 동일성이라 오해석은 없었지만
/// 리소스 객체가 키가 되어 수명이 얽혔고, 조회할 때마다 래퍼를 새로 할당했다.
/// </para>
/// <para>이 타입은 <b>영속화하지 않는다.</b> 프로세스 수명 안에서만 유효하다.</para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>을 구현해 ZLogger 같은
/// 무할당 로깅 축과 보간 문자열 핸들러가 <b>문자열 할당 없이</b> 인라인 포맷할 수 있다
/// (감사 2026-08-18 C-4). 표기는 진단 전용 단일 형식이므로 format/provider 인자는 무시하며,
/// 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ConnectionId : IEquatable<ConnectionId>, ISpanFormattable, IUtf8SpanFormattable
{
    private readonly uint _slot;
    private readonly uint _generation;

    /// <summary>유효하지 않은 커넥션을 나타내는 값.</summary>
    public static ConnectionId None => default;

    /// <summary>슬롯과 세대로 커넥션 핸들을 만든다.</summary>
    /// <param name="slot">커넥션 테이블의 슬롯 번호.</param>
    /// <param name="generation">해당 슬롯의 세대. 슬롯이 재사용될 때마다 증가한다. 0은 빈 슬롯을 뜻하므로 쓰지 않는다.</param>
    public ConnectionId(uint slot, uint generation)
    {
        _slot = slot;
        _generation = generation;
    }

    /// <summary>커넥션 테이블에서의 슬롯 번호.</summary>
    public uint Slot => _slot;

    /// <summary>슬롯의 세대. 재사용을 구분한다.</summary>
    public uint Generation => _generation;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _generation == 0;

    /// <summary>파티션 배정에 쓸 안정 해시 키를 만든다.</summary>
    /// <remarks>
    /// 슬롯만으로 파티션을 정한다. 같은 슬롯을 재사용하는 새 커넥션이 같은 파티션에 배정되어
    /// 파티션 간 이동이 생기지 않는다.
    /// </remarks>
    public PartitionKey ToPartitionKey() => PartitionKey.FromValue(_slot);

    /// <inheritdoc />
    public bool Equals(ConnectionId other) => _slot == other._slot && _generation == other._generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ConnectionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_slot, _generation);

    /// <summary>두 핸들이 같은지 비교한다.</summary>
    public static bool operator ==(ConnectionId left, ConnectionId right) => left.Equals(right);

    /// <summary>두 핸들이 다른지 비교한다.</summary>
    public static bool operator !=(ConnectionId left, ConnectionId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        IsNone
            ? "conn:none"
            : string.Create(CultureInfo.InvariantCulture, $"conn:{_slot}/{_generation}");

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
        if (IsNone)
        {
            ReadOnlySpan<char> none = "conn:none";
            if (!none.TryCopyTo(destination))
            {
                return false;
            }

            charsWritten = none.Length;
            return true;
        }

        ReadOnlySpan<char> prefix = "conn:";
        if (!prefix.TryCopyTo(destination))
        {
            return false;
        }

        int pos = prefix.Length;
        if (!_slot.TryFormat(destination[pos..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        pos += written;
        if ((uint)pos >= (uint)destination.Length)
        {
            return false;
        }

        destination[pos++] = '/';
        if (!_generation.TryFormat(destination[pos..], out written, default, CultureInfo.InvariantCulture))
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
        if (IsNone)
        {
            ReadOnlySpan<byte> none = "conn:none"u8;
            if (!none.TryCopyTo(utf8Destination))
            {
                return false;
            }

            bytesWritten = none.Length;
            return true;
        }

        ReadOnlySpan<byte> prefix = "conn:"u8;
        if (!prefix.TryCopyTo(utf8Destination))
        {
            return false;
        }

        int pos = prefix.Length;
        if (!_slot.TryFormat(utf8Destination[pos..], out int written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        pos += written;
        if ((uint)pos >= (uint)utf8Destination.Length)
        {
            return false;
        }

        utf8Destination[pos++] = (byte)'/';
        if (!_generation.TryFormat(utf8Destination[pos..], out written, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        bytesWritten = pos + written;
        return true;
    }
}
