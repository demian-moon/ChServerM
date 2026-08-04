namespace ChServerM.Framing;

/// <summary>
/// 프레임 디코딩 시도의 결과.
/// </summary>
/// <remarks>
/// <para>
/// 제네릭 <c>Result&lt;T&gt;</c> 대신 <b>연산 전용 상태 enum</b>을 쓴다. 무할당이고,
/// 어떤 실패가 가능한지가 타입에 다 드러나며, 호출자가 <c>switch</c>로 전부 다룰 수 있다.
/// </para>
/// <para>
/// <b>"데이터가 더 필요하다"와 "잘못됐다"를 반드시 구분한다.</b> 레거시는 체크섬 실패에
/// 예외를 던졌고 상위에서 그것을 삼킨 뒤 <b>상태 머신이 어긋난 채 파싱을 계속</b>했다.
/// 그 결과 하나의 손상된 프레임이 커넥션 전체를 영구히 오염시켰다.
/// </para>
/// <para><see cref="Decoded"/>와 <see cref="NeedMoreData"/>를 제외한 모든 값은 커넥션 종료 사유다.</para>
/// </remarks>
public enum FrameDecodeStatus : byte
{
    /// <summary>완전한 프레임이 아직 도착하지 않았다. 정상이며, 더 읽고 다시 시도한다.</summary>
    NeedMoreData = 0,

    /// <summary>프레임 하나를 온전히 읽었다.</summary>
    Decoded = 1,

    /// <summary>헤더가 규격을 위반했다. 어디서부터 다시 맞춰야 할지 알 수 없으므로 커넥션을 닫는다.</summary>
    Malformed = 2,

    /// <summary>선언된 페이로드 길이가 허용 상한을 넘었다.</summary>
    /// <remarks>
    /// <b>버퍼를 잡기 전에</b> 판정해야 한다. 길이 필드를 믿고 먼저 할당하면
    /// 4바이트짜리 거짓말로 서버 메모리를 고갈시킬 수 있다.
    /// </remarks>
    TooLarge = 3,

    /// <summary>알 수 없는 프로토콜 버전이다.</summary>
    VersionMismatch = 4,

    /// <summary>정의되지 않은 플래그 비트가 켜져 있다.</summary>
    /// <remarks>
    /// <para>
    /// <b>모르는 비트를 무시하지 않는다.</b> 무시하면 압축된 페이로드를 원본으로 착각해
    /// 핸들러에 넘기게 되고, 그것은 조용한 오동작이다 — 레거시 결함의 대표 유형이다.
    /// </para>
    /// <para>
    /// 플래그를 추가할 때는 와이어 포맷의 프로토콜 버전을 올린다
    /// (고정 헤더라면 <c>FrameHeader.Version</c>). 버전 필드가 있는 이유가 정확히 이것이다.
    /// </para>
    /// </remarks>
    InvalidFlags = 5,
}
