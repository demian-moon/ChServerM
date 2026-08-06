using System;
using System.Diagnostics.CodeAnalysis;
using ChServerM.Security;

namespace ChServerM.Hosting;

/// <summary>
/// 환경변수에서 시크릿을 읽는 원천 (12-factor 표준 경로).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 컨테이너·CI·systemd 전부가 환경변수 주입을 기본 지원한다 —
/// 설정 파일에 리터럴을 두지 않는 가장 낮은 문턱의 경로다.
/// </para>
/// <para>
/// 프리픽스는 충돌 방지다: <c>new EnvironmentSecretSource("CHSM_")</c> 에서
/// <c>TryGetSecret("PFX_PASSWORD", ...)</c> 는 <c>CHSM_PFX_PASSWORD</c> 를 읽는다 —
/// 이름 공간 없는 환경변수는 다른 프로세스 설정과 섞인다.
/// </para>
/// <para><b>스레드 규약.</b> 무상태다. 어디서 불러도 안전하다.</para>
/// </remarks>
public sealed class EnvironmentSecretSource : ISecretSource
{
    private readonly string _prefix;

    /// <summary>프리픽스를 지정해 원천을 만든다.</summary>
    /// <param name="prefix">모든 이름 앞에 붙일 접두. 기본은 없음.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/>가 <see langword="null"/>일 때.</exception>
    public EnvironmentSecretSource(string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        _prefix = prefix;
    }

    /// <inheritdoc />
    public bool TryGetSecret(string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        string? raw = Environment.GetEnvironmentVariable(_prefix + name);

        // 빈 값 = 부재(계약) — "변수는 만들고 값을 빠뜨린" 착각이 조용히 진행되지 않게.
        if (string.IsNullOrEmpty(raw))
        {
            value = null;
            return false;
        }

        value = raw;
        return true;
    }
}
