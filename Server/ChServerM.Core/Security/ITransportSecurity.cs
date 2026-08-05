using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Security;

/// <summary>
/// 전송 보안 축 — 수립 직후의 커넥션 바이트 경로를 보안 채널로 감싼다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 기밀성·무결성·서버 인증을 전송 축과 독립적으로 조립 시점에
/// 끼우는 경계다(ADR-0017). 이 축이 데코레이터인 덕분에 전송(TCP·인메모리)마다
/// 보안을 다시 구현하지 않고, 소켓 없이도 보안 경로를 통합 테스트할 수 있다.
/// 더 중요한 존재 이유는 <b>자체 암호 프로토콜을 설계할 자리를 없애는 것</b>이다 —
/// 구현체는 검증된 프로토콜(기본: TLS 1.3, <c>ChServerM.Security.Tls</c>)에 위임한다.
/// </para>
/// <para>
/// <b>레거시 대응.</b> 레거시의 보안 계층은 전량 폐기 판정이다(docs/legacy/07-security):
/// 미인증 RSA 키 교환(MITM 이 키를 대체해 자격증명 복원), 반복 키 XOR "암호화",
/// 세션 고정 IV + 인증 없는 CBC(패딩 오라클). 이 계약은 그 재발을 개별 금지가 아니라
/// <b>구조로</b> 막는다 — 키 교환·nonce·무결성이 전부 구현체(TLS) 내부로 들어가
/// 프레임워크 코드에 다시 나타나지 않는다.
/// </para>
/// <para>
/// <b>적용 지점과 순서.</b> 커넥션 수립 직후, 프레이밍 시작 전. 호스팅이 순서를
/// 강제한다. 버전 협상 핸드셰이크(ADR-0017 결정 3)는 이 채널이 확립된 <b>뒤에</b>
/// 채널 안에서 수행된다 — 그래야 다운그레이드 방지가 TLS 의 몫이 된다.
/// </para>
/// <para>
/// <b>실패 규약.</b> 핸드셰이크 실패는 예외가 아니라 <see cref="SecureChannelResult"/>로
/// 보고한다. 잘못된 핸드셰이크의 폭주는 공격 시나리오다(THREAT-MODEL T-16) —
/// 커넥션마다 예외를 던지면 공격 비용을 서버가 증폭한다. 구현체는 내부 라이브러리
/// 예외를 경계에서 한 번 상태로 번역한다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 구현체 인스턴스 하나를 모든 커넥션이 공유 호출할 수 있어야
/// 한다(무상태 또는 스레드 안전) — 프레임 디코더와 같은 규약이다. 반환된 채널은
/// 그 커넥션 전용이다.
/// </para>
/// <para>
/// <b>이 축을 끼우지 않으면 평문이다.</b> "없음"도 유효한 조립이지만(내부망·개발),
/// 커넥션 내 리플레이 차단(ADR-0017 결정 4 — TLS 레코드 계층의 몫)도 함께 사라진다.
/// TLS 를 내장한 전송(QUIC)은 이 축을 적용하지 않는다 — 이중 암호화 금지.
/// </para>
/// </remarks>
public interface ITransportSecurity
{
    /// <summary>서버 측 핸드셰이크를 수행하고 보안 채널을 확립한다.</summary>
    /// <param name="transport">원본(암호문 측) 양방향 파이프. 채널이 확립된 뒤에는 직접 읽고 쓰면 안 된다 — 평문은 <see cref="ISecureChannel"/>로만 오간다.</param>
    /// <param name="cancellationToken">커넥션 종료 토큰. 핸드셰이크 타임아웃(Phase 10)도 이 토큰으로 합류한다.</param>
    /// <returns>확립된 채널 또는 실패 상태. 취소는 <see cref="SecureChannelStatus.Canceled"/>로 보고된다 — 예외를 던지지 않는다.</returns>
    ValueTask<SecureChannelResult> SecureAsServerAsync(IDuplexPipe transport, CancellationToken cancellationToken);

    /// <summary>클라이언트 측 핸드셰이크를 수행하고 보안 채널을 확립한다.</summary>
    /// <param name="transport">원본(암호문 측) 양방향 파이프. 채널이 확립된 뒤에는 직접 읽고 쓰면 안 된다.</param>
    /// <param name="cancellationToken">커넥션 종료 토큰.</param>
    /// <returns>확립된 채널 또는 실패 상태. 검증 대상 호스트명·인증서 신뢰 정책 같은 방향별 세부는 Core 가 아니라 구현체 옵션이 담는다 — Core 표면 최소화.</returns>
    ValueTask<SecureChannelResult> SecureAsClientAsync(IDuplexPipe transport, CancellationToken cancellationToken);
}
