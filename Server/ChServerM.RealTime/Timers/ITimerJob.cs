namespace ChServerM.RealTime;

/// <summary>
/// 타이머에 등록하는 작업. <see cref="TimerWheel.TrySchedule"/>에 넘긴다.
/// </summary>
/// <remarks>
/// <para>
/// <b>만료와 취소는 다른 콜백이다 — 이 분리가 이 인터페이스의 존재 이유다.</b>
/// 레거시 <c>TimeEventSchedulerM</c>은 만료 발화도 <c>job.Cancel()</c>로 해서 핸들러가
/// "시간이 되어 불렸는지"와 "누가 취소했는지"를 <b>구별할 수 없었다</b> — 취소된 스크립트
/// 지연이 리셋 이벤트를 Set 하는 오동작의 원인이었다. 두 메서드 중 <b>정확히 하나만,
/// 정확히 한 번</b> 호출된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="OnTimerExpired"/>는 항상 휠의 드라이버 스레드에서 불린다.
/// <see cref="OnTimerCanceled"/>는 <see cref="TimerHandle.TryCancel"/>을 부른 스레드에서
/// 즉시 불린다(취소가 30일 뒤 슬롯 도달까지 미뤄지지 않는다) — 셧다운 드레인에서는 드라이버
/// 스레드다. 콜백 예외는 격리·집계되고 휠은 죽지 않는다(CLAUDE.md 9.2).
/// </para>
/// <para>
/// <b>수명 규약.</b> 콜백은 동기·짧게. 긴 작업은 실행 모델로 넘긴다 — 드라이버 스레드에서
/// 블로킹하면 같은 틱의 다른 타이머가 전부 밀린다.
/// </para>
/// </remarks>
public interface ITimerJob
{
    /// <summary>예약 시간이 만료됐다. 드라이버 스레드에서 불린다.</summary>
    void OnTimerExpired();

    /// <summary>발화 전에 취소됐다(명시적 취소 또는 휠 셧다운). 자원 정리는 여기서 한다.</summary>
    void OnTimerCanceled();
}
