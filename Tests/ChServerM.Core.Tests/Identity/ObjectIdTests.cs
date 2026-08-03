using System;
using System.Collections.Generic;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Identity;

/// <summary>
/// 레거시 <c>GlobalM.MakeGameOid()</c> 가 프로세스 전역 카운터라 다중 노드에서 충돌했다.
/// 노드 성분이 실제로 보존되는지, 비트 필드가 서로를 침범하지 않는지 검증한다.
/// </summary>
public sealed class ObjectIdTests
{
    [Theory]
    [InlineData(0L, 0, 0)]
    [InlineData(1L, 1, 1)]
    [InlineData(1_700_000_000_000L, 512, 2048)]
    public void Create_RoundTripsAllComponents(long timestampMs, int nodeId, int sequence)
    {
        ObjectId id = ObjectId.Create(timestampMs, nodeId, sequence);

        Assert.Equal(timestampMs, id.TimestampMs);
        Assert.Equal(nodeId, id.NodeId);
        Assert.Equal(sequence, id.Sequence);
    }

    [Fact]
    public void Create_MaxComponents_RoundTrip()
    {
        long maxTimestamp = (1L << ObjectId.TimestampBits) - 1;

        ObjectId id = ObjectId.Create(maxTimestamp, ObjectId.MaxNodeId, ObjectId.MaxSequence);

        Assert.Equal(maxTimestamp, id.TimestampMs);
        Assert.Equal(ObjectId.MaxNodeId, id.NodeId);
        Assert.Equal(ObjectId.MaxSequence, id.Sequence);
    }

    [Fact]
    public void Create_MaxComponents_StaysPositive()
    {
        // 부호 비트가 0 이어야 정렬·나머지 연산·직렬화가 모두 안전하다.
        long maxTimestamp = (1L << ObjectId.TimestampBits) - 1;
        ObjectId id = ObjectId.Create(maxTimestamp, ObjectId.MaxNodeId, ObjectId.MaxSequence);

        Assert.True(id.Value > 0);
    }

    [Fact]
    public void BitLayout_TotalsSixtyThreeBits()
    {
        // 부호 비트 1개를 남긴다.
        Assert.Equal(63, ObjectId.TimestampBits + ObjectId.NodeIdBits + ObjectId.SequenceBits);
    }

    [Fact]
    public void Create_MaxSequence_DoesNotBleedIntoNodeId()
    {
        ObjectId id = ObjectId.Create(1, 0, ObjectId.MaxSequence);

        Assert.Equal(0, id.NodeId);
        Assert.Equal(1, id.TimestampMs);
    }

    [Fact]
    public void Create_MaxNodeId_DoesNotBleedIntoTimestamp()
    {
        ObjectId id = ObjectId.Create(1, ObjectId.MaxNodeId, 0);

        Assert.Equal(1, id.TimestampMs);
        Assert.Equal(0, id.Sequence);
    }

    [Theory]
    [InlineData(-1L, 0, 0)]
    [InlineData(1L << ObjectId.TimestampBits, 0, 0)]
    [InlineData(0L, -1, 0)]
    [InlineData(0L, ObjectId.MaxNodeId + 1, 0)]
    [InlineData(0L, 0, -1)]
    [InlineData(0L, 0, ObjectId.MaxSequence + 1)]
    public void Create_OutOfRange_Throws(long timestampMs, int nodeId, int sequence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ObjectId.Create(timestampMs, nodeId, sequence));
    }

    [Fact]
    public void DifferentNodes_SameTimestampAndSequence_DoNotCollide()
    {
        // 레거시가 다중 노드 배포를 구조적으로 막았던 바로 그 지점이다.
        HashSet<long> seen = new();

        for (int node = 0; node <= ObjectId.MaxNodeId; node++)
        {
            Assert.True(seen.Add(ObjectId.Create(42, node, 7).Value));
        }

        Assert.Equal(ObjectId.MaxNodeId + 1, seen.Count);
    }

    [Fact]
    public void SameNode_AllSequences_DoNotCollide()
    {
        HashSet<long> seen = new();

        for (int seq = 0; seq <= ObjectId.MaxSequence; seq++)
        {
            Assert.True(seen.Add(ObjectId.Create(42, 3, seq).Value));
        }

        Assert.Equal(ObjectId.MaxSequence + 1, seen.Count);
    }

    [Fact]
    public void Ordering_FollowsTimestampThenNodeThenSequence()
    {
        // 대략적 시간순 정렬이 DB 인덱스 이점의 근거다.
        Assert.True(ObjectId.Create(1, 0, 0) < ObjectId.Create(2, 0, 0));
        Assert.True(ObjectId.Create(1, 0, 0) < ObjectId.Create(1, 1, 0));
        Assert.True(ObjectId.Create(1, 0, 0) < ObjectId.Create(1, 0, 1));

        // 타임스탬프가 노드보다 우선한다.
        Assert.True(ObjectId.Create(1, ObjectId.MaxNodeId, ObjectId.MaxSequence) < ObjectId.Create(2, 0, 0));
    }

    [Fact]
    public void None_IsDefault()
    {
        Assert.True(ObjectId.None.IsNone);
        Assert.True(default(ObjectId).IsNone);
        Assert.False(ObjectId.Create(1, 0, 0).IsNone);
    }

    [Fact]
    public void RawValue_RoundTrips()
    {
        ObjectId original = ObjectId.Create(1_700_000_000_000L, 77, 999);

        Assert.Equal(original, new ObjectId(original.Value));
    }
}
