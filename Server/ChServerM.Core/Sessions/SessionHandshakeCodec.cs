using System;
using System.Buffers.Binary;

namespace ChServerM.Sessions;

/// <summary>세션 재개 응답의 상태.</summary>
/// <remarks>
/// <b>⚠ 실패를 하나로만 표현한다.</b> "세션이 없다" 와 "토큰이 틀렸다" 를 구분해 주면
/// 공격자가 어떤 세션 식별자가 실재하는지 열거할 수 있다(ADR-0036).
/// </remarks>
public enum SessionResumeStatus : byte
{
    /// <summary>쓰이지 않는 값. 0 을 유효 상태로 두지 않아 빈 버퍼를 성공으로 오독하지 않는다.</summary>
    Unspecified = 0,

    /// <summary>재개 성공. 회전된 토큰이 함께 온다.</summary>
    Resumed = 1,

    /// <summary>재개 거부. <b>사유는 알려 주지 않는다.</b></summary>
    Rejected = 2,
}

/// <summary>
/// 세션 수립·재개 메시지의 <b>동결된</b> 와이어 형식.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 재개는 프레임워크의 메커니즘이므로(ADR-0036) 클라이언트와 서버가
/// <b>합의된 형식</b>을 공유해야 한다. <see cref="ChServerM.Handshake.VersionHandshakeCodec"/>
/// 와 같은 선례를 따른다 — 프레임워크가 정의하는 프로토콜은 Core 에 동결한다.
/// </para>
///
/// <para>
/// <b>⚠ 이 레이아웃은 영구 동결이다.</b> 클라이언트와 서버가 서로 다른 버전으로 배포될 수
/// 있으므로 필드를 재해석하거나 순서를 바꾸지 않는다. 바꿔야 하면 <b>새 메시지 ID</b>를
/// 예약한다.
/// </para>
///
/// <para>
/// <b>⚠ 왜 여기는 토큰 타입을 쓰지 않고 raw 스팬인가.</b> 재개 토큰 타입
/// (<c>ChServerM.Hosting.Sessions.SessionResumeToken</c>)은 암호 연산을 수행해 Core 에 둘 수
/// 없다(ADR-0036). 반면 <b>와이어 형식은 바이트를 옮기는 일</b>이라 암호가 필요 없다 —
/// 그래서 코덱은 Core 에 두고 스팬만 다룬다. 이 분리 덕분에 클라이언트 어셈블리도 Core 만
/// 참조해 프로토콜을 말할 수 있다.
/// </para>
///
/// <para>
/// <b>⚠ 성공과 실패의 응답 길이가 같다.</b> 실패 시 토큰 자리는 0 으로 채운다. 길이가
/// 다르면 상태 바이트를 읽지 않고도 결과를 알 수 있어 부수 채널이 된다.
/// </para>
///
/// <para><b>스레드 규약.</b> 상태가 없는 정적 코덱이다. 어디서든 호출할 수 있다.</para>
/// </remarks>
public static class SessionHandshakeCodec
{
    /// <summary>재개 토큰의 바이트 길이. 토큰 타입의 길이와 반드시 같아야 한다.</summary>
    public const int TokenLength = 32;

    /// <summary>세션 식별자의 와이어 길이(<c>ObjectId</c> = 64비트).</summary>
    public const int SessionIdLength = 8;

    /// <summary><see cref="ChServerM.Identity.FrameworkMessageIds.SessionResume"/> 페이로드 크기.</summary>
    public const int ResumeRequestSize = SessionIdLength + TokenLength;

    /// <summary><see cref="ChServerM.Identity.FrameworkMessageIds.SessionResumed"/> 페이로드 크기.</summary>
    public const int ResumeResponseSize = 1 + TokenLength;

    /// <summary><see cref="ChServerM.Identity.FrameworkMessageIds.SessionEstablished"/> 페이로드 크기.</summary>
    public const int EstablishedSize = SessionIdLength + TokenLength;

    private const int TokenOffsetInRequest = SessionIdLength;
    private const int StatusOffset = 0;
    private const int TokenOffsetInResponse = 1;

    /// <summary>재개 요청을 쓴다.</summary>
    /// <param name="destination">길이 <see cref="ResumeRequestSize"/> 이상.</param>
    /// <param name="sessionId">세션 식별자의 원시 값.</param>
    /// <param name="token">재개 토큰(<see cref="TokenLength"/> 바이트).</param>
    /// <exception cref="ArgumentException">대상이 짧거나 토큰 길이가 다르다.</exception>
    public static void WriteResumeRequest(Span<byte> destination, long sessionId, ReadOnlySpan<byte> token)
    {
        if (destination.Length < ResumeRequestSize)
        {
            throw new ArgumentException($"대상은 {ResumeRequestSize} 바이트 이상이어야 한다.", nameof(destination));
        }

        if (token.Length != TokenLength)
        {
            throw new ArgumentException($"토큰은 정확히 {TokenLength} 바이트여야 한다.", nameof(token));
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination, sessionId);
        token.CopyTo(destination.Slice(TokenOffsetInRequest, TokenLength));
    }

    /// <summary>재개 요청을 읽는다.</summary>
    /// <param name="payload">받은 페이로드.</param>
    /// <param name="sessionId">세션 식별자의 원시 값.</param>
    /// <param name="token">토큰을 받을 버퍼(<see cref="TokenLength"/> 바이트 이상).</param>
    /// <returns>형식이 맞으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>길이가 정확히 맞아야 한다.</b> 더 긴 페이로드를 관대하게 받으면 뒤에 붙은
    /// 바이트가 어디로도 검증되지 않은 채 흘러 들어온다.
    /// </remarks>
    public static bool TryReadResumeRequest(ReadOnlySpan<byte> payload, out long sessionId, Span<byte> token)
    {
        sessionId = 0;

        if (payload.Length != ResumeRequestSize || token.Length < TokenLength)
        {
            return false;
        }

        sessionId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        payload.Slice(TokenOffsetInRequest, TokenLength).CopyTo(token[..TokenLength]);
        return true;
    }

    /// <summary>재개 응답을 쓴다.</summary>
    /// <param name="destination">길이 <see cref="ResumeResponseSize"/> 이상.</param>
    /// <param name="status">결과.</param>
    /// <param name="rotatedToken">
    /// 성공 시 회전된 토큰. 실패면 빈 스팬을 넘긴다 — 토큰 자리는 0 으로 채워진다.
    /// </param>
    /// <exception cref="ArgumentException">대상이 짧거나 토큰 길이가 잘못됐다.</exception>
    public static void WriteResumeResponse(
        Span<byte> destination, SessionResumeStatus status, ReadOnlySpan<byte> rotatedToken)
    {
        if (destination.Length < ResumeResponseSize)
        {
            throw new ArgumentException($"대상은 {ResumeResponseSize} 바이트 이상이어야 한다.", nameof(destination));
        }

        if (rotatedToken.Length is not (0 or TokenLength))
        {
            throw new ArgumentException($"토큰은 비었거나 정확히 {TokenLength} 바이트여야 한다.", nameof(rotatedToken));
        }

        destination[StatusOffset] = (byte)status;

        Span<byte> tokenSlot = destination.Slice(TokenOffsetInResponse, TokenLength);
        if (rotatedToken.IsEmpty)
        {
            // ⚠ 실패해도 같은 길이를 보낸다 — 길이 차이가 부수 채널이 되지 않게.
            tokenSlot.Clear();
        }
        else
        {
            rotatedToken.CopyTo(tokenSlot);
        }
    }

    /// <summary>재개 응답을 읽는다.</summary>
    /// <param name="payload">받은 페이로드.</param>
    /// <param name="status">결과.</param>
    /// <param name="rotatedToken">토큰을 받을 버퍼(<see cref="TokenLength"/> 바이트 이상).</param>
    /// <returns>형식이 맞고 상태가 정의된 값이면 <see langword="true"/>.</returns>
    public static bool TryReadResumeResponse(
        ReadOnlySpan<byte> payload, out SessionResumeStatus status, Span<byte> rotatedToken)
    {
        status = SessionResumeStatus.Unspecified;

        if (payload.Length != ResumeResponseSize || rotatedToken.Length < TokenLength)
        {
            return false;
        }

        // ⚠ 정의되지 않은 상태 바이트를 성공으로 통과시키지 않는다 — "부트스트랩에 관대한
        //   수신은 없다"(VersionHandshakeCodec 와 같은 원칙). 0(Unspecified)도 거부한다:
        //   Unspecified 는 "빈 버퍼를 성공으로 오독하지 않기 위한" 송신 금지 센티넬이다
        //   (감사 2026-08-18 C-5).
        byte rawStatus = payload[StatusOffset];
        if (rawStatus is not ((byte)SessionResumeStatus.Resumed or (byte)SessionResumeStatus.Rejected))
        {
            return false;
        }

        status = (SessionResumeStatus)rawStatus;
        payload.Slice(TokenOffsetInResponse, TokenLength).CopyTo(rotatedToken[..TokenLength]);
        return true;
    }

    /// <summary>세션 수립 통지를 쓴다.</summary>
    /// <param name="destination">길이 <see cref="EstablishedSize"/> 이상.</param>
    /// <param name="sessionId">세션 식별자의 원시 값.</param>
    /// <param name="token">최초 재개 토큰.</param>
    /// <exception cref="ArgumentException">대상이 짧거나 토큰 길이가 다르다.</exception>
    public static void WriteEstablished(Span<byte> destination, long sessionId, ReadOnlySpan<byte> token) =>
        WriteResumeRequest(destination, sessionId, token); // 같은 레이아웃이다.

    /// <summary>세션 수립 통지를 읽는다.</summary>
    /// <param name="payload">받은 페이로드.</param>
    /// <param name="sessionId">세션 식별자의 원시 값.</param>
    /// <param name="token">토큰을 받을 버퍼.</param>
    /// <returns>형식이 맞으면 <see langword="true"/>.</returns>
    public static bool TryReadEstablished(ReadOnlySpan<byte> payload, out long sessionId, Span<byte> token) =>
        TryReadResumeRequest(payload, out sessionId, token);
}
