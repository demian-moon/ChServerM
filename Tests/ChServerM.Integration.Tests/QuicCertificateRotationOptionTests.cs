using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ChServerM.Transport.Quic;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// QUIC 서버 인증서 원천 옵션의 조립 시점 검증 — 감사 2026-08-18 T-4.
/// </summary>
/// <remarks>
/// 고정 인스턴스(<see cref="QuicTransportOptions.ServerCertificate"/>)와 회전 원천
/// (<see cref="QuicTransportOptions.ServerCertificateContextSource"/>)은 상호 배타이고,
/// 서버 전송에는 둘 중 하나가 필수다 — TLS 어댑터의 상호 배타 규율과 대칭이다.
/// 실런타임 회전은 msquic 종속이라 옵션 계약 수준으로 고정한다(감사 문서의 판정).
/// </remarks>
public sealed class QuicCertificateRotationOptionTests
{
    [Fact]
    public void Fixed_certificate_and_context_source_are_mutually_exclusive()
    {
        using X509Certificate2 certificate = CreateSelfSigned();
        QuicTransportOptions options = new()
        {
            ServerCertificate = certificate,
            ServerCertificateContextSource = static () => throw new NotSupportedException("호출되면 안 된다."),
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Context_source_alone_satisfies_server_certificate_requirement()
    {
        QuicTransportOptions options = new()
        {
            ServerCertificateContextSource = static () => throw new NotSupportedException("검증은 호출하지 않는다."),
        };

        // 원천만으로 서버 전송 요건이 성립한다 — 회전 경로가 1급 조립이다(T-4).
        options.Validate(requireServerCertificate: true);
    }

    [Fact]
    public void Server_transport_requires_certificate_or_context_source()
    {
        Assert.Throws<InvalidOperationException>(
            static () => new QuicTransportOptions().Validate(requireServerCertificate: true));
    }

    [Fact]
    public void Client_options_do_not_require_certificate()
    {
        // 클라이언트 방향은 인증서 없이 유효하다 — 기존 동작 유지 확인.
        new QuicTransportOptions().Validate();
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=chsm-quic-option-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
