using System.Collections.Generic;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.RealTime.Rooms.Tests;

public sealed class DirtySetTests
{
    private static ObjectId Id(long value) => new(value);

    [Fact]
    public void 중복_표시는_한_번만_남는다()
    {
        var set = new DirtySet<ObjectId>();

        Assert.True(set.Mark(Id(1)));
        Assert.False(set.Mark(Id(1))); // 한 틱에 열 번 움직여도 스냅샷은 한 번
        Assert.True(set.Mark(Id(2)));
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void 비우기는_전부_돌려주고_초기화한다()
    {
        var set = new DirtySet<ObjectId>();
        set.Mark(Id(1));
        set.Mark(Id(2));
        set.Mark(Id(3));

        var drained = new HashSet<ObjectId>(set.Drain().ToArray());

        Assert.Equal([Id(1), Id(2), Id(3)], drained);
        Assert.Equal(0, set.Count);
        Assert.True(set.Drain().IsEmpty);
        Assert.True(set.Mark(Id(1))); // 비운 뒤에는 다시 새 표시다
    }

    [Fact]
    public void 표시_해제가_동작한다()
    {
        var set = new DirtySet<ObjectId>();
        set.Mark(Id(1));

        Assert.True(set.IsMarked(Id(1)));
        Assert.True(set.Unmark(Id(1)));   // 전송 전에 제거된 엔티티
        Assert.False(set.Unmark(Id(1)));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void 내부_버퍼가_성장해도_결과가_정확하다()
    {
        var set = new DirtySet<ObjectId>();
        for (long i = 1; i <= 1000; i++)
        {
            set.Mark(Id(i));
        }

        Assert.Equal(1000, set.Drain().Length);
    }
}
