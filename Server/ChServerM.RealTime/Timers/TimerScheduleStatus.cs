namespace ChServerM.RealTime;

/// <summary>
/// <see cref="TimerWheel.TrySchedule"/>의 결과. 연산별 상태 enum 규약(Phase 1 에러 모델)을 따른다.
/// </summary>
/// <remarks>
/// 거부(<see cref="CapacityExceeded"/>)가 값으로 드러나는 것이 핵심이다 — 무제한 수용은
/// OOM 붕괴로 끝난다(CLAUDE.md 9.6). <b>거부가 붕괴보다 낫다.</b>
/// </remarks>
public enum TimerScheduleStatus
{
    /// <summary>예약이 수락됐다. 핸들이 유효하다.</summary>
    Accepted = 0,

    /// <summary>살아 있는 타이머 상한(<see cref="TimerWheelOptions.MaxPendingTimers"/>) 초과로 거부됐다.</summary>
    CapacityExceeded = 1,

    /// <summary>휠이 이미 셧다운됐다.</summary>
    Stopped = 2,
}
