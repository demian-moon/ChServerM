using System;

namespace ChServerM.Hosting;

/// <summary>
/// <see cref="ConnectionRateAdmissionControl"/>의 설정 — 토큰 버킷 파라미터.
/// </summary>
/// <remarks>
/// <para>
/// 토큰 버킷: 초당 <see cref="PermitsPerSecond"/> 개씩 채워지고 최대 <see cref="BurstCapacity"/>
/// 개까지 쌓인다. 신규 연결마다 토큰 1개를 소비하고, 없으면 거부한다.
/// </para>
/// <list type="bullet">
///   <item><description><b><see cref="PermitsPerSecond"/></b> — 지속 가능한 초당 신규 연결
///   수. 정상 재접속·부팅 러시를 수용하되 폭주는 막는 값으로 잡는다.</description></item>
///   <item><description><b><see cref="BurstCapacity"/></b> — 순간 허용 폭. 배포 직후 다수
///   클라이언트가 동시에 붙는 정상 버스트를 흡수한다. 너무 크면 폭주 방어가 무뎌진다.</description></item>
/// </list>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class ConnectionRateAdmissionControlOptions
{
    /// <summary>초당 채워지는 허가 수(지속 가능한 신규 연결 속도).</summary>
    public double PermitsPerSecond { get; set; } = 100;

    /// <summary>버킷에 쌓일 수 있는 최대 허가 수(순간 버스트 허용).</summary>
    public int BurstCapacity { get; set; } = 200;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (PermitsPerSecond <= 0 || !double.IsFinite(PermitsPerSecond))
        {
            throw new InvalidOperationException(
                $"{nameof(PermitsPerSecond)} 은 양의 유한수여야 한다. 현재 값: {PermitsPerSecond}. " +
                "0 은 모든 연결 거부를 뜻하므로, 그럴 의도면 전송을 unbind 한다.");
        }

        if (BurstCapacity <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(BurstCapacity)} 는 1 이상이어야 한다. 현재 값: {BurstCapacity}.");
        }
    }
}
