using System.Diagnostics.CodeAnalysis;

namespace ChServerM.Security;

/// <summary>
/// 이름으로 시크릿(암호·연결 문자열·API 키)을 찾는 계약 (Phase 9 시크릿 관리).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 시크릿이 필요한 지점(TLS PFX 암호, Phase 13 의 저장소 연결 문자열,
/// 앱의 인증 키)마다 환경변수 읽기를 각자 발명하면, 어느 날 하나가 설정 파일 리터럴로
/// 퇴행한다 — 레거시가 정확히 그랬다(MongoDB 연결 문자열에 계정·암호를 코드에 커밋,
/// <c>ServerGlobals.cs:103</c>). 이 계약이 소비의 단일 통로다: 조립 코드는 원천에서
/// 꺼내 옵션에 넘기고, <b>리터럴은 코드·설정 파일 어디에도 두지 않는다</b>.
/// </para>
/// <para>
/// <b>부재는 정상이다(false).</b> 시크릿이 필수인지는 조립하는 쪽이 판정한다 —
/// 필수 시크릿의 부재는 조립 시점 예외로 승격시켜 서버가 뜨기 전에 드러낸다.
/// </para>
/// <para>
/// <b>존재하지만 빈 값 = 부재다.</b> "설정했다고 착각"(변수는 만들고 값을 빠뜨림)이
/// 가장 흔한 사고다 — 빈 암호로 조용히 진행하는 것보다 "없다"로 드러나는 편이 안전측이다.
/// </para>
/// <para>
/// <b>메모리 보안을 약속하지 않는다.</b> .NET 문자열은 불변이라 지울 수 없다 —
/// 지워지는 척하는 타입(<c>SecureString</c> 류)은 만들지 않는 것이 정직하다
/// (레거시 가짜 체크섬과 같은 부류의 장치를 만들지 않는다). 시크릿을 장수 필드·
/// public 표면에 올리지 않는 것이 현실적 완화다(T-10).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 구현은 스레드 안전해야 한다. 조회는 시작·재연결 시점의
/// 저빈도 경로다 — 핫패스 규약의 대상이 아니다.
/// </para>
/// </remarks>
public interface ISecretSource
{
    /// <summary>이름으로 시크릿을 찾는다.</summary>
    /// <param name="name">시크릿 이름.</param>
    /// <param name="value">찾았으면 값. 빈 값은 부재로 취급되어 여기 실리지 않는다.</param>
    /// <returns>존재하고 비어 있지 않으면 <see langword="true"/>.</returns>
    bool TryGetSecret(string name, [NotNullWhen(true)] out string? value);
}
