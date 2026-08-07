using System;

namespace ChServerM.Diagnostics;

/// <summary>
/// 헬스 체크 하나의 결과 — 상태와 사람이 읽을 설명 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>실패는 값이다.</b> 체크가 문제를 예외로 던지지 않고 <see cref="HealthStatus.Unhealthy"/>
/// 결과로 돌려준다 — 헬스 판정 도중의 예외는 그 자체가 잡혀 <see cref="HealthStatus.Unhealthy"/>
/// 로 변환되지만(<c>HealthCheckService</c>), 정상적인 "나쁨" 판정은 값으로 표현하는 것이
/// 계약이다. 그래야 헬스 조회가 예외 처리 경로를 타지 않는다.
/// </para>
/// <para>
/// <b>기본값은 <see cref="HealthStatus.Unhealthy"/> 다</b> — <see cref="HealthStatus"/> 의
/// 기본값 규약을 그대로 물려받는다. <c>default(HealthCheckResult)</c> 는 "미판정 = 나쁨"이다.
/// </para>
/// </remarks>
public readonly struct HealthCheckResult : IEquatable<HealthCheckResult>
{
    /// <summary>상태와 설명으로 결과를 만든다.</summary>
    /// <param name="status">헬스 상태.</param>
    /// <param name="description">사람이 읽을 설명(선택). 저하·비정상의 이유를 남긴다.</param>
    public HealthCheckResult(HealthStatus status, string? description = null)
    {
        Status = status;
        Description = description;
    }

    /// <summary>헬스 상태.</summary>
    public HealthStatus Status { get; }

    /// <summary>사람이 읽을 설명. 없을 수 있다.</summary>
    public string? Description { get; }

    /// <summary>정상 결과를 만든다.</summary>
    /// <param name="description">설명(선택).</param>
    public static HealthCheckResult Healthy(string? description = null) => new(HealthStatus.Healthy, description);

    /// <summary>저하 결과를 만든다.</summary>
    /// <param name="description">저하 이유(선택).</param>
    public static HealthCheckResult Degraded(string? description = null) => new(HealthStatus.Degraded, description);

    /// <summary>비정상 결과를 만든다.</summary>
    /// <param name="description">비정상 이유(선택).</param>
    public static HealthCheckResult Unhealthy(string? description = null) => new(HealthStatus.Unhealthy, description);

    /// <inheritdoc />
    public bool Equals(HealthCheckResult other) =>
        Status == other.Status && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HealthCheckResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Status, Description);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    public static bool operator ==(HealthCheckResult left, HealthCheckResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    public static bool operator !=(HealthCheckResult left, HealthCheckResult right) => !left.Equals(right);
}
