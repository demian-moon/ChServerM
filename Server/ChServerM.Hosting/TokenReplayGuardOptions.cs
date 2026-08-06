using System;

namespace ChServerM.Hosting;

/// <summary>
/// <see cref="InMemoryTokenReplayGuard"/>의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Ttl"/> 은 토큰 수명 이상이어야 한다.</b> 이 값은 만료 검증이 아니라
/// 등록부 메모리를 유계로 만드는 장치다 — 토큰 수명보다 짧으면 TTL 축출 후 아직 유효한
/// 토큰이 재사용 가능해진다. 프레임워크는 토큰 형식을 모르므로 이 관계를 검증할 수 없다 —
/// 조립하는 쪽의 책임이다.
/// </para>
/// <para>
/// <b><see cref="MaxEntries"/> 산정.</b> 최악 메모리 = 항목 수 × (토큰 크기 + 오버헤드).
/// 필요량은 대략 <c>로그인 속도 × Ttl</c> 이다 — 여유를 두되, 포화는 신규 로그인 거부이므로
/// 관측(경고 로그)을 보고 조정한다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 가드 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class TokenReplayGuardOptions
{
    /// <summary>기본 등록부 상한. 10만 항목.</summary>
    public const int DefaultMaxEntries = 100_000;

    /// <summary>등록부에 유지할 최대 항목 수.</summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    /// <summary>항목이 만료 정리 대상이 되기까지의 시간. 기본 5분.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (MaxEntries <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxEntries)} 는 1 이상이어야 한다. 현재 값: {MaxEntries}. " +
                "무제한 등록부는 메모리 고갈 경로다(9.6).");
        }

        if (Ttl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(Ttl)} 은 양수여야 한다. 현재 값: {Ttl}.");
        }
    }
}
