namespace ChServerM.Resilience;

/// <summary>
/// 서킷 브레이커의 상태.
/// </summary>
/// <remarks>
/// 값 순서는 <b>"얼마나 통과시키는가"</b> 의 내림차순이다 — 진단 출력과 메트릭에서
/// 그대로 읽히도록 했다.
/// </remarks>
public enum CircuitState
{
    /// <summary>정상. 모든 호출을 통과시킨다.</summary>
    Closed = 0,

    /// <summary>
    /// 시험 중. <b>제한된 수의 호출만</b> 통과시켜 대상이 회복됐는지 확인한다.
    /// </summary>
    /// <remarks>
    /// 여기서 성공하면 <see cref="Closed"/>, 실패하면 다시 <see cref="Open"/> 이다.
    /// 이 단계가 없으면 회복 판정이 곧 전량 재개가 되어, 아직 아픈 대상에 부하를 몰아
    /// 다시 쓰러뜨린다.
    /// </remarks>
    HalfOpen = 1,

    /// <summary>차단. 대상을 호출하지 않고 즉시 실패시킨다.</summary>
    /// <remarks>
    /// <b>빠른 실패가 목적이다.</b> 죽은 대상에 계속 호출하면 호출자의 스레드·커넥션이
    /// 타임아웃을 기다리며 묶여, 장애가 <b>호출자 쪽으로 번진다</b>.
    /// </remarks>
    Open = 2,
}
