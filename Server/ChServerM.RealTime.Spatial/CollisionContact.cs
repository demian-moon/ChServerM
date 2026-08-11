using System;
using System.Numerics;

namespace ChServerM.RealTime.Spatial;

/// <summary>
/// 충돌 접촉 정보 — 분리에 필요한 최소 이동(MTV)의 방향과 깊이.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 + 막는 레거시 결함.</b> 레거시 <c>ContactPoint</c>는 두 중심 차의
/// <b>절댓값</b>을 반으로 나눠 접촉점이라 불렀다 — 항상 1사분면 벡터가 되는, 수학적으로
/// 무의미한 값이다. 게다가 중심이 겹치면 <c>Normalize(0)</c> → NaN 이었다. 이 타입은
/// SAT 의 <b>최소 침투 축</b>에서 정식으로 계산되며(<see cref="Obb.TryGetContact"/>),
/// NaN 이 나오는 경로가 없다.
/// </para>
/// <para>
/// <see cref="Normal"/>은 <b>첫 번째 도형에서 두 번째 도형을 향하는</b> 단위 벡터다.
/// 첫 번째 도형을 <c>-Normal × Depth</c> 만큼 옮기면 두 도형이 정확히 분리된다.
/// </para>
/// </remarks>
public readonly struct CollisionContact : IEquatable<CollisionContact>
{
    internal CollisionContact(Vector2 normal, float depth)
    {
        Normal = normal;
        Depth = depth;
    }

    /// <summary>분리 방향 단위 벡터. 첫 번째 도형 → 두 번째 도형.</summary>
    public Vector2 Normal { get; }

    /// <summary>침투 깊이. 항상 0 이상이다.</summary>
    public float Depth { get; }

    /// <inheritdoc />
    public bool Equals(CollisionContact other) => Normal == other.Normal && Depth == other.Depth;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CollisionContact other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Normal, Depth);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(CollisionContact left, CollisionContact right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(CollisionContact left, CollisionContact right) => !left.Equals(right);
}
