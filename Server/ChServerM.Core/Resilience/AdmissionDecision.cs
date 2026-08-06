using System;

namespace ChServerM.Resilience;

/// <summary>
/// 신규 커넥션 수용 판정 하나의 결과 (Phase 10, T-14).
/// </summary>
/// <remarks>
/// <para>
/// <b>실패를 예외가 아니라 값으로 나른다.</b> 과부하 거부는 정상 동작이다 —
/// "거부가 붕괴보다 낫다"(CLAUDE.md 9.6). 수락 루프의 판정마다 예외를 던지면 그것이 곧
/// 자원 소모 경로가 된다(<see cref="ChServerM.Security.AuthorizationDecision"/> 과 같은 원칙).
/// </para>
/// <para>
/// <b><see langword="default"/> 는 거부다.</b> 초기화를 빠뜨린 경로가 가장 제한적인
/// 결과로 수렴한다(가장 제한적 기본값 원칙).
/// </para>
/// </remarks>
public readonly struct AdmissionDecision : IEquatable<AdmissionDecision>
{
    private AdmissionDecision(bool isAdmitted, string? rejectionReason)
    {
        IsAdmitted = isAdmitted;
        RejectionReason = rejectionReason;
    }

    /// <summary>커넥션을 수용할지 여부.</summary>
    public bool IsAdmitted { get; }

    /// <summary>거부 사유. 로그 전용 — 저카디널리티 메트릭 태그로 쓰지 않는다.</summary>
    /// <remarks>
    /// 자유 문자열이라 메트릭 태그로 쓰면 시계열이 폭발한다(<c>TagNames</c> 규약).
    /// 관측은 거부 지점(전송)이 고정 저카디널리티 사유로 남기고, 이 문자열은 로그까지만 간다.
    /// </remarks>
    public string? RejectionReason { get; }

    /// <summary>수용.</summary>
    public static AdmissionDecision Admit() => new(isAdmitted: true, rejectionReason: null);

    /// <summary>거부.</summary>
    /// <param name="reason">로그에 남길 사유.</param>
    public static AdmissionDecision Reject(string? reason = null) => new(isAdmitted: false, reason);

    /// <inheritdoc />
    public bool Equals(AdmissionDecision other) =>
        IsAdmitted == other.IsAdmitted && RejectionReason == other.RejectionReason;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AdmissionDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsAdmitted, RejectionReason);

    /// <summary>두 판정이 같은지 비교한다.</summary>
    public static bool operator ==(AdmissionDecision left, AdmissionDecision right) => left.Equals(right);

    /// <summary>두 판정이 다른지 비교한다.</summary>
    public static bool operator !=(AdmissionDecision left, AdmissionDecision right) => !left.Equals(right);
}
