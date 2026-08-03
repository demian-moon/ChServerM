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
    RejectedByBackpressure = 5,

    /// <summary>핸들러가 예외를 던졌다.</summary>
    Faulted = 6,

    /// <summary>처리 중 취소됐다(커넥션 종료·서버 종료).</summary>
    Canceled = 7,
}
