using System;

namespace ChServerM.Framing;

/// <summary>
/// 프레임 페이로드에 적용된 변환.
/// </summary>
/// <remarks>
/// <para>
/// <b>플래그는 "이미 적용됐다"는 사실의 기록이지 "적용해달라"는 요청이 아니다.</b>
/// 수신 측은 이 값만 보고 역변환 순서를 정한다.
/// </para>
/// <para>
/// 적용 순서는 <b>압축 → 암호화</b>이고 해제는 그 역순이다. 순서를 고정하는 이유는
/// 암호화된 바이트는 압축되지 않기 때문이다.
/// </para>
/// <para>
/// 레거시는 압축 플래그를 헤더에 두고도 <b>압축 코드가 한 번도 실행되지 않았다.</b>
/// 플래그가 있다고 기능이 도는 게 아니다 — Phase 9에서 실제 경로를 붙일 때
/// 왕복 테스트로 증명한다.
/// </para>
/// </remarks>
[Flags]
public enum FrameFlags : ushort
{
    /// <summary>변환 없음.</summary>
    None = 0,

    /// <summary>페이로드가 압축돼 있다.</summary>
    Compressed = 1 << 0,

    /// <summary>페이로드가 암호화돼 있다.</summary>
    Encrypted = 1 << 1,

    /// <summary>더 큰 메시지의 조각이다.</summary>
    /// <remarks>
    /// 조각 재조립은 <b>상한이 있어야 한다.</b> 마지막 조각이 오지 않는
    /// 부분 메시지를 무한정 들고 있으면 그 자체가 메모리 고갈 공격 경로다.
    /// </remarks>
    Fragmented = 1 << 2,

    /// <summary>조각난 메시지의 마지막 조각이다.</summary>
    /// <remarks><see cref="Fragmented"/>와 함께여야 의미가 있다.</remarks>
    EndOfMessage = 1 << 3,
}
