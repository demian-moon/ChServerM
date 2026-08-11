using System;
using System.Collections.Generic;
using System.Numerics;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.RealTime.Spatial.Tests;

public sealed class InterestGridTests
{
    private static ObjectId Id(long value) => new(value);

    private static InterestGrid CreateGrid() =>
        new(worldMin: new Vector2(-100, -100), cellSize: 10f, cellsPerAxis: 32);

    [Fact]
    public void 추가_갱신_제거가_수를_추적한다()
    {
        InterestGrid grid = CreateGrid();

        Assert.True(grid.Add(Id(1), new Vector2(0, 0)));
        Assert.False(grid.Add(Id(1), new Vector2(1, 1))); // 중복 추가 거부
        Assert.True(grid.Update(Id(1), new Vector2(50, 50)));
        Assert.False(grid.Update(Id(2), Vector2.Zero));   // 없는 것 갱신 거부
        Assert.Equal(1, grid.Count);
        Assert.True(grid.Remove(Id(1)));
        Assert.False(grid.Remove(Id(1)));
        Assert.Equal(0, grid.Count);
    }

    [Fact]
    public void 반경_질의는_전수_검사와_같은_결과를_낸다()
    {
        // 그리드는 후보 축소 수단이고 정답은 거리 비교다 — 무작위 배치로 전수 검사와 대조한다.
        InterestGrid grid = CreateGrid();
        var random = new Random(42);
        var positions = new Dictionary<ObjectId, Vector2>();

        for (int i = 1; i <= 500; i++)
        {
            var pos = new Vector2(
                (random.NextSingle() * 260f) - 130f,  // 일부러 그리드 범위(-100~220) 밖까지 뿌린다
                (random.NextSingle() * 260f) - 130f);
            ObjectId id = Id(i);
            positions[id] = pos;
            Assert.True(grid.Add(id, pos));
        }

        var center = new Vector2(15, -20);
        const float radius = 37f;

        var expected = new HashSet<ObjectId>();
        foreach ((ObjectId id, Vector2 pos) in positions)
        {
            if (Vector2.DistanceSquared(pos, center) <= radius * radius)
            {
                expected.Add(id);
            }
        }

        var results = new List<ObjectId>();
        int added = grid.QueryCircle(center, radius, results);

        Assert.Equal(expected.Count, added);
        Assert.Equal(expected, new HashSet<ObjectId>(results));
    }

    [Fact]
    public void 영역_질의는_전수_검사와_같은_결과를_낸다()
    {
        InterestGrid grid = CreateGrid();
        var random = new Random(7);
        var positions = new Dictionary<ObjectId, Vector2>();

        for (int i = 1; i <= 300; i++)
        {
            var pos = new Vector2((random.NextSingle() * 200f) - 100f, (random.NextSingle() * 200f) - 100f);
            positions[Id(i)] = pos;
            grid.Add(Id(i), pos);
        }

        var area = new Aabb(new Vector2(-25, 10), new Vector2(40, 60));

        var expected = new HashSet<ObjectId>();
        foreach ((ObjectId id, Vector2 pos) in positions)
        {
            if (area.Contains(pos))
            {
                expected.Add(id);
            }
        }

        var results = new List<ObjectId>();
        grid.QueryAabb(in area, results);

        Assert.Equal(expected, new HashSet<ObjectId>(results));
    }

    [Fact]
    public void 셀_경계를_넘는_이동이_질의에_반영된다()
    {
        InterestGrid grid = CreateGrid();
        var results = new List<ObjectId>();

        grid.Add(Id(1), new Vector2(0, 0));
        grid.Update(Id(1), new Vector2(80, 80)); // 여러 셀을 건너뛰는 이동

        Assert.Equal(0, grid.QueryCircle(new Vector2(0, 0), 5f, results));
        Assert.Equal(1, grid.QueryCircle(new Vector2(80, 80), 5f, results));
    }

    [Fact]
    public void 레거시_버그17_회귀__범위_밖_좌표가_조용히_오매핑되지_않는다()
    {
        // 레거시 MortonIndex2 는 범위 밖 좌표를 마스크가 잘라내 엉뚱한 셀에 넣었다.
        // 여기서는 가장자리 셀로 클램프하되 실제 위치를 보존하므로, 질의 결과가 정확하다.
        InterestGrid grid = CreateGrid();
        var farAway = new Vector2(10_000f, -10_000f);
        grid.Add(Id(1), farAway);

        var results = new List<ObjectId>();
        Assert.Equal(0, grid.QueryCircle(new Vector2(0, 0), 90f, results)); // 원점 근처엔 없다
        Assert.Equal(1, grid.QueryCircle(farAway, 1f, results));            // 실제 위치에는 있다

        Assert.True(grid.TryGetPosition(Id(1), out Vector2 stored));
        Assert.Equal(farAway, stored); // 위치가 클램프로 손상되지 않는다
    }

    [Fact]
    public void 잘못된_생성_인자는_거부된다()
    {
        Assert.Throws<ArgumentException>(() => new InterestGrid(Vector2.Zero, 0f, 32));      // 0 나누기의 자리
        Assert.Throws<ArgumentException>(() => new InterestGrid(Vector2.Zero, float.NaN, 32));
        Assert.Throws<ArgumentException>(() => new InterestGrid(Vector2.Zero, 10f, 33));     // 2^n 아님
        Assert.Throws<ArgumentException>(() => new InterestGrid(Vector2.Zero, 10f, 131_072)); // 모튼 16비트 초과
    }

    [Fact]
    public void 음수_반지름_질의는_거부된다()
    {
        InterestGrid grid = CreateGrid();

        Assert.Throws<ArgumentException>(() => grid.QueryCircle(Vector2.Zero, -1f, []));
    }
}
