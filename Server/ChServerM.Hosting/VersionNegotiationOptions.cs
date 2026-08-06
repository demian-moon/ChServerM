using System;
using ChServerM.Handshake;

namespace ChServerM.Hosting;

/// <summary>
/// 버전 협상 핸드셰이크(ADR-0017 결정 3)의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 협상에서 "정책"인 값은 둘뿐이다 — 이 노드가 말할 수 있는 버전
/// 구간과, 상대의 첫 프레임을 얼마나 기다릴 것인가. 나머지(와이어 형식·선택 규칙)는
/// 동결 계약이라 설정이 아니다.
/// </para>
/// <para>
/// <b><see cref="HandshakeTimeout"/> 은 끌 수 없다.</b> 협상 프레임을 보내지 않고 매달리는
/// 커넥션은 슬로우로리스의 변형이다(THREAT-MODEL T-16) — 무한 대기는 공격자에게
/// 커넥션 슬롯을 공짜로 준다.
/// </para>
/// <para>
/// <b>프레이밍 축과의 조합.</b> 협상 결과가 와이어에 반영되려면 프레이밍에 버전 필드가
/// 있어야 한다 — 고정 헤더 프레이밍이 그렇다. varint 프레이밍은 버전 필드가 없으므로
/// 협상과 조립하면 결과가 어디에도 반영되지 않는다(협상 자체는 동작한다 — 핸드셰이크는
/// 프레이밍 축을 타지 않기 때문이다). 조립 시점 검증은 Core 프레이밍 계약에 버전
/// 표면이 없어 현재 불가능하다 — 두 번째 프로토콜 버전이 실존할 때 계약 확장과 함께
/// 판단한다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점 전용. <c>Build()</c> 가 값을 복사한다.</para>
/// </remarks>
public sealed class VersionNegotiationOptions
{
    /// <summary>기본 핸드셰이크 제한 시간. 5초.</summary>
    /// <remarks>TLS 핸드셰이크가 선행하는 조립에서도 넉넉한 값이다. 내부망이면 줄인다.</remarks>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>이 노드가 지원하는 프로토콜 버전 구간.</summary>
    /// <remarks>
    /// 기본값은 <c>[1, 1]</c> — 현존하는 유일한 버전이다. 새 버전을 배포할 때 서버가
    /// 먼저 <c>[1, 2]</c> 로 넓히고, 클라이언트가 따라온 뒤, 구버전 분포가 0이 되면
    /// <c>[2, 2]</c> 로 좁힌다(R-5 의 버전 분포 관측이 그 판단 근거다).
    /// </remarks>
    public ProtocolVersionRange SupportedVersions { get; set; } = new(1, 1);

    /// <summary>상대의 협상 프레임을 기다리는 제한 시간.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = DefaultHandshakeTimeout;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    /// <remarks>조립(<c>Build()</c>) 시점에 호출된다. 잘못 조립된 서버는 뜨지 않는 편이 낫다.</remarks>
    public void Validate()
    {
        if (SupportedVersions.Min == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(SupportedVersions)} 가 설정되지 않았다(default 센티넬). " +
                "생성자로 만든 구간을 지정한다.");
        }

        if (HandshakeTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(HandshakeTimeout)} 은 양수여야 한다. 현재 값: {HandshakeTimeout}. " +
                "무한 대기는 협상하지 않는 커넥션에 슬롯을 공짜로 준다(T-16).");
        }
    }
}
