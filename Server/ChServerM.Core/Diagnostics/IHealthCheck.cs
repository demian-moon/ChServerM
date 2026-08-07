using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Diagnostics;

/// <summary>
/// 서버의 한 측면이 건강한지 판정하는 헬스 체크 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "이 서버가 트래픽을 받을 준비가 됐는가(readiness)"·"프로세스가 살아
/// 동작하는가(liveness)"를 오케스트레이터(k8s 등)가 물을 수 있어야 한다. 이 계약이 그
/// 판정 단위다 — 실행 모델·세션 저장소·전송 등 <b>무엇이든</b> 이것을 구현해 헬스에
/// 기여한다. 어느 프로브(liveness/readiness)에 속하는지는 이 계약이 아니라 등록이 정한다
/// (계약을 최소로 유지 — 같은 체크가 두 프로브에 쓰일 수 있다).
/// </para>
/// <para>
/// <b>실패는 값으로, 예외는 예외적으로.</b> "나쁨" 판정은 <see cref="HealthCheckResult.Unhealthy"/>
/// 로 돌려준다. 다만 판정 <b>도중</b>의 예외(저장소 연결 실패 등)는 던져도 된다 —
/// 집계자가 잡아 <see cref="HealthStatus.Unhealthy"/> 로 변환한다. 한 체크의 예외가 전체
/// 헬스 조회를 깨뜨리지 않는다.
/// </para>
/// <para>
/// <b>가볍고 빠르게.</b> 헬스는 주기적으로(초 단위) 자주 호출된다. 무거운 작업·긴 타임아웃을
/// 두지 않는다 — 프로브가 느리면 오케스트레이터가 오판한다. 필요하면 캐시한 상태를 읽는다.
/// </para>
/// <para><b>스레드 규약.</b> 여러 프로브가 동시에 호출할 수 있다 — 구현은 스레드 안전해야 한다.</para>
/// </remarks>
public interface IHealthCheck
{
    /// <summary>이 측면의 헬스를 판정한다.</summary>
    /// <param name="cancellationToken">판정 취소 토큰(프로브 타임아웃 등).</param>
    /// <returns>헬스 결과. 판정 도중 예외를 던지면 집계자가 <see cref="HealthStatus.Unhealthy"/> 로 변환한다.</returns>
    ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
