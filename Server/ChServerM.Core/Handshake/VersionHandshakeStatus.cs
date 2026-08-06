namespace ChServerM.Handshake;

/// <summary>
/// 핸드셰이크 프레임 파싱 한 번의 결과.
/// </summary>
/// <remarks>
/// <see cref="VersionHandshakeCodec"/> 전용 상태다. <c>ErrorCode</c> 로의 수렴은
/// 호출자(호스팅의 협상 단계)가 한다 — <see cref="Malformed"/> 는
/// <c>ErrorCode.MalformedFrame</c> 으로 커넥션 종료가 정답이다.
/// </remarks>
public enum VersionHandshakeStatus
{
    /// <summary>판정 전. 이 값이 관측되면 초기화 누락 버그다(센티넬).</summary>
    None = 0,

    /// <summary>프레임이 아직 다 도착하지 않았다. 더 읽고 다시 시도한다.</summary>
    NeedMoreData,

    /// <summary>프레임을 읽어냈다.</summary>
    Success,

    /// <summary>
    /// 동결 레이아웃에 맞지 않는 바이트다. 커넥션을 닫아야 한다 —
    /// 협상 이전에는 합의된 형식이 없으므로 복구를 시도할 근거 자체가 없다.
    /// </summary>
    Malformed,
}
