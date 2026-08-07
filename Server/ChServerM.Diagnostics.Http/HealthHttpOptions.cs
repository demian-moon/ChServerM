using System;

namespace ChServerM.Diagnostics.Http;

/// <summary>
/// <see cref="HealthHttpEndpoint"/> 의 설정 — 주소와 프로브 경로 (Phase 11 관측, ADR-0024).
/// </summary>
/// <remarks>
/// <b>별도 admin 주소를 쓴다.</b> 헬스 프로브는 데이터 평면(게임·서비스 트래픽)과 다른
/// 포트에 두는 것이 관례다 — 오케스트레이터·모니터링만 접근하고, 외부에 노출하지 않는다.
/// 기본값은 루프백이라 사이드카·같은 파드 안에서만 닿는다.
/// </remarks>
public sealed class HealthHttpOptions
{
    /// <summary>HttpListener 접두사(수신 주소). 반드시 <c>/</c> 로 끝난다.</summary>
    /// <remarks>
    /// 기본은 루프백 <c>http://localhost:8081/</c>. 컨테이너에서 오케스트레이터가 다른
    /// 인터페이스로 프로브하면 <c>http://+:8081/</c> 등으로 바꾼다(그때는 URL ACL·방화벽을 함께 본다).
    /// </remarks>
    public string Prefix { get; set; } = "http://localhost:8081/";

    /// <summary>liveness 프로브 경로. 반드시 <c>/</c> 로 시작한다.</summary>
    public string LivenessPath { get; set; } = "/healthz";

    /// <summary>readiness 프로브 경로. 반드시 <c>/</c> 로 시작한다.</summary>
    public string ReadinessPath { get; set; } = "/readyz";

    /// <summary>런타임 진단 스냅샷 경로. <see langword="null"/>이면 노출하지 않는다.</summary>
    /// <remarks>
    /// <para>
    /// <b>기본이 <see langword="null"/>(미노출)인 이유.</b> 진단 스냅샷에는 클라이언트 주소
    /// 표본이 들어가고 이 엔드포인트는 평문·무인증이다. 프로브(<c>/healthz</c>·<c>/readyz</c>)는
    /// 오케스트레이터가 반드시 필요로 하지만 진단은 <b>사람이 필요할 때 켜는 것</b>이므로,
    /// <b>명시적으로 켜야 열리게</b> 둔다 — 기본값이 덜 노출하는 쪽이어야 한다.
    /// </para>
    /// <para>켤 때는 admin 주소가 외부에 닿지 않는지 함께 확인한다.</para>
    /// </remarks>
    public string? DiagnosticsPath { get; set; }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prefix) || !Prefix.EndsWith('/'))
        {
            throw new InvalidOperationException(
                $"{nameof(Prefix)}는 비어 있지 않고 '/'로 끝나야 한다(HttpListener 규약). 현재 값: '{Prefix}'");
        }

        if (!LivenessPath.StartsWith('/') || !ReadinessPath.StartsWith('/'))
        {
            throw new InvalidOperationException("프로브 경로는 '/'로 시작해야 한다.");
        }

        if (string.Equals(LivenessPath, ReadinessPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(LivenessPath)}와 {nameof(ReadinessPath)}가 같으면 프로브를 구분할 수 없다.");
        }

        if (DiagnosticsPath is not null)
        {
            if (!DiagnosticsPath.StartsWith('/'))
            {
                throw new InvalidOperationException($"{nameof(DiagnosticsPath)} 는 '/'로 시작해야 한다.");
            }

            if (string.Equals(DiagnosticsPath, LivenessPath, StringComparison.Ordinal)
                || string.Equals(DiagnosticsPath, ReadinessPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{nameof(DiagnosticsPath)} 가 프로브 경로와 같으면 진단이 프로브 응답을 가로챈다.");
            }
        }
    }
}
