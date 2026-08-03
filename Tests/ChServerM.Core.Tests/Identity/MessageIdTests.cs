using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Identity;

/// <summary>
/// 앱/프레임워크 ID 대역이 겹치면 앱 핸들러가 하트비트를 가로챈다.
/// 경계값을 고정해 둔다.
/// </summary>
public sealed class MessageIdTests
{
    [Fact]
    public void Ranges_DoNotOverlap()
    {
        Assert.True(MessageId.AppRangeEnd < MessageId.FrameworkRangeStart);
    }

    [Theory]
    [InlineData(MessageId.AppRangeStart)]
    [InlineData((ushort)20000)]
    [InlineData(MessageId.AppRangeEnd)]
    public void AppRange_IsClassifiedCorrectly(ushort value)
    {
        MessageId id = new(value);

        Assert.True(id.IsAppRange);
        Assert.False(id.IsFrameworkRange);
        Assert.False(id.IsNone);
    }

    [Theory]
    [InlineData(MessageId.FrameworkRangeStart)]
    [InlineData(ushort.MaxValue)]
    public void FrameworkRange_IsClassifiedCorrectly(ushort value)
    {
        MessageId id = new(value);

        Assert.True(id.IsFrameworkRange);
        Assert.False(id.IsAppRange);
    }

    [Fact]
    public void Zero_IsNone_AndBelongsToNoRange()
    {
        MessageId id = MessageId.None;

        Assert.True(id.IsNone);
        Assert.False(id.IsAppRange);
        Assert.False(id.IsFrameworkRange);
    }

    [Fact]
    public void FrameworkMessageIds_AreAllInFrameworkRange()
    {
        Assert.True(FrameworkMessageIds.Heartbeat.IsFrameworkRange);
        Assert.True(FrameworkMessageIds.HeartbeatAck.IsFrameworkRange);
        Assert.True(FrameworkMessageIds.DisconnectRequest.IsFrameworkRange);
    }

    [Fact]
    public void FrameworkMessageIds_AreDistinct()
    {
        Assert.NotEqual(FrameworkMessageIds.Heartbeat, FrameworkMessageIds.HeartbeatAck);
        Assert.NotEqual(FrameworkMessageIds.Heartbeat, FrameworkMessageIds.DisconnectRequest);
        Assert.NotEqual(FrameworkMessageIds.HeartbeatAck, FrameworkMessageIds.DisconnectRequest);
    }

    [Fact]
    public void Comparison_FollowsNumericOrder()
    {
        Assert.True(new MessageId(1) < new MessageId(2));
        Assert.True(new MessageId(2) >= new MessageId(2));
    }
}
