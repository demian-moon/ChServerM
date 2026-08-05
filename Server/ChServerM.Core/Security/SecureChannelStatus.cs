namespace ChServerM.Security;

/// <summary>
/// <see cref="ITransportSecurity"/> 핸드셰이크 한 번의 결과 상태.
/// </summary>
/// <remarks>
/// 연산별 상태 enum 규약(Phase 1 에러 모델)의 보안 축 구현이다.
/// 기본값 <see cref="None"/>은 "확립됨"이 아니다 — 존재하지 않는 결과의 기본값은
/// 가장 제한적인 값이어야 한다는 원칙(레거시 <c>AllowedPkState</c>가 기본값으로
/// 전부 허용이었던 결함의 역).
/// </remarks>
public enum SecureChannelStatus
{
    /// <summary>기본값 센티넬. 유효한 결과가 아니다 — 이 값이 관측되면 초기화되지 않은 <see cref="SecureChannelResult"/>가 흘러간 조립 버그다.</summary>
    None = 0,

    /// <summary>보안 채널이 확립됐다.</summary>
    Established = 1,

    /// <summary>핸드셰이크가 실패했다. 커넥션을 닫는다 — 재시도하지 않는다.</summary>
    HandshakeFailed = 2,

    /// <summary>핸드셰이크가 완료되기 전에 취소됐다(커넥션 종료·타임아웃).</summary>
    Canceled = 3,
}
