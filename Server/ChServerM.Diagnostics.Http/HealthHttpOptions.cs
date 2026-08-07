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
    }
}
