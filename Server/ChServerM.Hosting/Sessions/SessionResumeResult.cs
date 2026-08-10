using System;
using ChServerM.Sessions;

namespace ChServerM.Hosting.Sessions;

/// <summary>세션 재개 시도의 결과.</summary>
/// <remarks>
/// <para>
/// <b>⚠ 실패 사유를 세분하지 않는다.</b> "세션이 없다" 와 "토큰이 틀렸다" 를 구분해 주면
/// 공격자가 <b>어떤 SessionId 가 실재하는지 열거</b>할 수 있다. 두 경우 모두
/// <see cref="Failed"/> 하나로 답한다 — 서버 로그에는 구분해 남기되 <b>상대에게는 알리지 않는다</b>.
/// </para>
/// </remarks>
public readonly struct SessionResumeResult : IEquatable<SessionResumeResult>
{
    private SessionResumeResult(bool succeeded, SessionVersion version, SessionResumeToken rotatedToken, int stateLength)
    {
        Succeeded = succeeded;
        Version = version;
        RotatedToken = rotatedToken;
        StateLength = stateLength;
    }

    /// <summary>재개에 성공했는가.</summary>
    public bool Succeeded { get; }

    /// <summary>재개 후의 세션 버전. 이후 쓰기의 기대 버전으로 쓴다.</summary>
    public SessionVersion Version { get; }

    /// <summary>
    /// <b>새로 발급된</b> 재개 토큰. 클라이언트에 전달해야 다음 재접속이 가능하다.
    /// </summary>
    /// <remarks>
    /// 옛 토큰은 이 시점에 이미 무효다 — 회전이 곧 탈취 방어다.
    /// </remarks>
    public SessionResumeToken RotatedToken { get; }

    /// <summary>대상에 쓴 앱 상태의 길이(바이트).</summary>
    public int StateLength { get; }

    /// <summary>재개 실패(세션 없음 또는 토큰 불일치 — 구분하지 않는다).</summary>
    public static SessionResumeResult Failed => default;

    /// <summary>재개 성공.</summary>
    /// <param name="version">재개 후 버전.</param>
    /// <param name="rotatedToken">새 토큰.</param>
    /// <param name="stateLength">쓴 앱 상태 길이.</param>
    /// <returns>성공 결과.</returns>
    public static SessionResumeResult Ok(SessionVersion version, SessionResumeToken rotatedToken, int stateLength) =>
        new(true, version, rotatedToken, stateLength);

    /// <inheritdoc/>
    public bool Equals(SessionResumeResult other) =>
        Succeeded == other.Succeeded && Version == other.Version && StateLength == other.StateLength
        && RotatedToken == other.RotatedToken;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionResumeResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Succeeded, Version, StateLength);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(SessionResumeResult left, SessionResumeResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(SessionResumeResult left, SessionResumeResult right) => !left.Equals(right);
}

/// <summary>새 세션을 만들 때의 결과 — 식별자·버전·최초 재개 토큰.</summary>
public readonly struct SessionBinding : IEquatable<SessionBinding>
{
    /// <summary>세션 바인딩을 만든다.</summary>
    /// <param name="version">현재 버전.</param>
    /// <param name="resumeToken">클라이언트에 전달할 재개 토큰.</param>
    public SessionBinding(SessionVersion version, SessionResumeToken resumeToken)
    {
        Version = version;
        ResumeToken = resumeToken;
    }

    /// <summary>현재 세션 버전. 이후 쓰기의 기대 버전으로 쓴다.</summary>
    public SessionVersion Version { get; }

    /// <summary>클라이언트에 전달할 재개 토큰. <b>로그에 남기지 않는다.</b></summary>
    public SessionResumeToken ResumeToken { get; }

    /// <inheritdoc/>
    public bool Equals(SessionBinding other) => Version == other.Version && ResumeToken == other.ResumeToken;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionBinding other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Version.GetHashCode();

    /// <summary>두 바인딩이 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(SessionBinding left, SessionBinding right) => left.Equals(right);

    /// <summary>두 바인딩이 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(SessionBinding left, SessionBinding right) => !left.Equals(right);
}
