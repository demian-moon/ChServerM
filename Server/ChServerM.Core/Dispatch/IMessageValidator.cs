namespace ChServerM.Dispatch;

/// <summary>
/// 역직렬화된 메시지의 필드 범위·의미 유효성을 판정하는 계약 (Phase 9 입력 검증).
/// </summary>
/// <typeparam name="TMessage">검증할 메시지 타입.</typeparam>
/// <remarks>
/// <para>
/// <b>존재 이유 — 역직렬화 성공 ≠ 유효한 값.</b> 직렬화 축의 <c>TryDeserialize</c> 는
/// "바이트가 스키마에 맞는가"만 답한다. <c>HP = -999999</c>, 좌표 NaN, 음수 수량처럼
/// <b>스키마는 맞지만 의미가 틀린</b> 페이로드는 통과한다 — 와이어 값 신뢰가 레거시의
/// 반복 패턴이었다(T-22). 유효 범위는 워크로드 소관이라(ADR-0004) 프레임워크가 정할 수
/// 없지만, <b>검증이 끼는 자리와 실패 규약</b>은 프레임워크가 강제한다:
/// 검증기를 등록한 라우트에서는 역직렬화 직후·핸들러 도달 전에 반드시 실행되고,
/// 실패하면 핸들러는 실행되지 않는다(핸들러 안 검증은 하나쯤 빠뜨리는 것이 기본값이다).
/// </para>
/// <para>
/// <b>실패 처리는 역직렬화 실패와 같은 부류다.</b> 범위 밖 값은 클라이언트 버그거나
/// 조작이다 — <c>DispatchStatus.DeserializationFailed</c> 로 수렴하고, 종료 여부는
/// 기존 <c>CloseOnDeserializationFailure</c> 정책(기본 종료)이 정한다.
/// </para>
/// <para>
/// <b>실패는 값이다</b>(T-16). 범위 밖 값에 예외를 던지지 않는다 — 원격 입력이 만드는
/// 실패 경로다. 사유 로깅이 필요하면 구현이 직접 남긴다(로거 주입은 구현의 몫).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션의 디스패치 순차 컨텍스트에서 호출된다. 서로 다른
/// 커넥션이 같은 인스턴스를 동시에 부르므로 구현은 무상태이거나 스레드 안전해야 한다.
/// 핫패스다 — 검증은 필드 비교 수준이어야 하며 할당·IO 를 만들지 않는다.
/// </para>
/// </remarks>
public interface IMessageValidator<TMessage>
{
    /// <summary>메시지가 유효한지 판정한다.</summary>
    /// <param name="message">역직렬화된 메시지.</param>
    /// <returns>유효하면 <see langword="true"/>. 아니면 핸들러가 실행되지 않는다.</returns>
    bool Validate(in TMessage message);
}
