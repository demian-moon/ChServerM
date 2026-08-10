using System;
using ChServerM.Content;

namespace ChServerM.Hosting;

/// <summary>
/// 서버가 <b>콘텐츠 지문 불일치</b>로 연결을 거부했을 때
/// <see cref="ChServerMClient.ConnectAsync"/> 가 던지는 예외 (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 <see cref="VersionNegotiationException"/> 을 재사용하지 않는가.</b> 두 실패가
/// 요구하는 <b>조치가 다르기 때문</b>이다 — 버전 거부는 "클라이언트(실행 파일)를 갱신하라"
/// 이고 지문 불일치는 "콘텐츠(데이터 파일)를 갱신하라" 다. 호출자가 그 둘을 구분해 서로 다른
/// 안내를 띄울 수 있어야 하며, 예외 타입이 그 구분의 가장 값싼 표현이다.
/// </para>
/// <para>
/// <b>서버의 지문은 실려 오지 않는다.</b> 실행 가능한 조치는 하나뿐이고, 불투명한 128비트
/// 값이 그 조치를 앞당기지 못한다. 서버가 무엇을 기대했는지는 <b>서버 로그</b>에 양쪽 값이
/// 함께 남는다 — 대조가 필요한 쪽은 운영자이지 클라이언트가 아니다.
/// </para>
/// </remarks>
public sealed class ContentFingerprintMismatchException : Exception
{
    /// <summary>메시지 없이 만든다.</summary>
    public ContentFingerprintMismatchException()
    {
    }

    /// <summary>메시지를 지정해 만든다.</summary>
    /// <param name="message">실패 설명.</param>
    public ContentFingerprintMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>메시지와 내부 예외를 지정해 만든다.</summary>
    /// <param name="message">실패 설명.</param>
    /// <param name="innerException">원인 예외.</param>
    public ContentFingerprintMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>클라이언트가 제시했던 지문과 함께 만든다.</summary>
    /// <param name="message">실패 설명.</param>
    /// <param name="offered">클라이언트가 보낸 지문.</param>
    public ContentFingerprintMismatchException(string message, ContentFingerprint offered)
        : base(message)
    {
        Offered = offered;
    }

    /// <summary>거부당한, 클라이언트가 제시한 지문. 로그·버그 리포트에 싣는 용도다.</summary>
    public ContentFingerprint Offered { get; }
}
