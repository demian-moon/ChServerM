using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ChServerM.Diagnostics;

/// <summary>
/// 로그 이벤트를 가리키는 안정 식별자.
/// </summary>
/// <remarks>
/// <para>
/// 숫자가 <b>정본</b>이고 이름은 사람이 읽기 위한 것이다. 메시지 문구를 바꿔도 숫자가 같으면
/// 같은 이벤트다 — 로그 검색·알람 규칙이 문구 변경에 깨지지 않는다.
/// </para>
/// <para>
/// 레거시는 로그를 문자열 보간으로만 남겨 <b>기계가 집계할 수 있는 축이 하나도 없었다.</b>
/// </para>
/// <para>번호 대역은 <see cref="DiagnosticNames"/>에 정리한다.</para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>을 구현해 ZLogger 같은
/// 무할당 로깅 축과 보간 문자열 핸들러가 <b>문자열 할당 없이</b> 인라인 포맷할 수 있다
/// (감사 2026-08-18 C-4). 표기는 진단 전용 단일 형식(이름이 있으면 이름, 없으면 번호)이므로
/// format/provider 인자는 무시하며, 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct EventId : IEquatable<EventId>, ISpanFormattable, IUtf8SpanFormattable
{
    private readonly int _id;
    private readonly string? _name;

    /// <summary>번호와 이름으로 이벤트 식별자를 만든다.</summary>
    /// <param name="id">안정 번호. 한 번 배정하면 바꾸지 않는다.</param>
    /// <param name="name">사람이 읽는 이름. 로그 문구와 별개다.</param>
    public EventId(int id, string? name = null)
    {
        _id = id;
        _name = name;
    }

    /// <summary>안정 번호.</summary>
    public int Id => _id;

    /// <summary>사람이 읽는 이름. 지정하지 않았으면 <see langword="null"/>.</summary>
    public string? Name => _name;

    /// <inheritdoc />
    /// <remarks>이름은 비교하지 않는다. 정본은 번호다.</remarks>
    public bool Equals(EventId other) => _id == other._id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EventId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _id;

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(EventId left, EventId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(EventId left, EventId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        _name ?? _id.ToString(CultureInfo.InvariantCulture);

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
        if (_name is not null)
        {
            charsWritten = 0;
            if (!_name.TryCopyTo(destination))
            {
                return false;
            }

            charsWritten = _name.Length;
            return true;
        }

        return _id.TryFormat(destination, out charsWritten, default, CultureInfo.InvariantCulture);
    }

    /// <summary>진단 표기를 UTF-8 버퍼에 쓴다. 출력은 <see cref="ToString()"/>의 UTF-8 인코딩과 동일하다.</summary>
    /// <param name="utf8Destination">쓸 버퍼.</param>
    /// <param name="bytesWritten">성공 시 쓴 바이트 수. 실패 시 0.</param>
    /// <param name="format">무시한다 — 진단 전용 단일 표기다.</param>
    /// <param name="provider">무시한다 — 표기는 항상 인바리언트다.</param>
    /// <returns>버퍼가 충분하면 <see langword="true"/>.</returns>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_name is not null)
        {
            // 이름은 임의 문자열일 수 있다 — UTF-8 트랜스코딩은 Encoding.TryGetBytes 가
            // 전부-아니면-전무(all-or-nothing)로 처리한다 (실패 시 bytesWritten = 0).
            return Encoding.UTF8.TryGetBytes(_name, utf8Destination, out bytesWritten);
        }

        return _id.TryFormat(utf8Destination, out bytesWritten, default, CultureInfo.InvariantCulture);
    }
}
