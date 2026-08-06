namespace ChServerM.Features;

/// <summary>
/// 버전 협상(ADR-0017)으로 확정된 이 커넥션의 프레이밍 프로토콜 버전.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 협상 결과는 커넥션의 속성이지 서버 전역 설정이 아니다 — 롤링 배포
/// 중에는 커넥션마다 다른 버전이 합의될 수 있다. 상위 계층(관측·진단, 이후 버전별
/// 프레이밍 선택)이 결과를 조회할 유일한 통로가 이 피처다.
/// </para>
/// <para>
/// 협상을 조립하지 않은 커넥션에는 이 피처가 <b>없다</b>(<see langword="null"/>) —
/// "협상 안 함"과 "버전 1로 협상됨"은 다른 상태다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 협상 완료 시점(프레이밍 시작 전)에 한 번 등록되고 이후 불변이다.
/// <see cref="IFeatureCollection"/> 의 "수립 시점 등록, 이후 읽기 전용" 규약을 따른다.
/// </para>
/// </remarks>
public interface IProtocolVersionFeature
{
    /// <summary>협상으로 확정된 버전. 0(센티넬)일 수 없다.</summary>
    ushort NegotiatedVersion { get; }
}
