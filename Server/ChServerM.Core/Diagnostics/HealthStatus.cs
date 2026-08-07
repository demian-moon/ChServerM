namespace ChServerM.Diagnostics;

/// <summary>
/// 헬스 체크 하나 또는 집계의 상태 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>값 순서가 곧 심각도다 — 작을수록 나쁘다.</b> 집계는 "가장 나쁜 것이 이긴다"(최소값)로
/// 계산한다: 하나라도 <see cref="Unhealthy"/> 면 전체가 <see cref="Unhealthy"/>, 아니면서
/// 하나라도 <see cref="Degraded"/> 면 <see cref="Degraded"/>, 전부 <see cref="Healthy"/> 여야
/// <see cref="Healthy"/> 다.
/// </para>
/// <para>
/// <b>기본값(0)은 <see cref="Unhealthy"/> 다 — 가장 제한적인 기본값.</b> 결과를 설정하지
/// 못한 경로(예외로 중단·미초기화)는 자동으로 "나쁨"으로 판정된다. "모르면 안전하게"가
/// 헬스의 올바른 기본이다(<c>AdmissionDecision</c>·<c>AuthenticationResult</c> 와 같은 규약).
/// </para>
/// </remarks>
public enum HealthStatus : byte
{
    /// <summary>정상 동작하지 않는다. liveness 면 재시작, readiness 면 트래픽 제외 대상이다.</summary>
    Unhealthy = 0,

    /// <summary>동작하지만 저하됐다. 아직 처리하되 주의가 필요하다.</summary>
    Degraded = 1,

    /// <summary>정상이다.</summary>
    Healthy = 2,
}
