using System;

namespace ChServerM.Security.Tls;

/// <summary>
/// <see cref="FileCertificateSource"/>의 설정 — 어느 파일에서, 얼마나 자주 재확인하는가.
/// </summary>
/// <remarks>
/// <para>
/// 형식은 둘 중 하나만 지정한다: <b>PFX 단독</b>(<see cref="PfxPath"/>) 또는
/// <b>PEM 쌍</b>(<see cref="CertificatePemPath"/> + <see cref="PrivateKeyPemPath"/> —
/// cert-manager/Let's Encrypt 가 떨구는 표준 형식). 혼합·공백은 조립 시점 예외다.
/// </para>
/// <para>
/// <b>⚠ <see cref="PfxPassword"/> 는 코드·설정 파일에 리터럴로 두지 않는다.</b>
/// 환경변수·시크릿 저장소에서 읽어 넘긴다 — 설정 파일의 키가 레거시의
/// 하드코딩 자격증명 결함이다(Phase 9 시크릿 관리 항목).
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 원천 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class FileCertificateOptions
{
    /// <summary>PKCS#12(PFX) 파일 경로. PEM 쌍과 상호 배타.</summary>
    public string? PfxPath { get; set; }

    /// <summary>PFX 암호. 없으면 <see langword="null"/>.</summary>
    /// <remarks>
    /// <c>ISecretSource</c>(Core) 경유가 참조 패턴이다 — 리터럴 금지(위 모듈 문서):
    /// <code>
    /// ISecretSource secrets = new EnvironmentSecretSource("CHSM_");
    /// options.PfxPassword = secrets.TryGetSecret("PFX_PASSWORD", out string? pw) ? pw : null;
    /// </code>
    /// </remarks>
    public string? PfxPassword { get; set; }

    /// <summary>PEM 인증서(체인) 파일 경로. <see cref="PrivateKeyPemPath"/>와 쌍으로 지정한다.</summary>
    public string? CertificatePemPath { get; set; }

    /// <summary>PEM 개인키 파일 경로.</summary>
    public string? PrivateKeyPemPath { get; set; }

    /// <summary>파일 변경 재확인 주기. 기본 5분. <see cref="TimeSpan.Zero"/> = 자동 재확인 끔(명시 <c>Reload()</c>만).</summary>
    /// <remarks>
    /// 재확인은 핸드셰이크 시점에만 일어난다 — 전용 타이머·감시 스레드가 없다(9.5).
    /// cert-manager 류는 만료 수십 일 전에 회전하므로 분 단위 지연은 문제가 아니다.
    /// </remarks>
    public TimeSpan ReloadCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">형식 조합이 유효하지 않을 때.</exception>
    public void Validate()
    {
        bool hasPfx = !string.IsNullOrEmpty(PfxPath);
        bool hasPemCertificate = !string.IsNullOrEmpty(CertificatePemPath);
        bool hasPemKey = !string.IsNullOrEmpty(PrivateKeyPemPath);

        if (hasPfx && (hasPemCertificate || hasPemKey))
        {
            throw new InvalidOperationException(
                $"{nameof(PfxPath)} 와 PEM 경로가 함께 지정됐다 — 어느 쪽이 진짜인지 모호하다. 한 형식만 쓴다.");
        }

        if (!hasPfx && !(hasPemCertificate && hasPemKey))
        {
            throw new InvalidOperationException(
                $"{nameof(PfxPath)} 단독 또는 {nameof(CertificatePemPath)}+{nameof(PrivateKeyPemPath)} 쌍 중 " +
                "하나를 지정한다. PEM 은 인증서와 개인키 둘 다 필요하다.");
        }

        if (ReloadCheckInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(ReloadCheckInterval)} 은 음수일 수 없다: {ReloadCheckInterval}. " +
                "자동 재확인을 끄려면 Zero 를 쓴다.");
        }
    }
}
