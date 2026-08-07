using System;

namespace ChServerM.Hosting;

/// <summary>
/// <see cref="PerAddressConnectionRateAdmissionControl"/>의 설정 — 주소별 토큰 버킷과 슬롯 수.
/// </summary>
/// <remarks>
/// <para>
/// 주소별 토큰 버킷: 한 주소(IPv4 는 /32, IPv6 는 <see cref="IPv6PrefixLength"/> 프리픽스)마다
/// 초당 <see cref="PermitsPerSecond"/> 개씩 채워지고 최대 <see cref="BurstCapacity"/> 개까지
/// 쌓인다. <b>전역 제한보다 훨씬 작게</b> 잡는 것이 정상이다 — 한 주소가 정상적으로 낼 수 있는
/// 연결 속도만 허용하면 된다.
/// </para>
/// <para>
/// <b><see cref="SlotCount"/> 는 정확도와 메모리의 교환이다.</b> 슬롯이 많을수록 서로 다른
/// 주소가 한 버킷을 공유할 확률이 낮아진다. 동시 활성 주소 수의 <b>몇 배</b>로 잡는 것이
/// 경험칙이다(생일 문제 — 슬롯 수의 제곱근 근처부터 충돌이 흔해진다). 슬롯 하나는 수십 바이트라
/// 65,536 개여도 수 MB 수준이다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. 생성자가 값을 복사한다.</para>
/// </remarks>
public sealed class PerAddressConnectionRateOptions
{
    /// <summary>슬롯 수의 절대 상한.</summary>
    /// <remarks>설정 실수로 수억 개의 슬롯을 할당하는 것을 막는다.</remarks>
    public const int AbsoluteMaxSlotCount = 1 << 22;

    /// <summary>주소 하나가 낼 수 있는 초당 신규 연결 수.</summary>
    /// <remarks>전역 제한과 달리 <b>한 주소</b>의 정상 사용량 기준이다. 기본값은 보수적으로 낮게 잡았다.</remarks>
    public double PermitsPerSecond { get; set; } = 5;

    /// <summary>주소 하나의 순간 버스트 허용치.</summary>
    /// <remarks>
    /// 한 사용자가 앱을 다시 켜며 몇 개의 연결을 동시에 여는 정상 패턴을 흡수한다.
    /// NAT 뒤의 다수 사용자가 한 주소로 보이는 환경이면 이 값을 키워야 한다(아래 주의).
    /// </remarks>
    public int BurstCapacity { get; set; } = 10;

    /// <summary>주소별 버킷을 담는 고정 슬롯 수. 2의 거듭제곱으로 올림된다.</summary>
    /// <remarks>
    /// <b>이 수가 상한이다 — 맵이 자라지 않는다.</b> 시작 시 한 번 할당하고 그 뒤로는 절대
    /// 커지지 않으므로, 공격자가 소스 주소를 바꿔가며 메모리를 밀어 올릴 수 없다
    /// (<see cref="PerAddressConnectionRateAdmissionControl"/> 타입 문서의 설계 근거).
    /// </remarks>
    public int SlotCount { get; set; } = 16384;

    /// <summary>IPv6 주소를 묶을 프리픽스 길이(비트). 기본 64.</summary>
    /// <remarks>
    /// <para>
    /// <b>IPv6 를 주소 하나 단위로 제한하면 방어가 성립하지 않는다.</b> 최종 사용자에게도 보통
    /// <c>/64</c> 이상이 통째로 할당되므로, <c>/128</c> 단위 제한은 공격자에게 2^64 개의
    /// 우회로를 주는 것과 같다. 그래서 기본적으로 <c>/64</c> 로 묶어 <b>할당 단위</b>를 하나의
    /// 주체로 센다.
    /// </para>
    /// <para>
    /// 더 크게 묶으려면(예: ISP 할당 단위 <c>/56</c>·<c>/48</c>) 값을 줄인다. IPv4 는 이 값과
    /// 무관하게 주소 전체(/32)를 쓴다.
    /// </para>
    /// </remarks>
    public int IPv6PrefixLength { get; set; } = 64;

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

        if (SlotCount <= 0 || SlotCount > AbsoluteMaxSlotCount)
        {
            throw new InvalidOperationException(
                $"{nameof(SlotCount)} 는 1 이상 {AbsoluteMaxSlotCount} 이하여야 한다. 현재 값: {SlotCount}.");
        }

        if (IPv6PrefixLength is < 1 or > 128)
        {
            throw new InvalidOperationException(
                $"{nameof(IPv6PrefixLength)} 은 1 이상 128 이하여야 한다. 현재 값: {IPv6PrefixLength}.");
        }
    }
}
