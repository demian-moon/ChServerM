using System;
using System.Security.Cryptography.X509Certificates;

namespace ChServerM.Security.Tls;

/// <summary>
/// 서버 인증서의 원천 — 핸드셰이크마다 "지금 유효한 인증서"를 답한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 회전(rotation).</b> 인증서는 만료된다(Let's Encrypt 류는 90일).
/// 고정 인스턴스(<see cref="TlsSecurityOptions.ServerCertificate"/>)만 있으면 갱신된
/// 인증서를 집으려고 서버를 재시작해야 한다. 이 계약은 인증서 해석을 핸드셰이크
/// 시점으로 미뤄, 원천의 교체가 <b>새 핸드셰이크부터 즉시</b> 반영되게 한다.
/// 진행 중인 커넥션은 영향을 받지 않는다 — TLS 세션은 핸드셰이크 시점의 인증서로
/// 이미 확립됐다.
/// </para>
/// <para>
/// <b>수명·소유권.</b> 원천과, 원천이 돌려주는 인증서의 수명은 원천 구현이 소유한다.
/// 호출자(<see cref="TlsTransportSecurity"/>)는 반환값을 핸드셰이크 동안만 쓰고
/// 폐기하지 않는다. 원천을 만든 쪽(조립)이 원천을 <see cref="IDisposable.Dispose"/> 한다.
/// 구현은 교체된 구세대 인증서를 <b>즉시 폐기하면 안 된다</b> — 진행 중 핸드셰이크가
/// 참조하고 있을 수 있다(<see cref="FileCertificateSource"/> 의 세대 보관 참조).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 여러 커넥션의 핸드셰이크가 동시에 부른다 — 구현은 스레드
/// 안전해야 한다.
/// </para>
/// <para>
/// Core 계약이 아니라 이 어셈블리 소속인 이유: 인증서 원천은 교체 가능한 축이 아니라
/// TLS 어댑터의 운영 관심사다(ADR-0017 의 Core 표면 최소화와 같은 결).
/// </para>
/// </remarks>
public interface IServerCertificateSource : IDisposable
{
    /// <summary>현재 유효한 서버 인증서(개인키 포함)를 돌려준다.</summary>
    /// <returns>핸드셰이크에 제시할 인증서. <see langword="null"/>을 돌려주지 않는다.</returns>
    X509Certificate2 GetCertificate();
}
