using System;
using System.Diagnostics;
using System.Globalization;

namespace ChServerM.Handshake;

/// <summary>
/// 한 노드가 말할 수 있는 프레이밍 프로토콜 버전의 닫힌 구간 <c>[Min, Max]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 버전 협상(ADR-0017 결정 3)의 입력이자 출력 절반이다.
/// 클라이언트가 자기 구간을 제시하고, 서버가 자기 구간과의 교집합에서 최고 버전을
/// 고른다(<see cref="TrySelect"/>). "지원 버전"을 원시 <c>ushort</c> 두 개로 나르면
/// min·max 를 바꿔 넣는 실수를 타입이 못 잡는다 — 구간 불변식(<c>1 ≤ Min ≤ Max</c>)을
/// 생성자가 강제한다.
/// </para>
/// <para>
/// <b><see langword="default"/> 는 "설정되지 않음" 센티넬이다.</b> <see cref="ChServerM.Identity.MessageId.None"/> 과
/// 같은 원칙 — <c>Min == 0</c> 인 구간은 유효하지 않으며(버전 0 은 프레이밍이 거부하는
/// 센티넬), <see cref="Contains"/> 와 <see cref="TrySelect"/> 는 자연스럽게 항상 실패한다.
/// </para>
/// <para><b>스레드 규약.</b> 불변 값 타입. 어디서나 안전하다.</para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ProtocolVersionRange : IEquatable<ProtocolVersionRange>
{
    /// <summary>구간을 만든다.</summary>
    /// <param name="min">지원하는 최저 버전. 1 이상이어야 한다.</param>
    /// <param name="max">지원하는 최고 버전. <paramref name="min"/> 이상이어야 한다.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="min"/>이 0이거나 <paramref name="max"/>보다 클 때.
    /// </exception>
    public ProtocolVersionRange(ushort min, ushort max)
    {
        // 버전 0 은 "설정되지 않음" 센티넬이다 (FramingOptions.Validate 와 같은 규칙).
        ArgumentOutOfRangeException.ThrowIfZero(min);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        Min = min;
        Max = max;
    }

    /// <summary>지원하는 최저 버전. 0이면 설정되지 않은 센티넬.</summary>
    public ushort Min { get; }

    /// <summary>지원하는 최고 버전.</summary>
    public ushort Max { get; }

    /// <summary>버전이 이 구간에 속하는지 검사한다.</summary>
    /// <param name="version">검사할 버전.</param>
    /// <returns>속하면 <see langword="true"/>. 센티넬 구간은 항상 <see langword="false"/>.</returns>
    public bool Contains(ushort version) => version >= Min && version <= Max && Min != 0;

    /// <summary>두 구간의 교집합에서 최고 버전을 고른다.</summary>
    /// <param name="local">이쪽 노드의 지원 구간.</param>
    /// <param name="remote">상대 노드가 제시한 지원 구간.</param>
    /// <param name="selected">교집합이 있으면 그 최고 버전.</param>
    /// <returns>교집합이 있으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// "최고"를 고르는 이유: 양쪽 다 아는 버전 중 가장 새 것이 기능·수정을 가장 많이
    /// 담고 있다. 다운그레이드 방지(R-4)는 협상이 TLS 채널 안에서 일어나는 것으로
    /// 충족되므로 여기서 별도 장치가 필요 없다(ADR-0017 결정 3).
    /// </remarks>
    public static bool TrySelect(ProtocolVersionRange local, ProtocolVersionRange remote, out ushort selected)
    {
        // 센티넬(Min == 0)은 lower 가 0이 되어도 upper 검사에서 자연 탈락하지만,
        // 양쪽 다 센티넬이면 [0,0] 교집합이 "성립"해 버전 0을 고르게 된다 — 명시 차단.
        if (local.Min == 0 || remote.Min == 0)
        {
            selected = 0;
            return false;
        }

        ushort lower = Math.Max(local.Min, remote.Min);
        ushort upper = Math.Min(local.Max, remote.Max);

        if (lower > upper)
        {
            selected = 0;
            return false;
        }

        selected = upper;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(ProtocolVersionRange other) => Min == other.Min && Max == other.Max;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProtocolVersionRange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Min, Max);

    /// <summary>두 구간이 같은지 비교한다.</summary>
    public static bool operator ==(ProtocolVersionRange left, ProtocolVersionRange right) => left.Equals(right);

    /// <summary>두 구간이 다른지 비교한다.</summary>
    public static bool operator !=(ProtocolVersionRange left, ProtocolVersionRange right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"v[{Min},{Max}]");
}
