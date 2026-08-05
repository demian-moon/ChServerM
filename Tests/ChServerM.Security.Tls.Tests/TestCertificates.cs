using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChServerM.Security.Tls.Tests;

/// <summary>
/// 테스트 전용 자가서명 인증서 생성기.
/// </summary>
/// <remarks>
/// <para>
/// ECDSA P-256 / CN=localhost / serverAuth EKU. 생성한 키를 PFX 로 내보냈다가
/// 다시 로드하는 이유: Windows Schannel 은 ephemeral 키를 TLS 서버 자격증명으로
/// 쓰지 못한다 — 재로드가 키를 키 저장소(임시 파일)에 올린다. Linux(OpenSSL)에서는
/// 어느 쪽이든 동작하므로 공통 경로 하나만 둔다.
/// </para>
/// <para>제품 코드가 아니다 — 프로덕션 인증서 로딩·회전은 Phase 9 잔여 항목이다.</para>
/// </remarks>
internal static class TestCertificates
{
    /// <summary>7일 유효한 localhost 자가서명 인증서를 만든다. 호출자가 폐기한다.</summary>
    public static X509Certificate2 CreateSelfSigned()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
