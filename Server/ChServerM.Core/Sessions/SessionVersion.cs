using System;
using System.Diagnostics;

namespace ChServerM.Sessions;

/// <summary>
/// 세션 항목의 버전 — 낙관적 동시성(compare-and-swap)의 비교 대상.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 세션은 <b>읽고-고치고-쓰는</b> 자원이다. 그 사이에 다른 주체가 같은
/// 세션을 바꿨는지 알 방법이 없으면 뒤에 쓴 쪽이 앞의 변경을 조용히 지운다. 재접속 경로가
/// 특히 위험하다 — 옛 커넥션의 마지막 쓰기가 새 커넥션의 복구 상태를 덮으면 사용자는
/// "돌아왔는데 상태가 옛날 것" 을 보게 된다.
/// </para>
/// <para>
/// <b>값은 불투명하다.</b> 크기·순서·연속성에 의미를 두지 않는다. 저장소가 무엇을 넣든
/// 호출자는 <b>같은지 다른지만</b> 본다. 그래서 어댑터는 카운터든 타임스탬프든 Redis 의
/// 자체 리비전이든 자유롭게 고를 수 있다.
/// </para>
/// <para>
/// <b>⚠ 저장소 구현이 지켜야 할 계약 두 가지.</b>
/// </para>
/// <list type="number">
///   <item>쓰기가 성공할 때마다 <b>이전과 다른 값</b>이어야 한다</item>
///   <item>같은 키에 대해 <b>이전 값을 재사용하지 않는다</b> — 항목이 만료·삭제된 뒤
///   다시 만들어져도 마찬가지다. 재사용하면 오래된 버전을 들고 있던 쓰기가
///   <b>ABA 로 성공</b>해 남의 상태를 덮는다</item>
/// </list>
/// <para>
/// <b>스레드 규약.</b> 불변 값 타입이다. 어디서든 안전하게 복사·비교할 수 있다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct SessionVersion : IEquatable<SessionVersion>
{
    private readonly ulong _value;

    /// <summary>불투명한 원시 값으로 버전을 만든다. 저장소 어댑터가 쓴다.</summary>
    /// <param name="value">0 이 아닌 값. 0 은 <see cref="None"/> 이 쓴다.</param>
    public SessionVersion(ulong value) => _value = value;

    /// <summary>
    /// 항목이 없음을 뜻하는 버전. <b>첫 쓰기의 기대 버전</b>으로도 쓴다 —
    /// "아직 없을 때만 만들어라" 가 곧 생성의 조건부 표현이다.
    /// </summary>
    public static SessionVersion None => default;

    /// <summary>불투명한 원시 값. 어댑터와 진단 출력 외에는 해석하지 않는다.</summary>
    public ulong Value => _value;

    /// <summary>이 버전이 <see cref="None"/> 인가.</summary>
    public bool IsNone => _value == 0;

    /// <inheritdoc/>
    public bool Equals(SessionVersion other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionVersion other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>진단용 표현. 로그·진단 출력에만 쓴다.</summary>
    public override string ToString() => _value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>두 버전이 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(SessionVersion left, SessionVersion right) => left.Equals(right);

    /// <summary>두 버전이 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(SessionVersion left, SessionVersion right) => !left.Equals(right);
}
