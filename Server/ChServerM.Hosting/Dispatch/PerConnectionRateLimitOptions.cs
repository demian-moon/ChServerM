using System;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// <see cref="PerConnectionRateLimiter"/>의 설정 — 커넥션 하나가 낼 수 있는 메시지 속도.
/// </summary>
/// <remarks>
/// <para>
/// 토큰 버킷: 커넥션마다 초당 <see cref="PermitsPerSecond"/> 개씩 채워지고
/// <see cref="BurstCapacity"/> 까지 쌓인다. 프레임마다 토큰 1개를 소비하고, 없으면 그
/// 프레임을 버린다(커넥션은 살아 있다).
/// </para>
/// <para>
/// <b>정상 최대 속도보다 넉넉하게 잡는다.</b> 이 값이 정상 클라이언트의 순간 속도를 넘지
/// 않으면 정상 트래픽이 버려진다. 버스트가 정상 급증(로딩 화면 → 게임 진입 등)을 흡수한다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 미들웨어 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class PerConnectionRateLimitOptions
{
    /// <summary>커넥션당 초당 채워지는 허가 수(지속 가능한 메시지 속도).</summary>
    public double PermitsPerSecond { get; set; } = 1000;

    /// <summary>커넥션당 버킷에 쌓일 수 있는 최대 허가 수(순간 버스트 허용).</summary>
    public int BurstCapacity { get; set; } = 2000;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (PermitsPerSecond <= 0 || !double.IsFinite(PermitsPerSecond))
        {
            throw new InvalidOperationException(
                $"{nameof(PermitsPerSecond)} 은 양의 유한수여야 한다. 현재 값: {PermitsPerSecond}.");
        }

        if (BurstCapacity <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(BurstCapacity)} 는 1 이상이어야 한다. 현재 값: {BurstCapacity}.");
        }
    }
}
