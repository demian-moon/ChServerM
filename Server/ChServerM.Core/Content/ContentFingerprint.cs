using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace ChServerM.Content;

/// <summary>
/// 클라이언트와 서버가 <b>같은 콘텐츠를 보고 있는지</b> 대조하는 128비트 지문.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 콘텐츠(밸런스 표·로컬라이즈 문자열·에셋 매니페스트)가 어긋난 채로
/// 접속하면 증상이 <b>한참 뒤에 엉뚱한 모습</b>으로 나타난다. 접속 시점에 값 하나를
/// 대조하면 그 전부가 <b>연결 수립 실패</b>라는 명확한 사건이 된다.
/// </para>
///
/// <para>
/// <b>⚠ Core 는 이 값이 무엇의 지문인지 모른다.</b> 데이터 테이블은 <b>선택 축</b>이고
/// Core 는 그 존재를 알지 않는다(CLAUDE.md 3절). 그래서 이름도 계약도 "콘텐츠" 이지
/// "테이블" 이 아니다 — <b>이 일반화는 설계 취향이 아니라 하드 룰이 강제한 것</b>이다.
/// 지문을 만드는 쪽이 무엇이든, 여기서는 불투명한 128비트다.
/// </para>
///
/// <para>
/// <b>⚠ 인증이 아니다.</b> 이것은 <b>사고를 막는 장치</b>이지 <b>공격을 막는 장치</b>가
/// 아니다. 지문을 위조한 클라이언트를 막는 것은 인증·서명의 문제이며, 비암호 지문으로는
/// 답이 되지 않는다. 위협 모델에서 이 게이트는 <b>무결성 보증이 아니라 운영 사고 방지</b>로
/// 분류한다.
/// </para>
///
/// <para>
/// <b><see cref="None"/> 은 "설정되지 않음" 이다.</b> 0 을 유효한 지문으로 쓰지 않는다 —
/// 빈 버퍼나 초기화되지 않은 필드가 <b>우연히 일치</b>하는 것을 막는다. 게이트를 켜 놓고
/// 지문을 넣지 않는 조립 실수는 시작 시점에 걸러야 한다.
/// </para>
///
/// <para><b>스레드 규약.</b> 불변 값 타입이다.</para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>을 구현해 ZLogger 같은
/// 무할당 로깅 축과 보간 문자열 핸들러가 <b>문자열 할당 없이</b> 인라인 포맷할 수 있다
/// (감사 2026-08-18 C-4). 표기는 진단 전용 단일 형식(설정 시 16진 32자리, 아니면
/// <c>(none)</c>)이므로 format/provider 인자는 무시하며, 출력은 <see cref="ToString()"/>과
/// 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ContentFingerprint : IEquatable<ContentFingerprint>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>지문의 와이어 바이트 길이. 영구 동결.</summary>
    public const int ByteLength = 16;

    /// <summary>상위·하위 64비트로 만든다.</summary>
    /// <param name="high">상위 64비트.</param>
    /// <param name="low">하위 64비트.</param>
    public ContentFingerprint(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>상위 64비트.</summary>
    public ulong High { get; }

    /// <summary>하위 64비트.</summary>
    public ulong Low { get; }

    /// <summary>설정되지 않음을 뜻하는 센티넬.</summary>
    public static ContentFingerprint None => default;

    /// <summary>실제 지문이 설정됐는가.</summary>
    public bool IsSet => High != 0 || Low != 0;

    /// <summary>지문을 16바이트로 쓴다(리틀 엔디언, 하위 → 상위). 와이어 표현은 영구 동결.</summary>
    /// <param name="destination"><see cref="ByteLength"/> 바이트 이상.</param>
    /// <exception cref="ArgumentException">대상이 짧다.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
        {
            throw new ArgumentException($"대상은 {ByteLength} 바이트 이상이어야 한다.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, Low);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], High);
    }

    /// <summary>16바이트에서 지문을 읽는다.</summary>
    /// <param name="source"><see cref="ByteLength"/> 바이트 이상.</param>
    /// <returns>읽은 지문.</returns>
    /// <exception cref="ArgumentException">원본이 짧다.</exception>
    public static ContentFingerprint ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < ByteLength)
        {
            throw new ArgumentException($"원본은 {ByteLength} 바이트 이상이어야 한다.", nameof(source));
        }

        return new ContentFingerprint(
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source));
    }

    /// <inheritdoc/>
    public bool Equals(ContentFingerprint other) => High == other.High && Low == other.Low;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ContentFingerprint other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(High, Low);

    /// <summary>32자리 16진 표현. 로그와 배포 스크립트가 눈으로 대조할 수 있어야 한다.</summary>
    /// <returns>16진 문자열. 설정되지 않았으면 <c>(none)</c>.</returns>
    public override string ToString() =>
        IsSet ? string.Create(CultureInfo.InvariantCulture, $"{High:x16}{Low:x16}") : "(none)";

    /// <summary>두 지문이 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>같으면 <see langword="true"/>.</returns>
    public static bool operator ==(ContentFingerprint left, ContentFingerprint right) => left.Equals(right);

    /// <summary>두 지문이 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>다르면 <see langword="true"/>.</returns>
    public static bool operator !=(ContentFingerprint left, ContentFingerprint right) => !left.Equals(right);

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
        if (!IsSet)
        {
            ReadOnlySpan<char> none = "(none)";
            if (!none.TryCopyTo(destination))
            {
                return false;
            }

            charsWritten = none.Length;
            return true;
        }

        // x16 은 항상 정확히 16자리다 — High 가 성공하면 [16..] 에 Low 를 이어 쓴다.
        if (!High.TryFormat(destination, out int highWritten, "x16", CultureInfo.InvariantCulture)
            || !Low.TryFormat(destination[highWritten..], out int lowWritten, "x16", CultureInfo.InvariantCulture))
        {
            return false;
        }

        charsWritten = highWritten + lowWritten;
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
        if (!IsSet)
        {
            ReadOnlySpan<byte> none = "(none)"u8;
            if (!none.TryCopyTo(utf8Destination))
            {
                return false;
            }

            bytesWritten = none.Length;
            return true;
        }

        if (!High.TryFormat(utf8Destination, out int highWritten, "x16", CultureInfo.InvariantCulture)
            || !Low.TryFormat(utf8Destination[highWritten..], out int lowWritten, "x16", CultureInfo.InvariantCulture))
        {
            return false;
        }

        bytesWritten = highWritten + lowWritten;
        return true;
    }
}
