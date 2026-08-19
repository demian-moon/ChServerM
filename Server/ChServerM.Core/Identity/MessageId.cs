using System;
using System.Diagnostics;
using System.Globalization;

namespace ChServerM.Identity;

/// <summary>
/// 메시지(패킷) 타입을 가리키는 강타입 식별자.
/// </summary>
/// <remarks>
/// <para>ID 공간을 앱과 프레임워크로 나눈다.</para>
/// <list type="table">
///   <item><term>0</term><description><see cref="None"/> — 사용 금지. 초기화 누락을 잡는 센티넬</description></item>
///   <item><term>1 ~ 40000</term><description>앱이 자유롭게 정의</description></item>
///   <item><term>40001 ~ 65535</term><description>프레임워크 예약</description></item>
/// </list>
/// <para>
/// 레거시는 FlatBuffers가 기본값을 직렬화하지 않아 <c>0</c>을 쓰면 헤더 길이가 달라졌다.
/// 고정 헤더로 바꾸면서 그 제약은 사라졌지만, <c>0</c>은 여전히 <b>설정하지 않은 값</b>을 뜻하는
/// 센티넬로 남긴다.
/// </para>
/// <para>
/// <see cref="ISpanFormattable"/>·<see cref="IUtf8SpanFormattable"/>을 구현해 ZLogger 같은
/// 무할당 로깅 축과 보간 문자열 핸들러가 <b>문자열 할당 없이</b> 인라인 포맷할 수 있다
/// (감사 2026-08-18 C-4). 표기는 진단 전용 단일 형식이므로 format/provider 인자는 무시하며,
/// 출력은 <see cref="ToString()"/>과 문자·바이트 단위로 동일하다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct MessageId : IEquatable<MessageId>, IComparable<MessageId>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>앱이 쓸 수 있는 첫 번째 값.</summary>
    public const ushort AppRangeStart = 1;

    /// <summary>앱이 쓸 수 있는 마지막 값.</summary>
    public const ushort AppRangeEnd = 40000;

    /// <summary>프레임워크가 예약한 첫 번째 값.</summary>
    public const ushort FrameworkRangeStart = 40001;

    private readonly ushort _value;

    /// <summary>수치로 메시지 식별자를 만든다.</summary>
    public MessageId(ushort value) => _value = value;

    /// <summary>설정되지 않은 값.</summary>
    public static MessageId None => default;

    /// <summary>원본 수치.</summary>
    public ushort Value => _value;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _value == 0;

    /// <summary>앱 예약 범위에 속하는지 여부.</summary>
    public bool IsAppRange => _value is >= AppRangeStart and <= AppRangeEnd;

    /// <summary>프레임워크 예약 범위에 속하는지 여부.</summary>
    public bool IsFrameworkRange => _value >= FrameworkRangeStart;

    /// <inheritdoc />
    public bool Equals(MessageId other) => _value == other._value;

    /// <inheritdoc />
    public int CompareTo(MessageId other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MessageId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value;

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(MessageId left, MessageId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(MessageId left, MessageId right) => !left.Equals(right);

    /// <summary>왼쪽 번호가 더 작은지 비교한다.</summary>
    public static bool operator <(MessageId left, MessageId right) => left._value < right._value;

    /// <summary>왼쪽 번호가 더 큰지 비교한다.</summary>
    public static bool operator >(MessageId left, MessageId right) => left._value > right._value;

    /// <summary>왼쪽 번호가 같거나 더 작은지 비교한다.</summary>
    public static bool operator <=(MessageId left, MessageId right) => left._value <= right._value;

    /// <summary>왼쪽 번호가 같거나 더 큰지 비교한다.</summary>
    public static bool operator >=(MessageId left, MessageId right) => left._value >= right._value;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"msg:{_value}");

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
        ReadOnlySpan<char> prefix = "msg:";
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
        ReadOnlySpan<byte> prefix = "msg:"u8;
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

/// <summary>프레임워크가 예약한 메시지 식별자.</summary>
/// <remarks>앱은 <see cref="MessageId.AppRangeStart"/>~<see cref="MessageId.AppRangeEnd"/>를 쓴다.</remarks>
public static class FrameworkMessageIds
{
    /// <summary>연결 유지 확인 요청.</summary>
    public static MessageId Heartbeat => new(40001);

    /// <summary>연결 유지 확인 응답.</summary>
    public static MessageId HeartbeatAck => new(40002);

    /// <summary>정상 종료 요청.</summary>
    public static MessageId DisconnectRequest => new(40003);

    /// <summary>서버가 연결을 거부했음을 알리는 통지. 페이로드에 사유가 실릴 수 있다.</summary>
    /// <remarks>
    /// 동시 접속 상한 등으로 수락 직후 닫을 때, 그냥 끊으면 클라이언트는 RST 하나만 보고
    /// "서버가 꽉 찼다"와 "네트워크가 끊겼다"를 구분할 수 없어 재시도 정책을 세울 수 없다
    /// (Phase 10 과부하 제어와 연결). 이 ID 의 프레임을 최선 노력으로 보낸 뒤 닫는다.
    /// 버전 협상의 거부 통지도 이 ID 를 재사용한다(R-3) — 그 경우의 페이로드는
    /// <see cref="ChServerM.Handshake.VersionHandshakeCodec"/> 의 동결 레이아웃이다.
    /// </remarks>
    public static MessageId ConnectionRejected => new(40004);

    /// <summary>버전 협상 개시 — 클라이언트가 지원 구간 <c>[Min, Max]</c> 를 제시한다 (ADR-0017).</summary>
    /// <remarks>
    /// 커넥션(보안 축이 있으면 그 채널 안)의 <b>첫 프레임</b>이어야 하며, 와이어 형식은
    /// <see cref="ChServerM.Handshake.VersionHandshakeCodec"/> 가 영구 동결한다 —
    /// 협상 이전에는 합의된 버전이 없으므로 이 프레임만은 어느 축에도 얹지 않는다(R-2).
    /// </remarks>
    public static MessageId ClientHello => new(40005);

    /// <summary>버전 협상 확정 — 서버가 교집합 최고 버전을 통보한다 (ADR-0017).</summary>
    /// <remarks>
    /// 교집합이 없으면 이 대신 <see cref="ConnectionRejected"/> 를 보내고 닫는다.
    /// 와이어 형식은 <see cref="ChServerM.Handshake.VersionHandshakeCodec"/> 참조.
    /// </remarks>
    public static MessageId ServerHello => new(40006);

    /// <summary>세션 재개 요청 — 클라이언트가 세션 식별자와 재개 토큰을 제시한다 (ADR-0036).</summary>
    /// <remarks>
    /// 와이어 형식은 <see cref="ChServerM.Sessions.SessionHandshakeCodec"/> 가 동결한다.
    /// 자격 판정과 토큰 회전은 서버가 하며, <b>실패 사유를 구분해 답하지 않는다</b> —
    /// 구분하면 공격자가 실재하는 세션 식별자를 열거할 수 있다.
    /// </remarks>
    public static MessageId SessionResume => new(40007);

    /// <summary>세션 재개 응답 — 성공 여부와 <b>회전된</b> 재개 토큰.</summary>
    /// <remarks>
    /// <b>성공·실패의 페이로드 길이가 같다</b>(실패 시 토큰 자리는 0). 길이 차이는
    /// 상태 바이트를 읽지 않고도 결과를 알려 주는 부수 채널이 된다.
    /// </remarks>
    public static MessageId SessionResumed => new(40008);

    /// <summary>세션 수립 통지 — 서버가 새 세션의 식별자와 최초 재개 토큰을 알린다.</summary>
    /// <remarks>
    /// <b>수립 여부는 앱이 정한다</b>(인증 정책은 앱의 몫이다). 프레임워크는 그 결과를
    /// 전달하는 이 메시지와, 이후의 재개 흐름 전체를 제공한다.
    /// </remarks>
    public static MessageId SessionEstablished => new(40009);

    /// <summary>콘텐츠 지문 제시 — 클라이언트가 자기 콘텐츠의 지문을 보낸다 (ADR-0044).</summary>
    /// <remarks>
    /// <b>버전 협상 프레임에 필드를 더하는 대신 ID 를 새로 예약했다.</b>
    /// <see cref="ClientHello"/> 페이로드는 영구 동결이라 늘릴 수 없다(R-2).
    /// 와이어 형식은 <see cref="ChServerM.Content.ContentFingerprintCodec"/> 가 동결한다.
    /// </remarks>
    public static MessageId ContentOffer => new(40010);

    /// <summary>콘텐츠 지문 수락 — 서버가 일치를 확인했다. 페이로드는 비어 있다.</summary>
    /// <remarks>
    /// <b>실을 정보가 없어도 프레임을 보낸다.</b> 침묵으로 수락을 표현하면 클라이언트가
    /// "수락됐다" 와 "아직 안 왔다" 를 구분할 수 없어 제한 시간까지 기다리게 된다.
    /// 불일치는 <see cref="ConnectionRejected"/> 를 재사용한다 — 사유 코드만 다르다.
    /// </remarks>
    public static MessageId ContentAccepted => new(40011);
}
