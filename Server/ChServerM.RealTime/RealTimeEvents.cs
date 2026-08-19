using ChServerM.Diagnostics;

namespace ChServerM.RealTime;

/// <summary>
/// 실시간 축의 로그 이벤트 ID. 1700 대역을 쓴다.
/// </summary>
/// <remarks>
/// 번호가 정본이고 이름은 보조다(<see cref="EventId"/> 규약). 런타임 이벤트 ID 대역의
/// 전역 정리표는 아직 없다 — 생기면 이 대역(1700~1799, Phase 17)을 등록한다.
/// </remarks>
internal static class RealTimeEvents
{
    /// <summary>틱 실행 시간이 예산(틱 간격)을 넘었다.</summary>
    internal static readonly EventId TickOverrun = new(1701, nameof(TickOverrun));

    /// <summary>캐치업 상한을 넘어 틱을 건너뛰었다.</summary>
    internal static readonly EventId TicksSkipped = new(1702, nameof(TicksSkipped));

    /// <summary>틱 핸들러가 예외를 던졌다.</summary>
    internal static readonly EventId TickFaulted = new(1703, nameof(TickFaulted));

    /// <summary>틱 루프 자체가 예외로 중단됐다. 핸들러 예외가 아니라 루프 결함이다.</summary>
    internal static readonly EventId TickLoopCrashed = new(1704, nameof(TickLoopCrashed));

    /// <summary>스핀 구간이 실측상 효과 없는 조합으로 설정됐다(감사 2026-08-18 R-9).</summary>
    internal static readonly EventId SpinWindowIneffective = new(1705, nameof(SpinWindowIneffective));

    /// <summary>타이머 콜백이 예외를 던졌다.</summary>
    internal static readonly EventId TimerCallbackFaulted = new(1711, nameof(TimerCallbackFaulted));

    /// <summary>살아 있는 타이머 상한 초과로 예약을 거부했다.</summary>
    internal static readonly EventId TimerRejected = new(1712, nameof(TimerRejected));
}
