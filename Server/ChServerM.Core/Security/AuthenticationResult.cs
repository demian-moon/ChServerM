using System;

namespace ChServerM.Security;

/// <summary>
/// 자격 검증 한 번의 결과 — 성공이면 커넥션에 부여할 상태 비트를 담는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>실패를 예외가 아니라 값으로 나른다.</b> 오답 자격은 원격 입력이 만드는 정상적인
/// 실패 경로다 — 예외를 쓰면 악의적 로그인 폭주가 비용을 증폭시킨다(T-16,
/// <see cref="SecureChannelResult"/> 와 같은 원칙).
/// </para>
/// <para>
/// <b>성공 = 상태 전이다.</b> "인증됐다"는 별도 플래그를 두지 않고
/// <see cref="GrantedStates"/> 가 <c>IConnectionStateFeature</c> 로 대체(replace)
/// 전이된다 — 인증 여부와 허용 메시지 집합이라는 두 상태가 따로 놀며 어긋나는
/// 버그 표면 자체를 없앤다(T-19 화이트리스트와 한 몸).
/// </para>
/// <para>
/// <b><see langword="default"/> 는 실패다.</b> 초기화를 빠뜨린 경로가 가장 제한적인
/// 결과로 수렴한다(가장 제한적 기본값 원칙).
/// </para>
/// </remarks>
public readonly struct AuthenticationResult : IEquatable<AuthenticationResult>
{
    private AuthenticationResult(bool isAuthenticated, uint grantedStates, string? failureDescription)
    {
        IsAuthenticated = isAuthenticated;
        GrantedStates = grantedStates;
        FailureDescription = failureDescription;
    }

    /// <summary>자격이 유효한지 여부.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>성공 시 커넥션 상태에 대체 전이할 비트. 실패면 0.</summary>
    /// <remarks>비트의 의미는 앱이 정의한다 — 프레임워크가 상태 이름을 정하면
    /// 워크로드 전제가 Core 에 들어온다(ADR-0004, <c>IConnectionStateFeature</c> 와 동일).</remarks>
    public uint GrantedStates { get; }

    /// <summary>실패 사유. 로그 전용 — <b>와이어로 나가지 않는다.</b></summary>
    /// <remarks>
    /// "존재하지 않는 계정"과 "비밀번호 오답"을 상대에게 구분해 알리면 계정 존재 여부를
    /// 노출한다(계정 열거). 상세 사유는 서버 로그까지만 간다.
    /// </remarks>
    public string? FailureDescription { get; }

    /// <summary>검증 성공 — 부여할 상태 비트와 함께.</summary>
    /// <param name="grantedStates">전이할 상태 비트. 0일 수 없다.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="grantedStates"/>가 0일 때.</exception>
    /// <remarks>
    /// 0을 막는 이유: 전부-거부 상태로의 "성공"은 어떤 메시지도 통과시키지 못하는
    /// 죽은 설정이다 — <c>MessageStateFilterOptions.InitialStates = 0</c> 거부와 같은 원칙.
    /// </remarks>
    public static AuthenticationResult Success(uint grantedStates)
    {
        ArgumentOutOfRangeException.ThrowIfZero(grantedStates);
        return new AuthenticationResult(isAuthenticated: true, grantedStates, failureDescription: null);
    }

    /// <summary>검증 실패.</summary>
    /// <param name="description">로그에 남길 사유. 와이어로 나가지 않는다.</param>
    public static AuthenticationResult Failure(string? description = null) =>
        new(isAuthenticated: false, grantedStates: 0, description);

    /// <inheritdoc />
    public bool Equals(AuthenticationResult other) =>
        IsAuthenticated == other.IsAuthenticated
        && GrantedStates == other.GrantedStates
        && FailureDescription == other.FailureDescription;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthenticationResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(IsAuthenticated, GrantedStates, FailureDescription);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    public static bool operator ==(AuthenticationResult left, AuthenticationResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    public static bool operator !=(AuthenticationResult left, AuthenticationResult right) => !left.Equals(right);
}
