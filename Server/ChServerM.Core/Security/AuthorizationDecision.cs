using System;

namespace ChServerM.Security;

/// <summary>
/// 인가 판정 한 번의 결과 (T-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>실패를 예외가 아니라 값으로 나른다.</b> 권한 밖 요청은 원격 입력이 만드는 정상적인
/// 실패 경로다(<see cref="AuthenticationResult"/> 와 같은 원칙, T-16).
/// </para>
/// <para>
/// <b><see langword="default"/> 는 거부다.</b> 초기화를 빠뜨린 경로가 가장 제한적인
/// 결과로 수렴한다(가장 제한적 기본값 원칙).
/// </para>
/// </remarks>
public readonly struct AuthorizationDecision : IEquatable<AuthorizationDecision>
{
    private AuthorizationDecision(bool isAllowed, string? denyDescription)
    {
        IsAllowed = isAllowed;
        DenyDescription = denyDescription;
    }

    /// <summary>요청이 허용됐는지 여부.</summary>
    public bool IsAllowed { get; }

    /// <summary>거부 사유. 로그 전용 — <b>와이어로 나가지 않는다.</b></summary>
    /// <remarks>
    /// 상세 사유를 상대에게 알리면 권한 체계·자원 존재 여부를 탐색하는 지도가 된다.
    /// 서버 로그까지만 간다(<see cref="AuthenticationResult.FailureDescription"/> 과 동일).
    /// </remarks>
    public string? DenyDescription { get; }

    /// <summary>허용.</summary>
    public static AuthorizationDecision Allow() => new(isAllowed: true, denyDescription: null);

    /// <summary>거부.</summary>
    /// <param name="description">로그에 남길 사유. 와이어로 나가지 않는다.</param>
    public static AuthorizationDecision Deny(string? description = null) =>
        new(isAllowed: false, description);

    /// <inheritdoc />
    public bool Equals(AuthorizationDecision other) =>
        IsAllowed == other.IsAllowed && DenyDescription == other.DenyDescription;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorizationDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsAllowed, DenyDescription);

    /// <summary>두 판정이 같은지 비교한다.</summary>
    public static bool operator ==(AuthorizationDecision left, AuthorizationDecision right) => left.Equals(right);

    /// <summary>두 판정이 다른지 비교한다.</summary>
    public static bool operator !=(AuthorizationDecision left, AuthorizationDecision right) => !left.Equals(right);
}
