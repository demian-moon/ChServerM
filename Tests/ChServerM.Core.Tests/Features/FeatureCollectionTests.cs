using ChServerM.Features;
using Xunit;

namespace ChServerM.Core.Tests.Features;

/// <summary>
/// 전송별 선택 기능이 여기로 들어간다. 없는 기능 조회가 예외가 되면
/// 상위 계층이 전송 종류를 알아야 하고, 그러면 교체 가능성이 깨진다.
/// </summary>
public sealed class FeatureCollectionTests
{
    private interface ITestFeature
    {
        int Value { get; }
    }

    private interface IOtherFeature
    {
    }

    private sealed class TestFeature(int value) : ITestFeature
    {
        public int Value { get; } = value;
    }

    [Fact]
    public void Get_Unregistered_ReturnsNull()
    {
        // 없는 것은 정상이다. 호출자는 null 검사로 물러선다.
        Assert.Null(new FeatureCollection().Get<ITestFeature>());
    }

    [Fact]
    public void SetThenGet_ReturnsSameInstance()
    {
        FeatureCollection features = new();
        TestFeature feature = new(42);

        features.Set<ITestFeature>(feature);

        Assert.Same(feature, features.Get<ITestFeature>());
    }

    [Fact]
    public void Get_IsKeyedByContractType_NotConcreteType()
    {
        FeatureCollection features = new();
        features.Set<ITestFeature>(new TestFeature(1));

        Assert.Null(features.Get<IOtherFeature>());
    }

    [Fact]
    public void Set_Overwrites()
    {
        FeatureCollection features = new();
        features.Set<ITestFeature>(new TestFeature(1));
        features.Set<ITestFeature>(new TestFeature(2));

        Assert.Equal(2, features.Get<ITestFeature>()!.Value);
        Assert.Equal(1, features.Count);
    }

    [Fact]
    public void SetNull_RemovesRegistration()
    {
        FeatureCollection features = new();
        features.Set<ITestFeature>(new TestFeature(1));

        features.Set<ITestFeature>(null);

        Assert.Null(features.Get<ITestFeature>());
        Assert.Equal(0, features.Count);
    }

    [Fact]
    public void Revision_StartsAtZero_AndAdvancesOnMutation()
    {
        FeatureCollection features = new();
        Assert.Equal(0, features.Revision);

        features.Set<ITestFeature>(new TestFeature(1));
        int afterSet = features.Revision;
        Assert.True(afterSet > 0);

        features.Set<ITestFeature>(null);
        Assert.True(features.Revision > afterSet);
    }

    [Fact]
    public void Revision_DoesNotAdvance_WhenRemovingAbsentFeature()
    {
        // 캐시 무효화를 헛되이 유발하지 않는다.
        FeatureCollection features = new();

        features.Set<ITestFeature>(null);

        Assert.Equal(0, features.Revision);
    }

    [Fact]
    public void Revision_DoesNotAdvance_OnResetOfEmptyCollection()
    {
        FeatureCollection features = new();

        features.Reset();

        Assert.Equal(0, features.Revision);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        FeatureCollection features = new();
        features.Set<ITestFeature>(new TestFeature(1));

        features.Reset();

        Assert.Equal(0, features.Count);
        Assert.Null(features.Get<ITestFeature>());
    }

    [Fact]
    public void EmptyCollection_DoesNotAllocateBackingStore()
    {
        // 기능을 안 쓰는 커넥션은 딕셔너리 할당이 0 이어야 한다.
        // 직접 관측할 수 없으므로 Count 경로가 null 저장소를 견디는지로 대신 확인한다.
        FeatureCollection features = new();

        Assert.Equal(0, features.Count);
        Assert.Null(features.Get<ITestFeature>());
    }
}
