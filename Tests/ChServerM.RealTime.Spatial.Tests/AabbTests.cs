using System;
using System.Numerics;
using Xunit;

namespace ChServerM.RealTime.Spatial.Tests;

public sealed class AabbTests
{
    [Fact]
    public void 포함과_교차의_경계_규칙이_같다()
    {
        // 레거시 RectM 은 Contains 와 Intersects 의 경계 규칙이 달랐다(#7).
        // 여기서는 닫힌 구간 하나다: 경계 위의 점은 포함이고, 모서리 접촉은 교차다.
        var box = new Aabb(new Vector2(0, 0), new Vector2(10, 10));

        Assert.True(box.Contains(new Vector2(10, 10)));
        Assert.True(box.Contains(new Vector2(0, 0)));
        Assert.False(box.Contains(new Vector2(10.001f, 10)));

        var touching = new Aabb(new Vector2(10, 10), new Vector2(20, 20));
        Assert.True(box.Intersects(in touching));
    }

    [Fact]
    public void 분리된_박스는_교차하지_않는다()
    {
        var a = new Aabb(new Vector2(0, 0), new Vector2(1, 1));
        var b = new Aabb(new Vector2(2, 2), new Vector2(3, 3));

        Assert.False(a.Intersects(in b));
    }

    [Fact]
    public void 원_교차는_가장_가까운_점_기준이다()
    {
        var box = new Aabb(new Vector2(0, 0), new Vector2(10, 10));

        Assert.True(box.IntersectsCircle(new Vector2(12, 5), 2f));   // 오른쪽 면에 접촉
        Assert.False(box.IntersectsCircle(new Vector2(12, 5), 1.9f));
        Assert.True(box.IntersectsCircle(new Vector2(5, 5), 0.1f));  // 내부
        Assert.False(box.IntersectsCircle(new Vector2(13, 13), 4f)); // 모서리 대각선 밖 (√18 ≈ 4.24)
    }

    [Fact]
    public void 레거시_버그3_회귀__생성자가_좌표를_뒤섞지_않는다()
    {
        // 레거시 RectM 생성자는 4번째 점에 X 대신 Y 를 썼다. Min/Max 표현은 점 배열이
        // 없어서 그 부류의 오타가 존재할 자리가 없다 — 생성 결과를 수치로 고정한다.
        Aabb box = Aabb.FromCenter(new Vector2(10, 20), new Vector2(3, 7));

        Assert.Equal(new Vector2(7, 13), box.Min);
        Assert.Equal(new Vector2(13, 27), box.Max);
        Assert.Equal(new Vector2(10, 20), box.Center);
        Assert.Equal(new Vector2(6, 14), box.Size);
    }

    [Fact]
    public void 뒤집힌_경계와_NaN은_생성_시점에_거부된다()
    {
        Assert.Throws<ArgumentException>(() => new Aabb(new Vector2(1, 0), new Vector2(0, 1)));
        Assert.Throws<ArgumentException>(() => new Aabb(new Vector2(float.NaN, 0), Vector2.One));
        Assert.Throws<ArgumentException>(() => Aabb.FromCenter(Vector2.Zero, new Vector2(-1, 0)));
    }
}
