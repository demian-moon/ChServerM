using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ChServerM.Resilience;

namespace ChServerM.Hosting;

/// <summary>
/// 원격 주소별로 신규 연결 속도를 제한하는 <see cref="IAdmissionControl"/> (Phase 10, T-14·T-16, ADR-0026).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 전역 속도 제한(<see cref="ConnectionRateAdmissionControl"/>)은 프로세스 CPU 를
/// 지키지만 <b>누가 학대자인지 구분하지 못한다</b> — 악성 IP 하나가 전역 예산을 빨아들이면 정상
/// 사용자가 함께 거부된다(공격자가 원하는 결과다). 이 구현은 주소별로 예산을 나눠, 폭주하는
/// 주소만 거부하고 나머지는 그대로 받는다. 둘은 <see cref="CompositeAdmissionControl"/> 로 AND
/// 결합하는 것이 의도된 사용법이다 — 전역이 총량을, 주소별이 개별 학대자를 막는다.
/// </para>
/// <para>
/// <b>⚠ 이 방어 장치가 스스로 공격 표면이 되지 않아야 한다 — 이 설계의 핵심.</b>
/// 주소→버킷을 <c>Dictionary</c> 에 담으면 공격자가 소스 주소를 바꿔가며 접속하는 것만으로
/// 맵이 무한히 자라 <b>OOM 으로 죽는다</b>. 방어하려던 자원 고갈을 방어 코드가 대신 일으키는
/// 셈이다. 그래서 여기서는 <b>시작 시 고정 크기 슬롯 배열</b>을 한 번 할당하고 그 뒤로 절대
/// 늘리지 않는다 — 축출 메커니즘·스윕 타이머·항목당 할당이 아예 존재하지 않는다
/// ("무제한 큐 금지"의 같은 원리, CLAUDE.md 9.6).
/// </para>
/// <para>
/// <b>대가는 해시 충돌이다.</b> 서로 다른 두 주소가 같은 슬롯에 떨어지면 하나의 버킷을 공유해,
/// 한쪽의 소비가 다른 쪽을 제한할 수 있다. 정상 상태에서는 슬롯 수를 활성 주소 수보다 넉넉히
/// 잡아 확률을 낮추고(<see cref="PerAddressConnectionRateOptions.SlotCount"/>), 충돌이 나도
/// 리필이 수 초 안에 회복시킨다. <b>정확한 계정이 필요하면 이 구현이 아니라 축출 정책이 있는
/// 구현을 쓴다</b> — 그 교환을 옵션이 아니라 타입으로 갈라두었다.
/// </para>
/// <para>
/// <b>충돌을 의도적으로 노리는 공격은 랜덤 시드로 막는다.</b> 해시가 프로세스마다 고정이면
/// 공격자가 피해자 주소와 같은 슬롯에 떨어지는 주소를 미리 계산해 그 슬롯의 토큰을 고갈시키는
/// <b>표적 서비스 거부</b>가 가능하다. <see cref="HashCode"/> 는 프로세스마다 무작위 시드를
/// 쓰므로(BCL 의 해시 플러딩 방어) 그 계산이 성립하지 않는다.
/// </para>
/// <para>
/// <b>IPv6 는 프리픽스로 묶는다(기본 <c>/64</c>).</b> 최종 사용자에게도 보통 <c>/64</c> 이상이
/// 통째로 할당되므로 주소 하나 단위로 제한하면 공격자에게 2^64 개의 우회로를 주는 것과 같다.
/// 그래서 <b>할당 단위</b>를 하나의 주체로 센다(<see cref="PerAddressConnectionRateOptions.IPv6PrefixLength"/>).
/// IPv4 는 주소 전체(/32)를 쓰고, IPv4 매핑 IPv6(<c>::ffff:a.b.c.d</c>)는 IPv4 로 되돌려 센다 —
/// 듀얼스택 소켓이 IPv4 클라이언트를 그 형태로 넘기므로, 되돌리지 않으면 같은 클라이언트가
/// 두 주체로 갈린다.
/// </para>
/// <para>
/// <b>주소를 모르면 판정하지 않는다(수용).</b> 원격 종단이
/// <see langword="null"/> 이거나 IP 종단이 아니면(인메모리 전송 등) 이 규칙은 할 말이 없으므로
/// 통과시킨다 — 거부가 아니다. 총량 방어는 컴포지트의 전역 규칙이 계속 맡는다.
/// </para>
/// <para>
/// <b>⚠ NAT 주의.</b> 한 공인 IP 뒤에 다수 사용자가 있는 환경(회사·학교·모바일 캐리어)에서는
/// 그들이 하나의 주체로 보인다. 그 배포에서는 버스트·속도를 그만큼 키워야 정상 사용자가
/// 거부되지 않는다.
/// </para>
/// <para>
/// <b>⚠ 락을 쓴다 — 근거.</b> 리필과 소비가 원자적이어야 하는데 이 경로는 <b>커넥션당 1회</b>
/// (프레임당이 아니다)라 핫패스가 아니다(9.1 은 핫패스 락을 금한다). 게다가 이 규칙은 보통
/// 전역 규칙과 함께 쓰이는데 그쪽이 이미 직렬화 지점이라, 락 하나를 더해도 동시성 특성이
/// 달라지지 않는다. 슬롯별 락 스트라이핑은 <b>경합이 측정된 뒤에</b> 도입한다(측정 없는 최적화 금지).
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다. 여러 전송이 공유할 수 있다.</para>
/// </remarks>
public sealed class PerAddressConnectionRateAdmissionControl : IAdmissionControl
{
    private readonly double _permitsPerSecond;
    private readonly double _burstCapacity;
    private readonly int _ipv6PrefixLength;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    /// <summary>주소 해시로 인덱싱하는 고정 슬롯. <b>시작 시 한 번 할당하고 절대 늘리지 않는다.</b></summary>
    /// <remarks>
    /// 구조체 배열이라 슬롯당 객체 할당이 없다 — 런타임 할당 0 이 이 방어의 조건이다
    /// (할당이 있으면 공격자가 그 할당을 유도할 수 있다). 크기는 2의 거듭제곱이라
    /// 인덱싱이 비트 마스크 한 번이다.
    /// </remarks>
    private readonly Slot[] _slots;

    /// <summary><see cref="_slots"/> 길이 − 1. 2의 거듭제곱이므로 이 값이 인덱스 마스크다.</summary>
    private readonly int _slotMask;

    /// <summary>설정을 검증하고 모든 슬롯을 가득 채워 시작한다.</summary>
    /// <param name="options">주소별 토큰 버킷·슬롯 파라미터.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다. 생략하면 시스템 시계.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    /// <remarks>
    /// 슬롯을 가득 채워 시작하는 이유: 빈 버킷으로 시작하면 서버 부팅 직후 첫 접속이 전부
    /// 거부된다(전역 구현이 버킷을 채워 시작하는 것과 같은 근거).
    /// </remarks>
    public PerAddressConnectionRateAdmissionControl(
        PerAddressConnectionRateOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _permitsPerSecond = options.PermitsPerSecond;
        _burstCapacity = options.BurstCapacity;
        _ipv6PrefixLength = options.IPv6PrefixLength;
        _timeProvider = timeProvider ?? TimeProvider.System;

        int slotCount = RoundUpToPowerOfTwo(options.SlotCount);
        _slots = new Slot[slotCount];
        _slotMask = slotCount - 1;

        long now = _timeProvider.GetTimestamp();
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Tokens = _burstCapacity;
            _slots[i].LastRefillTimestamp = now;
        }
    }

    /// <inheritdoc />
    public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint)
    {
        // 주소를 모르면 이 규칙은 판정하지 않는다(타입 문서). 컴포지트의 다른 규칙이 계속 본다.
        if (remoteEndPoint is not IPEndPoint ipEndPoint)
        {
            return AdmissionDecision.Admit();
        }

        int slot = SlotOf(ipEndPoint.Address);

        lock (_gate)
        {
            ref Slot bucket = ref _slots[slot];

            long now = _timeProvider.GetTimestamp();
            double elapsedSeconds = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, now).TotalSeconds;

            // 경과 시간만큼 토큰을 채운다(상한 = 버스트 용량). 음수 경과(시계 이상)는 0으로 본다.
            if (elapsedSeconds > 0)
            {
                bucket.Tokens = Math.Min(_burstCapacity, bucket.Tokens + (elapsedSeconds * _permitsPerSecond));
                bucket.LastRefillTimestamp = now;
            }

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return AdmissionDecision.Admit();
            }

            // 사유에 주소를 담지 않는다 — 이 문자열은 메트릭 태그로 흘러갈 수 있고,
            // 주소는 카디널리티가 무한이라 시계열을 폭발시킨다(TagNames 규약).
            return AdmissionDecision.Reject("per-address connection rate exceeded");
        }
    }

    /// <summary>주소를 정규화해 슬롯 인덱스를 구한다.</summary>
    /// <remarks>
    /// 정규화 = IPv4 매핑 되돌리기 + IPv6 프리픽스 마스킹(타입 문서). 해시는
    /// <see cref="HashCode"/> 라 프로세스마다 시드가 다르다 — 표적 충돌 공격이 성립하지 않는다.
    /// </remarks>
    private int SlotOf(IPAddress address)
    {
        // 최대 16바이트(IPv6). 스택에 담아 주소당 할당을 0 으로 둔다.
        Span<byte> bytes = stackalloc byte[16];

        // 듀얼스택 소켓이 IPv4 클라이언트를 ::ffff:a.b.c.d 로 넘긴다 — 되돌리지 않으면
        // 같은 클라이언트가 IPv4 접속과 다른 주체로 갈린다.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!address.TryWriteBytes(bytes, out int written))
        {
            // 주소 계열이 예상 밖이다(발생하지 않아야 한다). 판정하지 않고 통과시킨다 —
            // 알 수 없는 입력 때문에 정상 연결을 끊는 것이 더 나쁘다.
            return 0;
        }

        Span<byte> key = bytes[..written];

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            MaskToPrefix(key, _ipv6PrefixLength);
        }

        HashCode hash = default;
        hash.AddBytes(key);

        // 2의 거듭제곱 크기라 마스크 한 번이면 된다. HashCode 는 하위 비트까지 잘 섞인다.
        return hash.ToHashCode() & _slotMask;
    }

    /// <summary>프리픽스 길이 밖의 비트를 0 으로 만든다 — 같은 할당 단위를 한 주체로 묶는다.</summary>
    private static void MaskToPrefix(Span<byte> address, int prefixLength)
    {
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        if (remainingBits != 0 && fullBytes < address.Length)
        {
            // 부분 바이트: 상위 remainingBits 개만 남긴다.
            address[fullBytes] &= (byte)(0xFF << (8 - remainingBits));
            fullBytes++;
        }

        for (int i = fullBytes; i < address.Length; i++)
        {
            address[i] = 0;
        }
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        // 인덱싱을 나눗셈이 아니라 마스크로 하기 위해 2의 거듭제곱으로 올린다.
        // 옵션 검증이 상한을 이미 확인했으므로 오버플로가 없다.
        int result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    /// <summary>슬롯 하나의 토큰 버킷 상태.</summary>
    /// <remarks>
    /// <b>패딩하지 않는다.</b> 인접 슬롯이 캐시 라인을 공유하지만(9.4 의 false sharing),
    /// 이 경로는 커넥션당 1회 저빈도이고 어차피 단일 락 뒤에서 갱신되므로 패딩의 이득이 없다.
    /// 슬롯 수만큼 64바이트를 낭비하는 쪽이 손해다(16,384 슬롯이면 1MB).
    /// </remarks>
    private struct Slot
    {
        public double Tokens;
        public long LastRefillTimestamp;
    }
}
