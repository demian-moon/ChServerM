using System;
using System.Collections.Generic;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.RealTime.Spatial.Tests;

public sealed class InterestSetTests
{
    private static ObjectId Id(long value) => new(value);

    [Fact]
    public void 첫_프레임의_관측은_전부_Enter다()
    {
        var set = new InterestSet();

        set.BeginUpdate();
        Assert.True(set.Observe(Id(1)));
        Assert.True(set.Observe(Id(2)));
        set.EndUpdate();

        Assert.Equal(2, set.Entered.Length);
        Assert.Equal(0, set.Left.Length);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void 집합_차분이_Enter와_Leave를_가른다()
    {
        // 레거시 CollisionEventGenerate 의 정석 알고리즘 승계 검증:
        // 프레임 1 = {1, 2}, 프레임 2 = {2, 3} → Enter {3}, Leave {1}, Stay {2}.
        var set = new InterestSet();

        set.BeginUpdate();
        set.Observe(Id(1));
        set.Observe(Id(2));
        set.EndUpdate();

        set.BeginUpdate();
        Assert.False(set.Observe(Id(2))); // Stay — Enter 가 아니다
        Assert.True(set.Observe(Id(3)));  // Enter
        set.EndUpdate();

        Assert.Equal([Id(3)], set.Entered.ToArray());
        Assert.Equal([Id(1)], set.Left.ToArray());
        Assert.True(set.Contains(Id(2)));
        Assert.False(set.Contains(Id(1)));
    }

    [Fact]
    public void 같은_프레임의_중복_관측은_한_번만_센다()
    {
        // 셀 경계에 걸친 질의는 같은 대상을 두 번 볼 수 있다 — 정상 경로다.
        var set = new InterestSet();

        set.BeginUpdate();
        Assert.True(set.Observe(Id(1)));
        Assert.False(set.Observe(Id(1)));
        set.EndUpdate();

        Assert.Equal(1, set.Entered.Length);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void 전부_사라지면_전부_Leave다()
    {
        var set = new InterestSet();

        set.BeginUpdate();
        set.Observe(Id(1));
        set.Observe(Id(2));
        set.EndUpdate();

        set.BeginUpdate();
        set.EndUpdate();

        Assert.Equal(0, set.Entered.Length);
        Assert.Equal(2, set.Left.Length);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void 여러_프레임을_돌려도_할당된_집합을_재사용한다()
    {
        // 레거시 결함 #14(프레임마다 new HashSet) 회귀 방지 — 많은 프레임을 돌려도
        // 동작이 일관돼야 한다(할당량 자체는 벤치마크 영역).
        var set = new InterestSet();
        var random = new Random(11);

        for (int frame = 0; frame < 200; frame++)
        {
            set.BeginUpdate();
            for (int i = 0; i < 50; i++)
            {
                set.Observe(Id(random.Next(1, 80)));
            }

            set.EndUpdate();
            Assert.Equal(set.Count, CountDistinct(set));
        }

        static int CountDistinct(InterestSet set)
        {
            int count = 0;
            for (long id = 1; id < 80; id++)
            {
                if (set.Contains(new ObjectId(id)))
                {
                    count++;
                }
            }

            return count;
        }
    }

    [Fact]
    public void 짝이_맞지_않는_호출은_버그_신호로_거부된다()
    {
        var set = new InterestSet();

        Assert.Throws<InvalidOperationException>(() => set.Observe(Id(1)));
        Assert.Throws<InvalidOperationException>(set.EndUpdate);

        set.BeginUpdate();
        Assert.Throws<InvalidOperationException>(set.BeginUpdate);
    }
}
