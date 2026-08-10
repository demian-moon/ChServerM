using System;

namespace ChServerM.Resilience;

/// <summary>
/// 서킷 브레이커 축의 Core 계약 — 반복 실패하는 대상으로 가는 호출을 끊는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 외부 의존(세션 저장소·원격 서비스)이 죽었을 때 계속 호출하면
/// 호출자의 스레드·커넥션이 타임아웃을 기다리며 묶인다. <b>장애가 대상에서 호출자로
/// 번지는 것</b>이 진짜 문제이며, 이 계약은 그 전파를 끊는다.
/// </para>
/// <para>
/// <b>ADR-0027 의 보류를 여기서 푼다.</b> Phase 10 에서 "대상이 없는 추상화는 만들지
/// 않는다" 는 이유로 미뤄 뒀고, Redis 세션 저장소(ADR-0034)가 첫 실물 대상이 되면서
/// 만들었다. 추상화를 먼저 만들지 않은 것이 옳았다 — 실물이 있어야 "무엇을 실패로
/// 세는가" 같은 결정을 근거 있게 할 수 있다.
/// </para>
///
/// <para>
/// <b>⚠ 왜 <c>ExecuteAsync(Func&lt;...&gt;)</c> 가 아닌가.</b> 델리게이트를 받는 실행
/// 래퍼는 쓰기 편하지만 <b>호출마다 클로저와 상태 머신을 할당</b>한다. 세션 조회는 요청마다
/// 일어나는 경로이므로 그 비용이 그대로 곱해진다. 대신 <see cref="TryEnter"/> +
/// <see cref="RecordSuccess"/>/<see cref="RecordFailure"/> 세 조각으로 나눠 호출자가
/// 무할당으로 조립하게 한다(CLAUDE.md 2절).
/// </para>
///
/// <para>
/// <b>⚠ 무엇을 실패로 세는가는 호출자가 정한다.</b> 이 계약은 "실패했다" 는 사실만 받는다.
/// <b>대상이 정상적으로 '아니오' 라고 답한 것은 실패가 아니다</b> — 예를 들어 세션 CAS
/// 충돌은 정상적인 동시성 결과이므로, 그것을 실패로 세면 <b>경합이 심할 때 멀쩡한 저장소를
/// 차단</b>하게 된다. 이 구분을 계약 밖에 둔 이유는 축마다 답이 다르기 때문이다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 구현은 <b>스레드 안전해야 한다.</b> 여러 요청 경로가 같은 브레이커를
/// 동시에 지나간다.
/// </para>
///
/// <para>
/// <b>사용 규약 — <see cref="TryEnter"/> 가 <see langword="true"/> 를 반환했으면
/// 반드시 결과를 보고한다.</b> 성공·실패 중 하나를 <c>finally</c> 에서 보장한다.
/// 보고를 빠뜨리면 반열림 상태의 시험 자리가 영구히 점유되어 <b>회로가 영원히 닫히지
/// 않는다</b>(CLAUDE.md 9.2 의 "락-프리 상태는 finally 로 복원한다" 와 같은 부류다).
/// </para>
/// </remarks>
public interface ICircuitBreaker
{
    /// <summary>브레이커 이름. 진단·메트릭에서 어느 대상인지 구분한다.</summary>
    string Name { get; }

    /// <summary>현재 상태.</summary>
    CircuitState State { get; }

    /// <summary>호출을 시도해도 되는지 묻는다.</summary>
    /// <returns>
    /// 통과시키면 <see langword="true"/>. 차단이면 <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <see langword="true"/> 를 받았으면 <b>반드시</b> <see cref="RecordSuccess"/> 또는
    /// <see cref="RecordFailure"/> 로 결과를 보고한다(타입 문서의 사용 규약).
    /// </remarks>
    bool TryEnter();

    /// <summary>호출이 성공했음을 보고한다.</summary>
    void RecordSuccess();

    /// <summary>호출이 실패했음을 보고한다.</summary>
    /// <param name="exception">실패 원인. 진단·로그용이며 판정에는 쓰지 않는다.</param>
    void RecordFailure(Exception? exception = null);
}
