using System;
using ChServerM.Handshake;

namespace ChServerM.Hosting;

/// <summary>
/// 클라이언트의 버전 협상이 실패했을 때 <see cref="ChServerMClient.ConnectAsync"/> 가 던지는 예외.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 연결 수립은 호출자 대면 경로라 실패가 예외로 나가는데(보안 축의
/// <c>AuthenticationException</c> 과 같은 원칙), 버전 거부에는 맞는 BCL 예외가 없다.
/// 호출자는 이 예외를 잡아 "클라이언트 업데이트가 필요하다"를 사용자에게 알린다 —
/// <see cref="ServerSupportedVersions"/> 가 그 근거다(R-3: 거부에는 사유가 실린다).
/// </para>
/// <para>
/// 서버가 거부 사유를 보내지 않고 끊었거나(형식 위반·시간 초과) 응답을 해석할 수 없으면
/// <see cref="ServerSupportedVersions"/> 는 <see langword="null"/> 이다.
/// </para>
/// </remarks>
public sealed class VersionNegotiationException : Exception
{
    /// <summary>메시지 없이 만든다.</summary>
    public VersionNegotiationException()
    {
    }

    /// <summary>메시지를 지정해 만든다.</summary>
    /// <param name="message">실패 설명.</param>
    public VersionNegotiationException(string message)
        : base(message)
    {
    }

    /// <summary>메시지와 내부 예외를 지정해 만든다.</summary>
    /// <param name="message">실패 설명.</param>
    /// <param name="innerException">원인 예외.</param>
    public VersionNegotiationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>서버가 알려온 지원 구간과 함께 만든다.</summary>
    /// <param name="message">실패 설명.</param>
    /// <param name="serverSupportedVersions">서버의 지원 구간.</param>
    public VersionNegotiationException(string message, ProtocolVersionRange serverSupportedVersions)
        : base(message)
    {
        ServerSupportedVersions = serverSupportedVersions;
    }

    /// <summary>서버가 거부하며 알려온 지원 구간. 모르면 <see langword="null"/>.</summary>
    public ProtocolVersionRange? ServerSupportedVersions { get; }
}
