using System;

namespace ChServerM.Diagnostics;

/// <summary>
/// 메트릭·추적에 붙는 태그 하나 (이름-값 쌍).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="IMetricsSink"/> 가 벤더 타입
/// (<c>System.Diagnostics.Metrics.TagList</c> 등)을 Core public 표면에 노출하지 않으려면
/// 중립 태그 표현이 필요하다. 값은 <see cref="string"/> 전용이다 — 메트릭 태그는 소수의
/// 저카디널리티 문자열(전송 종류·오류 코드 문자열)만 써야 하고, 값 타입을 담으면 박싱이
/// 유발되며 카디널리티 사고(커넥션 ID를 태그로)를 타입이 부추긴다(<see cref="TagNames"/> 규약).
/// </para>
/// <para>
/// <b>핫패스 규약.</b> 태그는 <see cref="ReadOnlySpan{T}"/> 로 전달한다 — 호출자가
/// <c>stackalloc</c> 또는 컴파일러 인라인 배열로 넘기면 힙 할당이 없다. 태그 없는
/// 카운터(프레임 수 등)는 빈 스팬을 넘긴다.
/// </para>
/// </remarks>
public readonly struct MetricTag : IEquatable<MetricTag>
{
    /// <summary>태그를 만든다.</summary>
    /// <param name="name">태그 이름. <see cref="TagNames"/> 의 상수를 쓴다.</param>
    /// <param name="value">태그 값. 저카디널리티여야 한다.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/>이 비어 있을 때.</exception>
    public MetricTag(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Value = value;
    }

    /// <summary>태그 이름.</summary>
    public string Name { get; }

    /// <summary>태그 값. <see langword="null"/>일 수 있다(부재 표현).</summary>
    public string? Value { get; }

    /// <inheritdoc />
    public bool Equals(MetricTag other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MetricTag other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Value);

    /// <summary>두 태그가 같은지 비교한다.</summary>
    public static bool operator ==(MetricTag left, MetricTag right) => left.Equals(right);

    /// <summary>두 태그가 다른지 비교한다.</summary>
    public static bool operator !=(MetricTag left, MetricTag right) => !left.Equals(right);
}
