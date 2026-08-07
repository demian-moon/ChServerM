using System;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 헬스 체크 하나의 등록 — 이름, 체크, 소속 프로브 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <b>이름은 보고서의 키다.</b> 대시보드·로그가 어느 체크가 나쁜지 이름으로 가리킨다 —
/// 등록마다 고유해야 오해가 없다. 소속 프로브(<see cref="HealthProbe"/>)를 여기 두는 이유는
/// <see cref="HealthProbe"/> 문서 참조.
/// </remarks>
public sealed class HealthCheckRegistration
{
    /// <summary>등록을 만든다.</summary>
    /// <param name="name">체크 이름(보고서 키). 비어 있을 수 없다.</param>
    /// <param name="check">헬스 체크.</param>
    /// <param name="probes">이 체크가 기여하는 프로브. 기본은 <see cref="HealthProbe.All"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/>이 비어 있을 때.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="check"/>가 <see langword="null"/>일 때.</exception>
    public HealthCheckRegistration(string name, IHealthCheck check, HealthProbe probes = HealthProbe.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        Name = name;
        Check = check;
        Probes = probes;
    }

    /// <summary>체크 이름(보고서 키).</summary>
    public string Name { get; }

    /// <summary>헬스 체크.</summary>
    public IHealthCheck Check { get; }

    /// <summary>이 체크가 기여하는 프로브.</summary>
    public HealthProbe Probes { get; }
}
