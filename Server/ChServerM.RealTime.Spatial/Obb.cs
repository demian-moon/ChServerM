using System;
using System.Numerics;

namespace ChServerM.RealTime.Spatial;

/// <summary>
/// 회전 가능한 사각형(OBB). 중심 + 반크기 + 회전각, 20바이트, 무할당 SAT 충돌 판정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> SAT(분리 축 정리) 충돌 판정의 담체다. 레거시 <c>QuadPointBoundM</c>은
/// struct 가 배열 3개를 보유하고(인스턴스마다 할당), 인터페이스 다형성으로 박싱·언박싱이
/// 일어나며, 그 언박싱 복사본 때문에 <b>회전이 실제로 적용된 적이 없었다</b>. 이 타입은
/// 중심·반크기·각도 세 값만 가지며, 꼭짓점·축은 필요할 때 계산한다(20B, 할당 0).
/// </para>
/// <para>
/// <b>막는 레거시 결함(전부 회귀 테스트로 고정).</b>
/// </para>
/// <list type="number">
///   <item><description>축 정렬 사각형끼리 항상 충돌 안 함(<c>return</c> 누락) — 여기서는
///   빠른 경로가 별도 분기가 아니라 같은 SAT 수식의 특수화라 어긋날 수 없다.</description></item>
///   <item><description>회전이 언박싱 복사본에 적용되어 무효 — 값 타입 + <see cref="Rotated"/>가
///   새 값을 반환하는 명시적 계약이라 "몰래 버려지는 복사본"이 없다.</description></item>
///   <item><description>접촉점·법선이 수학적으로 무의미(절댓값 오용·NaN) —
///   <see cref="TryGetContact"/>는 최소 침투 축에서 계산하고 NaN 경로가 없다.</description></item>
///   <item><description>float 좌표에 정수 <c>-1</c> 경계 트릭 — 경계 규칙은
///   <see cref="Aabb"/>와 동일한 닫힌 구간 하나다.</description></item>
/// </list>
/// <para>
/// <b>각도 규약 — 라디안 하나만 쓴다.</b> 레거시는 -180~180도와 0~360도가 혼재해 조용히
/// 틀렸다. 도(度) 입력은 호출부에서 <c>float.DegreesToRadians</c>로 변환한다.
/// </para>
/// <para><b>스레드 규약.</b> 불변 값 타입. 어디서든 안전하다.</para>
/// </remarks>
public readonly struct Obb : IEquatable<Obb>
{
    /// <summary>OBB 를 만든다.</summary>
    /// <param name="center">중심.</param>
    /// <param name="halfExtents">반크기. 성분이 음수·NaN 이면 안 된다.</param>
    /// <param name="angleRadians">회전각(라디안, 반시계 양수). 유한해야 한다.</param>
    /// <exception cref="ArgumentException">반크기·각도가 유효하지 않을 때.</exception>
    public Obb(Vector2 center, Vector2 halfExtents, float angleRadians)
    {
        if (!(halfExtents.X >= 0f && halfExtents.Y >= 0f))
        {
            throw new ArgumentException($"반크기({halfExtents})는 음수·NaN 일 수 없다.");
        }

        if (!float.IsFinite(angleRadians))
        {
            throw new ArgumentException($"회전각({angleRadians})은 유한해야 한다.");
        }

        Center = center;
        HalfExtents = halfExtents;
        AngleRadians = angleRadians;
    }

    /// <summary>중심.</summary>
    public Vector2 Center { get; }

    /// <summary>반크기(로컬 X·Y 방향).</summary>
    public Vector2 HalfExtents { get; }

    /// <summary>회전각(라디안, 반시계 양수).</summary>
    public float AngleRadians { get; }

    /// <summary>회전각을 더한 새 OBB. 값 타입이므로 원본은 변하지 않는다 — 반환값을 써야 한다.</summary>
    public Obb Rotated(float deltaRadians) => new(Center, HalfExtents, AngleRadians + deltaRadians);

    /// <summary>평행 이동한 새 OBB.</summary>
    public Obb Translated(Vector2 offset) => new(Center + offset, HalfExtents, AngleRadians);

    /// <summary>네 꼭짓점을 채운다. 순서는 로컬 (+,+), (−,+), (−,−), (+,−)의 회전 결과다.</summary>
    /// <param name="corners">길이 4 이상의 대상 버퍼.</param>
    /// <exception cref="ArgumentException">버퍼가 4 미만일 때.</exception>
    public void GetCorners(Span<Vector2> corners)
    {
        if (corners.Length < 4)
        {
            throw new ArgumentException("꼭짓점 버퍼는 길이 4 이상이어야 한다.", nameof(corners));
        }

        (float sin, float cos) = MathF.SinCos(AngleRadians);
        Vector2 axisX = new Vector2(cos, sin) * HalfExtents.X;
        Vector2 axisY = new Vector2(-sin, cos) * HalfExtents.Y;

        corners[0] = Center + axisX + axisY;
        corners[1] = Center - axisX + axisY;
        corners[2] = Center - axisX - axisY;
        corners[3] = Center + axisX - axisY;
    }

    /// <summary>이 OBB 를 감싸는 최소 AABB. 그리드 삽입·광역 필터에 쓴다.</summary>
    public Aabb GetBoundingAabb()
    {
        (float sin, float cos) = MathF.SinCos(AngleRadians);
        // 회전 후 AABB 반크기 = |R| · h (성분별 절댓값 회전 행렬).
        Vector2 bounding = new(
            (MathF.Abs(cos) * HalfExtents.X) + (MathF.Abs(sin) * HalfExtents.Y),
            (MathF.Abs(sin) * HalfExtents.X) + (MathF.Abs(cos) * HalfExtents.Y));
        return Aabb.FromCenter(Center, bounding);
    }

    /// <summary>점 포함 판정. 점을 로컬 좌표로 되돌려 반크기와 비교한다. 경계 포함.</summary>
    public bool Contains(Vector2 point)
    {
        (float sin, float cos) = MathF.SinCos(AngleRadians);
        Vector2 delta = point - Center;
        float localX = (delta.X * cos) + (delta.Y * sin);
        float localY = (-delta.X * sin) + (delta.Y * cos);
        return MathF.Abs(localX) <= HalfExtents.X && MathF.Abs(localY) <= HalfExtents.Y;
    }

    /// <summary>SAT 교차 판정. 경계 접촉도 교차다(닫힌 구간 규칙).</summary>
    public bool Intersects(in Obb other) => FindMinimumOverlap(in this, in other, out _, out _);

    /// <summary>교차하면 최소 침투 축의 접촉 정보를 구한다.</summary>
    /// <param name="other">상대 도형.</param>
    /// <param name="contact">
    /// 교차 시 분리 정보. <see cref="CollisionContact.Normal"/>은 이 도형 → 상대 방향이다.
    /// </param>
    /// <returns>교차하면 <see langword="true"/>.</returns>
    /// <remarks>
    /// 중심이 완전히 겹쳐도 NaN 이 나오지 않는다 — 방향을 정할 중심 차가 없으면 최소 침투
    /// 축의 양(+) 방향을 그대로 쓴다(임의지만 유한하고 단위 벡터다).
    /// </remarks>
    public bool TryGetContact(in Obb other, out CollisionContact contact)
    {
        if (!FindMinimumOverlap(in this, in other, out Vector2 axis, out float depth))
        {
            contact = default;
            return false;
        }

        // 법선을 "이 도형 → 상대" 방향으로 정렬한다. 레거시는 절댓값을 써 방향이 소실됐다.
        Vector2 centerDelta = other.Center - Center;
        if (Vector2.Dot(centerDelta, axis) < 0f)
        {
            axis = -axis;
        }

        contact = new CollisionContact(axis, depth);
        return true;
    }

    /// <summary>
    /// SAT 본체 — 네 후보 축(각 OBB 의 로컬 축 2개)에 대한 구간 겹침 검사.
    /// 모두 겹치면 최소 겹침 축과 깊이를 낸다.
    /// </summary>
    /// <remarks>
    /// 꼭짓점 투영 대신 <b>반지름 공식</b>을 쓴다: 축 L 위 OBB 의 투영 반지름은
    /// <c>|dot(L, ax)|·hx + |dot(L, ay)|·hy</c> — 꼭짓점 배열도, 할당도 없다.
    /// 축 정렬(각도 0) 두 OBB 는 같은 수식이 AABB 검사로 자연 수렴하므로, 레거시처럼
    /// "빠른 경로 분기의 return 누락" 같은 결함이 존재할 자리가 없다.
    /// </remarks>
    private static bool FindMinimumOverlap(in Obb a, in Obb b, out Vector2 minAxis, out float minOverlap)
    {
        (float sinA, float cosA) = MathF.SinCos(a.AngleRadians);
        (float sinB, float cosB) = MathF.SinCos(b.AngleRadians);

        Span<Vector2> axes =
        [
            new(cosA, sinA),
            new(-sinA, cosA),
            new(cosB, sinB),
            new(-sinB, cosB),
        ];

        Vector2 centerDelta = b.Center - a.Center;
        minAxis = axes[0];
        minOverlap = float.MaxValue;

        for (int i = 0; i < axes.Length; i++)
        {
            Vector2 axis = axes[i];

            float radiusA =
                (MathF.Abs(Vector2.Dot(axis, axes[0])) * a.HalfExtents.X) +
                (MathF.Abs(Vector2.Dot(axis, axes[1])) * a.HalfExtents.Y);
            float radiusB =
                (MathF.Abs(Vector2.Dot(axis, axes[2])) * b.HalfExtents.X) +
                (MathF.Abs(Vector2.Dot(axis, axes[3])) * b.HalfExtents.Y);

            float distance = MathF.Abs(Vector2.Dot(axis, centerDelta));
            float overlap = radiusA + radiusB - distance;
            if (overlap < 0f)
            {
                return false; // 분리 축 발견 — 교차하지 않는다.
            }

            if (overlap < minOverlap)
            {
                minOverlap = overlap;
                minAxis = axis;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(Obb other) =>
        Center == other.Center && HalfExtents == other.HalfExtents && AngleRadians == other.AngleRadians;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Obb other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Center, HalfExtents, AngleRadians);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(Obb left, Obb right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(Obb left, Obb right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"obb:[{Center} ±{HalfExtents} @{AngleRadians}rad]";
}
