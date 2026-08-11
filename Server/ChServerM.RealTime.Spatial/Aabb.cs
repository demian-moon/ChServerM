using System;
using System.Numerics;

namespace ChServerM.RealTime.Spatial;

/// <summary>
/// 축 정렬 바운딩 박스(AABB). <c>Min</c>·<c>Max</c> 두 점, 16바이트, 무할당.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시 <c>RectM</c>은 struct 가 배열 2개(<c>_points</c>·<c>_axes</c>)를
/// 인스턴스 필드로 보유해 <b>사각형 하나 만들 때마다 힙 할당 2회</b>였고, 이동·충돌 계산이
/// 전부 새 인스턴스를 반환해 할당이 폭증했다. AABB 는 Min/Max 두 점이면 충분하다 —
/// 이 타입은 16바이트 값이고 어떤 연산도 할당하지 않는다.
/// </para>
/// <para>
/// <b>경계 규칙 — 닫힌 구간 <c>[Min, Max]</c> 하나로 통일한다.</b> 포함·교차 모두 경계를
/// 포함한다(접촉 = 교차). 레거시는 <c>Contains</c>와 <c>Intersects</c>의 경계 규칙이 달라
/// 조용히 어긋났다 — 규칙이 하나면 어긋날 수가 없다.
/// </para>
/// <para><b>스레드 규약.</b> 불변 값 타입. 어디서든 안전하다.</para>
/// </remarks>
public readonly struct Aabb : IEquatable<Aabb>
{
    /// <summary>AABB 를 만든다.</summary>
    /// <param name="min">최소 모서리.</param>
    /// <param name="max">최대 모서리. 성분별로 <paramref name="min"/> 이상이어야 한다.</param>
    /// <exception cref="ArgumentException">min 이 max 보다 크거나 성분에 NaN 이 있을 때 — 뒤집힌 박스는 버그의 신호다.</exception>
    public Aabb(Vector2 min, Vector2 max)
    {
        if (!(min.X <= max.X && min.Y <= max.Y))
        {
            throw new ArgumentException(
                $"Min({min})은 성분별로 Max({max}) 이하여야 한다. NaN 도 여기서 걸린다.");
        }

        Min = min;
        Max = max;
    }

    /// <summary>중심과 반크기로 만든다.</summary>
    /// <param name="center">중심.</param>
    /// <param name="halfExtents">반크기. 성분이 음수·NaN 이면 안 된다.</param>
    /// <exception cref="ArgumentException">반크기가 유효하지 않을 때.</exception>
    public static Aabb FromCenter(Vector2 center, Vector2 halfExtents)
    {
        if (!(halfExtents.X >= 0f && halfExtents.Y >= 0f))
        {
            throw new ArgumentException($"반크기({halfExtents})는 음수·NaN 일 수 없다.");
        }

        return new Aabb(center - halfExtents, center + halfExtents);
    }

    /// <summary>최소 모서리.</summary>
    public Vector2 Min { get; }

    /// <summary>최대 모서리.</summary>
    public Vector2 Max { get; }

    /// <summary>중심.</summary>
    public Vector2 Center => (Min + Max) * 0.5f;

    /// <summary>크기(폭·높이).</summary>
    public Vector2 Size => Max - Min;

    /// <summary>점 포함 판정. 경계 포함(닫힌 구간).</summary>
    public bool Contains(Vector2 point) =>
        point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;

    /// <summary>AABB 교차 판정. 경계 접촉도 교차다(닫힌 구간 — <see cref="Contains"/>와 같은 규칙).</summary>
    public bool Intersects(in Aabb other) =>
        Min.X <= other.Max.X && other.Min.X <= Max.X &&
        Min.Y <= other.Max.Y && other.Min.Y <= Max.Y;

    /// <summary>원과의 교차 판정. AOI 반경 질의의 셀 필터로 쓴다.</summary>
    /// <param name="center">원의 중심.</param>
    /// <param name="radius">반지름. 음수면 항상 <see langword="false"/>.</param>
    /// <remarks>박스에서 원 중심에 가장 가까운 점과의 거리 제곱 비교 — 제곱근이 없다.</remarks>
    public bool IntersectsCircle(Vector2 center, float radius)
    {
        if (radius < 0f)
        {
            return false;
        }

        Vector2 closest = Vector2.Clamp(center, Min, Max);
        return Vector2.DistanceSquared(closest, center) <= radius * radius;
    }

    /// <summary>평행 이동한 새 AABB.</summary>
    public Aabb Translated(Vector2 offset) => new(Min + offset, Max + offset);

    /// <inheritdoc />
    public bool Equals(Aabb other) => Min == other.Min && Max == other.Max;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Aabb other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Min, Max);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(Aabb left, Aabb right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(Aabb left, Aabb right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"aabb:[{Min} ~ {Max}]";
}
