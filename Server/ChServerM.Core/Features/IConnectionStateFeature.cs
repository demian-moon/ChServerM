namespace ChServerM.Features;

/// <summary>
/// 커넥션의 프로토콜 상태 집합 — 상태별 메시지 화이트리스트의 판정 근거.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "인증 전에는 인증 후 메시지를 받지 않는다"(THREAT-MODEL T-19)를
/// 판정하려면 커넥션이 지금 어느 상태인지가 어딘가에 있어야 한다. 세션 계층(Phase 13)
/// 이전에도 성립해야 하므로 커넥션 feature 로 둔다 — 커넥션 단위로 유지할 것은
/// <c>IConnection.Features</c>에 둔다는 규약(<c>MessageContext</c> 문서) 그대로다.
/// </para>
/// <para>
/// <b>비트마스크다.</b> 각 비트의 의미(연결 직후·인증됨·로비·게임 중 …)는 앱이 정의한다 —
/// 프레임워크가 상태 이름을 정하는 순간 워크로드 전제가 Core 에 들어온다(ADR-0004 위반).
/// 판정은 any-of: 허용 마스크와 현재 집합의 교집합이 비어 있지 않으면 통과다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션의 디스패치 순차 컨텍스트 전용이다 — 프레임 디스패치는
/// 커넥션 안에서 순차이므로(읽기 루프 + 파티션 배타, ADR-0008) 핸들러가 쓴 값을
/// 다음 프레임의 미들웨어가 읽는 것이 안전하다. 디스패치 밖(다른 스레드)에서
/// 읽고 쓰면 그 보장이 없다.
/// </para>
/// <para>
/// <b>레거시 대응.</b> <c>AllowedPkState</c>는 존재하지 않는 세션의 기본값이
/// <c>A_SC_ANY_STATE</c>(전부 허용)였다(docs/legacy/06-session-user) — 여기서는
/// 상태의 부여·판정 모두 명시적이고, 판정 쪽(<c>MessageStateFilterMiddleware</c>)이
/// 기본 거부다.
/// </para>
/// </remarks>
public interface IConnectionStateFeature
{
    /// <summary>현재 상태 집합(비트마스크). 상태 전이는 핸들러가 이 값을 바꾸는 것이다.</summary>
    uint States { get; set; }
}
