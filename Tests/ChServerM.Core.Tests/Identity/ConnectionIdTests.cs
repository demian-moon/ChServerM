using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Identity;

/// <summary>
/// slot + generation 핸들의 존재 이유는 하나다 — 재사용된 슬롯을 낡은 ID 가 가리키지 못하게 하는 것.
/// </summary>
public sealed class ConnectionIdTests
{
    [Fact]
    public void SameSlot_DifferentGeneration_AreNotEqual()
    {
        // 커넥션이 끊기고 슬롯이 재사용됐다. 예전 ID 를 들고 있던 코드가
        // 새 커넥션에 접근하면 안 된다.
        Assert.NotEqual(new ConnectionId(7, 1), new ConnectionId(7, 2));
    }

    [Fact]
    public void SameSlotAndGeneration_AreEqual()
    {
        Assert.Equal(new ConnectionId(7, 3), new ConnectionId(7, 3));
    }

    [Fact]
    public void Components_RoundTrip()
    {
        ConnectionId id = new(1234, 5678);

        Assert.Equal(1234u, id.Slot);
        Assert.Equal(5678u, id.Generation);
    }

    [Fact]
    public void None_IsDefault()
    {
        Assert.True(ConnectionId.None.IsNone);
        Assert.True(default(ConnectionId).IsNone);
    }

    [Fact]
    public void GenerationZero_IsNone_EvenWithSlot()
    {
        // generation 0 이 "미할당"이므로 슬롯 0 도 유효한 슬롯 번호로 쓸 수 있다.
        Assert.True(new ConnectionId(99, 0).IsNone);
        Assert.False(new ConnectionId(0, 1).IsNone);
    }

    [Fact]
    public void ToPartitionKey_IsStableAcrossGenerations()
    {
        // 파티션은 슬롯으로 정한다 — 같은 슬롯의 커넥션은 세대가 바뀌어도
        // 같은 실행 파티션에 머문다. 슬롯 배열 접근이 파티션 로컬로 유지된다.
        Assert.Equal(
            new ConnectionId(7, 1).ToPartitionKey(),
            new ConnectionId(7, 2).ToPartitionKey());
    }

    [Fact]
    public void DifferentSlots_ProduceDifferentPartitionKeys()
    {
        Assert.NotEqual(new ConnectionId(1, 1).ToPartitionKey(), new ConnectionId(2, 1).ToPartitionKey());
    }
}
