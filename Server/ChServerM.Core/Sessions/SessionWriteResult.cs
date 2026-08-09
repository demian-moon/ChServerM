using System;

namespace ChServerM.Sessions;

/// <summary>
/// 세션 쓰기의 결과 — 성공 여부와, 성공했다면 새 버전.
/// </summary>
/// <remarks>
/// <para>
/// <b>실패는 예외가 아니다.</b> 버전 충돌은 <b>정상적인 동시성 결과</b>이므로 핫패스에서
/// 예외로 흘리지 않는다(CLAUDE.md 8절 <c>TryXxx</c> 패턴). 저장소 장애·직렬화 오류처럼
/// 진짜 예외적인 상황만 예외로 던진다.
/// </para>
/// <para>
/// <b>⚠ 충돌 시 현재 버전을 돌려주지 않는 이유.</b> 버전만 받아도 호출자는 아무것도 할 수
/// 없다 — CAS 실패는 "남이 값을 바꿨다" 는 뜻이므로 <b>바뀐 값을 다시 읽어 병합</b>해야 하고,
/// 그 재읽기가 어차피 최신 버전을 가져온다. 버전만 돌려주는 API 는 쓸모 없는 정보를 위해
/// 어댑터에 부담(Redis 라면 Lua 스크립트나 추가 왕복)을 지운다.
/// </para>
/// </remarks>
public readonly struct SessionWriteResult : IEquatable<SessionWriteResult>
{
    private SessionWriteResult(bool succeeded, SessionVersion version)
    {
        Succeeded = succeeded;
        Version = version;
    }

    /// <summary>기대 버전이 맞아 쓰기가 반영됐는가.</summary>
    public bool Succeeded { get; }

    /// <summary>성공했을 때의 새 버전. 실패면 <see cref="SessionVersion.None"/>.</summary>
    public SessionVersion Version { get; }

    /// <summary>기대 버전이 현재 버전과 달라 쓰기를 적용하지 않았다.</summary>
    public static SessionWriteResult Conflict => default;

    /// <summary>쓰기가 반영됐다.</summary>
    /// <param name="version">새 버전.</param>
    /// <returns>성공 결과.</returns>
    public static SessionWriteResult Ok(SessionVersion version) => new(true, version);

    /// <inheritdoc/>
    public bool Equals(SessionWriteResult other) => Succeeded == other.Succeeded && Version == other.Version;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionWriteResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Succeeded, Version);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(SessionWriteResult left, SessionWriteResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(SessionWriteResult left, SessionWriteResult right) => !left.Equals(right);
}
