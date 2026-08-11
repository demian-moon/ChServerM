namespace ChServerM.RealTime;

/// <summary>
/// 틱마다 호출되는 작업. <see cref="TickLoop"/>에 넘긴다.
/// </summary>
/// <remarks>
/// <para>
/// <b>동기 계약이다 — 의도적이다.</b> 반환하면 틱이 끝난 것이고, 실행 시간이 곧 예산 소비다.
/// <c>async</c> 로 만들면 "틱이 예산을 넘었는가"를 판정할 수 없다(어디까지가 이 틱의
/// 작업인지 경계가 사라진다). 비동기 작업이 필요하면 여기서 시작만 하고
/// 완료는 실행 모델(<c>IExecutionModel</c>)에 맡긴다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 항상 틱 루프의 전용 스레드에서, 절대 동시에 두 번 호출되지 않는다.
/// 핸들러 내부 상태에 동기화가 필요 없다는 뜻이다(CLAUDE.md 9.1 — 계약을 코드로 표현한다).
/// </para>
/// <para>
/// 예외를 던져도 루프는 죽지 않는다 — 틱 단위로 격리되고
/// <see cref="TickLoopStatistics.FaultedTicks"/>로 집계된다(CLAUDE.md 9.2).
/// </para>
/// </remarks>
public interface ITickHandler
{
    /// <summary>틱 하나를 실행한다.</summary>
    /// <param name="context">이 틱의 문맥. 예정 시각·시작 지연·예산이 들어 있다.</param>
    void OnTick(in TickContext context);
}
