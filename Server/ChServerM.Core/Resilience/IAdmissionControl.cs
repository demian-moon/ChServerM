using System.Net;

namespace ChServerM.Resilience;

/// <summary>
/// 신규 커넥션을 수용할지 <b>동적으로</b> 판정하는 과부하 제어 축 (Phase 10, T-14).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 정적 상한이 못 막는 것.</b> 전송의 <c>MaxConnections</c> 는 정상 상태의
/// 하드 상한이지만, 그 상한 <b>안</b>에서 일어나는 연결 폭주(SYN 폭주·재접속 스톰)는
/// accept 루프와 핸드셰이크 CPU 를 그대로 때린다(T-16). 이 축은 수락 루프에서 "지금 이
/// 연결을 받아도 되는가"를 동적 신호(신규 연결 속도·자원 압박 등)로 판정한다 —
/// <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6).
/// </para>
/// <para>
/// <b>삽입 지점은 전송 계층이다.</b> 판정에 필요한 정보(원격 주소·수락 시점)는 전송만
/// 가진다. 계약은 Core(무의존)에 두고 구현은 전송 옵션으로 주입한다
/// (<c>RejectionNotice</c> 와 같은 주입 패턴). 거부 시 전송은 기존 거부 통지 경로를
/// 재사용한다 — 이 축은 "받을지 말지"만 답하고, 어떻게 닫고 통지할지는 전송이 안다.
/// </para>
/// <para>
/// <b>판정은 상태를 바꿀 수 있다(<see cref="TryAdmit"/>).</b> 토큰 버킷 같은 구현은
/// 수용할 때 토큰을 소비한다 — 이름의 <c>Try</c> 가 그 부수효과를 알린다
/// (<c>ITokenReplayGuard.TryClaim</c> 과 같은 명명).
/// </para>
/// <para>
/// <b>스레드 규약.</b> TCP 수락 루프는 단일 스레드지만, InMemory 는 다중 게시자이고 한
/// 인스턴스를 여러 전송이 공유할 수 있다 — 구현은 스레드 안전해야 한다.
/// </para>
/// <para>
/// <b>핫패스가 아니다.</b> 커넥션당 1회 호출이다(프레임당이 아니다). 무할당 규약의
/// 대상은 아니지만, 거부 경로에서 예외·비동기 대기를 만들지 않는다(그 자체가 공격 표면).
/// </para>
/// </remarks>
public interface IAdmissionControl
{
    /// <summary>신규 커넥션을 수용할지 판정한다.</summary>
    /// <param name="remoteEndPoint">원격 주소. 전송이 모르면 <see langword="null"/>
    /// (InMemory 등). 전역 속도 제한은 이 값을 쓰지 않고, IP별 제한은 쓴다.</param>
    /// <returns>수용/거부 판정. 거부면 전송이 통지 후 닫는다.</returns>
    AdmissionDecision TryAdmit(EndPoint? remoteEndPoint);
}
