using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 등록된 헬스 체크를 프로브별로 돌려 집계하는 서비스 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 여러 <see cref="IHealthCheck"/> 를 한 프로브 결과로 모은다. 조립 시점에
/// 등록을 고정하고(<c>ServerBuilder</c>), 프로브가 물을 때마다 해당 프로브의 체크만 골라
/// 돌려 <see cref="HealthReport"/> 로 집계한다.
/// </para>
/// <para>
/// <b>항목별 <c>try/catch</c> — 한 체크의 예외가 전체를 깨지 않는다.</b> 체크가 예외를
/// 던지면 그 항목만 <see cref="HealthStatus.Unhealthy"/> 로 변환하고 나머지는 계속 판정한다.
/// 헬스 조회 자체가 예외로 터지면 오케스트레이터가 프로세스를 오판한다 — 소비 루프의
/// 항목별 격리(CLAUDE.md 9.2)와 같은 원칙이다.
/// </para>
/// <para>
/// <b>순차 실행.</b> 내장 체크는 로컬 상태(스레드 생존·생명주기 플래그)를 읽는 즉시 완료라
/// 병렬화 이득이 없다. I/O 를 하는 체크(원격 저장소 핑)가 늘면 병렬로 바꾼다 — 그때는
/// 측정을 근거로 남긴다(CLAUDE.md 2).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 등록은 불변이고 체크는 스레드 안전 계약이므로, 여러 프로브가
/// 동시에 <see cref="CheckHealthAsync"/> 를 불러도 안전하다.
/// </para>
/// </remarks>
public sealed class HealthCheckService
{
    private readonly HealthCheckRegistration[] _registrations;

    /// <summary>등록으로 서비스를 만든다.</summary>
    /// <param name="registrations">헬스 체크 등록. 순서가 보고서 항목 순서다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/>가 <see langword="null"/>일 때.</exception>
    public HealthCheckService(IEnumerable<HealthCheckRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _registrations = [.. registrations];
    }

    /// <summary>프로브 하나의 헬스를 판정해 집계한다.</summary>
    /// <param name="probe">판정할 프로브. 이 프로브에 속한 체크만 돈다. 기본은 <see cref="HealthProbe.All"/>.</param>
    /// <param name="cancellationToken">판정 취소 토큰(프로브 타임아웃 등).</param>
    /// <returns>집계 상태와 체크별 결과. 해당 프로브의 체크가 없으면 <see cref="HealthStatus.Healthy"/>(감시할 것이 없다).</returns>
    public async ValueTask<HealthReport> CheckHealthAsync(
        HealthProbe probe = HealthProbe.All,
        CancellationToken cancellationToken = default)
    {
        List<HealthReportEntry> entries = [];

        // 최악이 이긴다 — Healthy(최댓값)에서 시작해 항목마다 최솟값으로 내린다.
        HealthStatus aggregate = HealthStatus.Healthy;

        foreach (HealthCheckRegistration registration in _registrations)
        {
            // 이 프로브에 속하지 않는 체크는 건너뛴다.
            if ((registration.Probes & probe) == 0)
            {
                continue;
            }

            HealthCheckResult result = await RunOneAsync(registration, cancellationToken).ConfigureAwait(false);
            entries.Add(new HealthReportEntry(registration.Name, result.Status, result.Description));

            if (result.Status < aggregate)
            {
                aggregate = result.Status;
            }
        }

        return new HealthReport(aggregate, entries);
    }

    private static async ValueTask<HealthCheckResult> RunOneAsync(
        HealthCheckRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            return await registration.Check.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 계약: 체크의 예외는 이 항목만 Unhealthy 로 만들고 전체 조회는 계속된다.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // 판정 도중 예외 = 나쁨. 원인을 설명에 남겨 대시보드에서 보이게 한다.
            return HealthCheckResult.Unhealthy(exception.Message);
        }
    }
}
