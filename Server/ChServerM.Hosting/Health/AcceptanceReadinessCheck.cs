using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 서버가 트래픽을 받을 상태인지 보는 내장 readiness 체크 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> readiness 의 1차 근원은 서버 생명주기다 — 수용 중이면 준비됨,
/// 드레이닝이면 준비 안 됨. 무중단 배포에서 <see cref="ChServerMServer.UnbindAsync"/> 로
/// 신규 수용을 멈춘 순간 이 체크가 <see cref="HealthStatus.Unhealthy"/> 로 바뀌어, 로드밸런서가
/// 트래픽을 빼는 동안 기존 커넥션이 하던 일을 끝낸다(디레지스터 신호).
/// </para>
/// <para>
/// <b>liveness 가 아니라 readiness 다.</b> 드레이닝은 <b>정상적인</b> 종료 절차이지 고장이
/// 아니다 — liveness 로 쓰면 배포 때마다 오케스트레이터가 프로세스를 재시작해버린다
/// (<see cref="HealthProbe"/> 문서의 프로브 의미 차이).
/// </para>
/// </remarks>
internal sealed class AcceptanceReadinessCheck : IHealthCheck
{
    private readonly ServerLifecycleState _lifecycle;

    public AcceptanceReadinessCheck(ServerLifecycleState lifecycle) => _lifecycle = lifecycle;

    /// <inheritdoc />
    public ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        HealthCheckResult result = _lifecycle.State switch
        {
            ServerState.Accepting => HealthCheckResult.Healthy("수용 중"),
            ServerState.Created => HealthCheckResult.Unhealthy("아직 시작하지 않음"),
            ServerState.Draining => HealthCheckResult.Unhealthy("드레이닝 중 — 신규 수용 중단"),
            _ => HealthCheckResult.Unhealthy("멈춤"),
        };

        return ValueTask.FromResult(result);
    }
}
