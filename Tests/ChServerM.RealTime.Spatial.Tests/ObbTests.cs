using System;
using System.Numerics;
using Xunit;

namespace ChServerM.RealTime.Spatial.Tests;

/// <summary>
/// SAT 충돌 판정 테스트. 로드맵 경고("충돌 판정은 단위 테스트를 먼저 쓴다")의 이행이다 —
/// 레거시 충돌 계층의 미수정 버그 8건이 여기서 회귀 케이스로 고정된다.
/// </summary>
public sealed class ObbTests
{
    private const float HalfPi = MathF.PI / 2f;

    [Fact]
    public void 레거시_버그1_회귀__축_정렬_사각형끼리도_충돌한다()
    {
        // 레거시 QuadPointBoundM 은 축 정렬 quad vs quad 분기의 return 누락으로
        // "축 정렬된 두 사각형은 절대 충돌하지 않는다"고 판정했다.
        var a = new Obb(new Vector2(0, 0), new Vector2(5, 5), 0f);
        var b = new Obb(new Vector2(4, 4), new Vector2(5, 5), 0f);

        Assert.True(a.Intersects(in b));
    }

    [Fact]
    public void 레거시_버그11_회귀__회전이_실제로_적용된다()
    {
        // 레거시 콜라이더의 Rotate 는 언박싱 복사본을 회전시켜 아무 효과가 없었다.
        // 가로로 긴 막대를 90도 돌리면 세로로 길어져야 한다.
        var bar = new Obb(Vector2.Zero, new Vector2(10, 1), 0f);
        Obb rotated = bar.Rotated(HalfPi);

        // 회전 전: (0, 5)는 막대 밖(높이 반경 1). 회전 후: 세로 막대 안이다.
        Assert.False(bar.Contains(new Vector2(0, 5)));
        Assert.True(rotated.Contains(new Vector2(0, 5)));

        // 회전 전 안이던 (5, 0)은 회전 후 밖이다.
        Assert.True(bar.Contains(new Vector2(5, 0)));
        Assert.False(rotated.Contains(new Vector2(5, 0)));
    }

    [Fact]
    public void 레거시_버그13_회귀__접촉_법선은_첫_도형에서_상대를_향한다()
    {
        // 레거시 ContactPoint 는 중심 차의 절댓값을 써 법선이 항상 1사분면이었다.
        // b 가 a 의 왼쪽에 있으면 법선(a→b)은 -X 방향이어야 한다.
        var a = new Obb(new Vector2(0, 0), new Vector2(5, 5), 0f);
        var b = new Obb(new Vector2(-8, 0), new Vector2(5, 5), 0f);

        Assert.True(a.TryGetContact(in b, out CollisionContact contact));
        Assert.True(contact.Normal.X < 0f, $"법선이 상대 방향이 아니다: {contact.Normal}");
        Assert.Equal(0f, contact.Normal.Y, 3);
        Assert.Equal(2f, contact.Depth, 3); // 겹침 = 5+5-8
    }

    [Fact]
    public void 레거시_버그13_회귀__중심이_겹쳐도_NaN이_나오지_않는다()
    {
        // 레거시는 중심이 같으면 Normalize(0) → NaN 이었다.
        var a = new Obb(new Vector2(3, 3), new Vector2(2, 2), 0f);
        var b = new Obb(new Vector2(3, 3), new Vector2(1, 1), 0.5f);

        Assert.True(a.TryGetContact(in b, out CollisionContact contact));
        Assert.False(float.IsNaN(contact.Normal.X) || float.IsNaN(contact.Normal.Y), "법선에 NaN");
        Assert.False(float.IsNaN(contact.Depth), "깊이에 NaN");
        Assert.Equal(1f, contact.Normal.Length(), 3); // 단위 벡터 유지
    }

    [Fact]
    public void 분리된_도형은_충돌하지_않는다()
    {
        var a = new Obb(new Vector2(0, 0), new Vector2(1, 1), 0.3f);
        var b = new Obb(new Vector2(10, 10), new Vector2(1, 1), 1.1f);

        Assert.False(a.Intersects(in b));
        Assert.False(a.TryGetContact(in b, out _));
    }

    [Fact]
    public void 축_정렬로는_겹치지만_회전하면_분리되는_경우를_SAT가_잡는다()
    {
        // 대각선으로 마주 본 두 회전 사각형 — AABB 로는 겹치지만 SAT 분리 축이 존재한다.
        var a = new Obb(new Vector2(0, 0), new Vector2(2, 0.1f), MathF.PI / 4f);   // ↗ 방향 막대
        var b = new Obb(new Vector2(2.2f, 0), new Vector2(2, 0.1f), MathF.PI / 4f); // 평행 이동한 같은 막대

        Assert.True(a.GetBoundingAabb().Intersects(b.GetBoundingAabb()), "전제: AABB 는 겹친다");
        Assert.False(a.Intersects(in b), "평행한 두 막대 사이에는 분리 축이 있다");
    }

    [Fact]
    public void 경계_접촉은_교차다()
    {
        // 닫힌 구간 규칙 하나로 통일 — 레거시는 Contains/Intersects 규칙이 달랐다.
        var a = new Obb(new Vector2(0, 0), new Vector2(1, 1), 0f);
        var b = new Obb(new Vector2(2, 0), new Vector2(1, 1), 0f);

        Assert.True(a.Intersects(in b));
        Assert.True(a.TryGetContact(in b, out CollisionContact contact));
        Assert.Equal(0f, contact.Depth, 3);
    }

    [Fact]
    public void 꼭짓점은_회전각을_반영한다()
    {
        var box = new Obb(Vector2.Zero, new Vector2(1, 1), HalfPi);
        Span<Vector2> corners = stackalloc Vector2[4];
        box.GetCorners(corners);

        foreach (Vector2 corner in corners)
        {
            Assert.Equal(MathF.Sqrt(2f), corner.Length(), 3); // 반지름 √2 유지
        }
    }

    [Fact]
    public void 바운딩_AABB는_회전을_반영한다()
    {
        var bar = new Obb(Vector2.Zero, new Vector2(10, 1), 0f);
        Aabb upright = bar.Rotated(HalfPi).GetBoundingAabb();

        Assert.Equal(1f, upright.Max.X, 2);
        Assert.Equal(10f, upright.Max.Y, 2);
    }

    [Fact]
    public void 잘못된_인자는_생성_시점에_거부된다()
    {
        Assert.Throws<ArgumentException>(() => new Obb(Vector2.Zero, new Vector2(-1, 1), 0f));
        Assert.Throws<ArgumentException>(() => new Obb(Vector2.Zero, Vector2.One, float.NaN));
        Assert.Throws<ArgumentException>(() => new Obb(Vector2.Zero, new Vector2(float.NaN, 1), 0f));
    }
}
