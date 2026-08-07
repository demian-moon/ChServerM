namespace ChServerM.Dispatch;

/// <summary>
/// 메시지 하나를 디스패치한 결과.
/// </summary>
/// <remarks>
/// <para>
/// 디스패처는 <b>예외를 밖으로 흘리지 않는다.</b> 핸들러 하나가 던진 예외 때문에
/// 읽기 루프가 죽으면 멀쩡한 후속 메시지까지 잃는다. 결과를 값으로 돌려
/// 호출자가 "닫을지 계속할지"를 정하게 한다.
/// </para>
/// <para>
/// <see cref="Handled"/>가 아닌 모든 값은 <b>반드시 메트릭에 기록된다.</b>
/// 조용히 버려지는 메시지를 만들지 않는 것이 이 enum의 목적이다.
/// </para>
/// </remarks>
public enum DispatchStatus : byte
{
    /// <summary>핸들러가 정상적으로 처리했다.</summary>
    Handled = 0,

    /// <summary>이 메시지 식별자에 등록된 핸들러가 없다.</summary>
    /// <remarks>
    /// 커넥션을 닫을지는 정책이다. 엄격한 프로토콜이면 닫고,
    /// 버전 호환이 필요하면 무시하고 계속한다.
    /// </remarks>
    HandlerNotFound = 1,

    /// <summary>현재 커넥션 상태에서 허용되지 않는 메시지다.</summary>
    /// <remarks>인증 전에 게임 메시지를 보내는 경우 등. 레거시의 상태 화이트리스트가 잡던 것.</remarks>
    RejectedByState = 2,

    /// <summary>미들웨어가 거부했다(인증·권한·속도 제한).</summary>
    RejectedByPolicy = 3,

    /// <summary>페이로드를 역직렬화할 수 없었다.</summary>
    DeserializationFailed = 4,

    /// <summary>큐 포화로 받지 못했다. <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).</summary>
    /// <remarks>
    /// <para>
    /// <b>현재 실행 모델에는 이 값을 만드는 자연 생산자가 없다 — 설계상 그렇다.</b>
    /// 메시지 디스패치 주 경로(<c>PartitionDispatchGate</c>→<c>TryEnqueueExclusive</c>)는
    /// <b>자연 백프레셔</b>를 쓴다: 커넥션당 in-flight 프레임은 항상 1건이고, 읽기 루프가
    /// 그 완료를 기다리며 소켓 읽기를 멈춘다(ADR-0008). 그래서 이 경로의 큐는 넘칠 수 없고,
    /// 넘치지 않으니 백프레셔로 거부할 일이 없다. 유계 큐가 실제로 포화하는 곳은
    /// <c>IExecutionPartition.TryPost</c>(타이머·크로스 파티션 주입)이며, 그것은
    /// <see cref="DispatchStatus"/>(메시지 단위 결과)가 아니라
    /// <see cref="Diagnostics.MetricNames.PartitionWorkRejected"/> 카운터로 관측된다.
    /// </para>
    /// <para>
    /// 따라서 이 값은 <b>큐잉 디스패치 모델(채널 워커 풀 등, 축 표의 후보)이 도입될 때를 위해
    /// 예약</b>돼 있다 — 그 모델에서는 디스패치 = 유계 풀 게시이고, 포화가 곧 이 상태다.
    /// <c>FramedConnectionHandler</c> 의 매핑(자원 한계 종료)은 그날을 위한 방어적 배선이다.
    /// 지금 억지 생산자를 만들면 위의 자연 백프레셔와 충돌한다.
    /// </para>
    /// </remarks>
    RejectedByBackpressure = 5,

    /// <summary>핸들러가 예외를 던졌다.</summary>
    Faulted = 6,

    /// <summary>처리 중 취소됐다(커넥션 종료·서버 종료).</summary>
    Canceled = 7,

    /// <summary>자격 검증에 실패했다. <b>커넥션은 무조건 닫힌다</b> — 옵션이 아니다.</summary>
    /// <remarks>
    /// <see cref="RejectedByPolicy"/> 와 구분하는 이유: 그쪽은 <c>CloseOnPolicyRejection</c>
    /// 옵션에 걸려 있어, 재사용하면 옵션 하나로 인증 실패가 종료 없이 통과하는 구멍이
    /// 생긴다. 레거시가 정확히 "검증은 하는데 결과를 버리는" 형태로 죽었으므로(T-20),
    /// 인증 실패 = 즉시 종료는 정책이 아니라 불변으로 둔다 —
    /// <see cref="RejectedByState"/>(T-19)와 같은 급이다. 관측에서도 인증(6000)과
    /// 인가(6001)가 구분된다(T-07).
    /// </remarks>
    RejectedByAuthentication = 8,

    /// <summary>속도 제한에 걸렸다. <b>커넥션은 닫지 않는다</b>(기본) — 일시적 제한이다.</summary>
    /// <remarks>
    /// <see cref="RejectedByPolicy"/>(6001, 인가)와 구분하는 이유: 속도 제한은
    /// <c>RateLimitExceeded</c>(6003)로 관측돼야 인가 실패와 대시보드에서 갈린다. 그리고
    /// 속도 제한은 <b>일시적</b>이라 커넥션을 끊으면 정상 사용자가 재접속 폭풍을 만든다
    /// (<c>FramedConnectionOptions</c> 의 정책 거부 무-종료 근거와 같다). 그래서 이 상태는
    /// 옵션 무관하게 커넥션을 닫지 않고 그 프레임만 버린다 — 클라이언트는 스스로 늦춘다.
    /// </remarks>
    RejectedByRateLimit = 9,

    /// <summary>부하가 높아 비필수로 분류된 메시지를 버렸다. <b>커넥션은 닫지 않는다</b> — 일시적 압박이다.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="RejectedByPolicy"/> 와 구분하는 이유: 그쪽은 <c>CloseOnPolicyRejection</c>
    /// 옵션에 걸려 있어, 재사용하면 <b>부하가 올라갈 때 옵션 하나 때문에 커넥션이 무더기로
    /// 끊기고</b> 그 재접속이 부하를 더 키운다 — 열화가 붕괴를 앞당기는 정확한 역효과다.
    /// 그래서 이 상태는 옵션 무관하게 닫지 않고 그 프레임만 버린다
    /// (<see cref="RejectedByRateLimit"/> 와 같은 근거).
    /// </para>
    /// <para>
    /// <see cref="RejectedByRateLimit"/> 와도 구분한다: 속도 제한은 <b>이 클라이언트가 너무 많이
    /// 보낸 것</b>이고 열화는 <b>서버가 힘든 것</b>이다. 원인이 반대이므로 대시보드에서 갈려야
    /// 조치가 갈린다(속도 제한 증가 = 학대자, 열화 증가 = 증설 신호).
    /// </para>
    /// </remarks>
    RejectedByLoadShedding = 10,
}
