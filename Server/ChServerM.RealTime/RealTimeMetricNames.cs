using ChServerM.Diagnostics;

namespace ChServerM.RealTime;

/// <summary>
/// 실시간 축의 메트릭 이름. <see cref="IMetricsSink"/>에 넘기는 문자열의 정본이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> Core 의 <see cref="MetricNames"/>에 넣지 않는 이유는 이 어셈블리가
/// <b>선택 축</b>이기 때문이다(ADR-0004) — Core 가 이 축의 존재를 알게 되는 순간
/// "전부 빼도 성립한다"가 깨진다. 접두사만 Core 와 공유해 대시보드에서 한 계열로 보이게 한다.
/// </para>
/// <para>
/// <b>조용한 유실 금지(CLAUDE.md 9.6).</b> 스킵된 틱(<see cref="TickSkipped"/>)과 거부된
/// 타이머(<see cref="TimerRejected"/>)가 이 목록에 있는 이유다 — 관측되지 않는 유실은
/// 존재하지 않는 것과 같다.
/// </para>
/// </remarks>
public static class RealTimeMetricNames
{
    /// <summary>틱 하나의 실행 시간(초). 히스토그램.</summary>
    public const string TickDuration = DiagnosticNames.Prefix + ".tick.duration";

    /// <summary>예산(틱 간격)을 초과한 틱 수. 카운터.</summary>
    public const string TickOverruns = DiagnosticNames.Prefix + ".tick.overruns";

    /// <summary>캐치업 상한을 넘어 건너뛴 틱 수. 카운터.</summary>
    public const string TickSkipped = DiagnosticNames.Prefix + ".tick.skipped";

    /// <summary>핸들러 예외로 실패한 틱 수. 카운터.</summary>
    public const string TickFaults = DiagnosticNames.Prefix + ".tick.faults";

    /// <summary>예약이 수락된 타이머 수. 카운터.</summary>
    public const string TimerScheduled = DiagnosticNames.Prefix + ".timer.scheduled";

    /// <summary>만료로 발화한 타이머 수. 카운터.</summary>
    public const string TimerFired = DiagnosticNames.Prefix + ".timer.fired";

    /// <summary>취소된 타이머 수. 카운터.</summary>
    public const string TimerCanceled = DiagnosticNames.Prefix + ".timer.canceled";

    /// <summary>상한 초과로 거부된 예약 수. 카운터.</summary>
    public const string TimerRejected = DiagnosticNames.Prefix + ".timer.rejected";

    /// <summary>살아 있는(아직 발화·취소되지 않은) 타이머 수. 게이지.</summary>
    public const string TimerPending = DiagnosticNames.Prefix + ".timer.pending";

    /// <summary>콜백 예외 수. 카운터.</summary>
    public const string TimerFaults = DiagnosticNames.Prefix + ".timer.faults";
}
