namespace ChServerM.Diagnostics;

/// <summary>
/// 프레임워크가 판정하는 실패 원인.
/// </summary>
/// <remarks>
/// <para>
/// 이것은 <b>범용 반환 타입이 아니다.</b> 각 연산은 자기 전용 상태 enum
/// (<c>FrameDecodeStatus</c> 등)을 쓴다. <see cref="ErrorCode"/>는 그 상태들이
/// <b>커넥션 종료·메트릭·로그로 수렴할 때</b> 쓰는 공통 축이다.
/// </para>
/// <para>대역:</para>
/// <list type="table">
///   <item><term>0</term><description>실패 없음</description></item>
///   <item><term>1000번대</term><description>전송(Transport)</description></item>
///   <item><term>2000번대</term><description>프레이밍</description></item>
///   <item><term>3000번대</term><description>직렬화</description></item>
///   <item><term>4000번대</term><description>디스패치</description></item>
///   <item><term>5000번대</term><description>실행·백프레셔</description></item>
///   <item><term>6000번대</term><description>보안·인증</description></item>
///   <item><term>9000번대</term><description>설정·조립</description></item>
/// </list>
/// <para>
/// 레거시는 실패를 대부분 <b>표현하지 않았다</b> — 체크섬 검증은 <c>return true</c>,
/// 재시도·만료·백프레셔는 조용한 무동작이었다. 원인을 값으로 만들어야 관측 가능해진다.
/// </para>
/// </remarks>
public enum ErrorCode
{
    /// <summary>실패 없음.</summary>
    None = 0,

    // ── 1000 전송 ──────────────────────────────────────────────

    /// <summary>상대가 정상적으로 연결을 닫았다.</summary>
    ConnectionClosedByPeer = 1000,

    /// <summary>연결이 비정상 종료됐다(RST, 케이블 단절 등).</summary>
    ConnectionReset = 1001,

    /// <summary>연결 시도가 실패했다.</summary>
    ConnectAborted = 1002,

    /// <summary>제한 시간 안에 읽기·쓰기가 진행되지 않았다.</summary>
    TransportTimeout = 1003,

    /// <summary>동시 접속 상한에 걸려 수용을 거부했다.</summary>
    ConnectionLimitReached = 1004,

    /// <summary>서버가 종료 중이라 연결을 정리했다.</summary>
    ServerShuttingDown = 1005,

    // ── 2000 프레이밍 ──────────────────────────────────────────

    /// <summary>헤더가 규격에 맞지 않는다.</summary>
    MalformedFrame = 2000,

    /// <summary>선언된 페이로드 길이가 허용 상한을 넘었다.</summary>
    FrameTooLarge = 2001,

    /// <summary>알 수 없는 프로토콜 버전이다.</summary>
    ProtocolVersionMismatch = 2002,

    /// <summary>플래그 조합이 유효하지 않다.</summary>
    InvalidFrameFlags = 2003,

    // ── 3000 직렬화 ────────────────────────────────────────────

    /// <summary>페이로드를 역직렬화할 수 없다.</summary>
    DeserializationFailed = 3000,

    /// <summary>페이로드를 직렬화할 수 없다.</summary>
    SerializationFailed = 3001,

    /// <summary>이 메시지 타입에 등록된 직렬화기가 없다.</summary>
    SerializerNotRegistered = 3002,

    // ── 4000 디스패치 ──────────────────────────────────────────

    /// <summary>메시지 식별자에 대응하는 핸들러가 없다.</summary>
    HandlerNotFound = 4000,

    /// <summary>현재 커넥션 상태에서 허용되지 않는 메시지다.</summary>
    MessageNotAllowedInState = 4001,

    /// <summary>핸들러가 예외를 던졌다.</summary>
    HandlerFaulted = 4002,

    // ── 5000 실행·백프레셔 ─────────────────────────────────────

    /// <summary>큐가 가득 차 작업을 거부했다. <b>무제한으로 받지 않는다</b>(CLAUDE.md 9.6).</summary>
    QueueFull = 5000,

    /// <summary>송신 버퍼가 상한에 도달했다. 상대가 읽어가지 않는다.</summary>
    SendBackpressure = 5001,

    /// <summary>작업이 취소됐다.</summary>
    OperationCanceled = 5002,

    /// <summary>부하가 높아 비필수 메시지를 버렸다(우아한 열화). <b>커넥션은 닫지 않는다.</b></summary>
    /// <remarks>
    /// 6000번대(보안)가 아니라 여기인 이유: 이것은 거절당할 자격의 문제가 아니라
    /// <b>서버의 자원 상태</b> 문제다. 속도 제한(<see cref="RateLimitExceeded"/>)이 "이 클라이언트가
    /// 많이 보냈다" 라면 이쪽은 "서버가 힘들다" 이며, 이 코드가 늘면 학대자 차단이 아니라
    /// <b>증설</b>이 답이다.
    /// </remarks>
    LoadShed = 5003,

    // ── 6000 보안·인증 ─────────────────────────────────────────

    /// <summary>인증에 실패했다.</summary>
    AuthenticationFailed = 6000,

    /// <summary>인증은 됐으나 권한이 없다.</summary>
    AuthorizationFailed = 6001,

    /// <summary>무결성 검증에 실패했다. 변조 가능성이 있다.</summary>
    IntegrityCheckFailed = 6002,

    /// <summary>속도 제한에 걸렸다.</summary>
    RateLimitExceeded = 6003,

    /// <summary>보안 채널 핸드셰이크가 실패했다(ADR-0017). 커넥션을 닫는다.</summary>
    SecureChannelFailed = 6004,

    // ── 9000 설정·조립 ─────────────────────────────────────────

    /// <summary>옵션 값이 유효하지 않다. 시작 시점에 발견되어야 한다.</summary>
    InvalidConfiguration = 9000,

    /// <summary>필요한 구성 요소가 등록되지 않았다.</summary>
    ComponentNotRegistered = 9001,

    /// <summary>분류할 수 없는 내부 오류.</summary>
    Internal = 9999,
}
