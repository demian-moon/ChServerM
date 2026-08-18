using System;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Identity;

/// <summary>
/// 레거시 <c>HashM</c> 은 오브젝트 스코프 키를 전역 작업 ID 로 그대로 썼다.
/// 두 오브젝트가 같은 키를 쓰면 두 번째 등록이 실패하고 만료가 조용히 사라졌다.
/// </summary>
public sealed class SessionIdTests
{
    [Fact]
    public void SessionId_WrapsObjectId()
    {
        ObjectId oid = ObjectId.Create(1_700_000_000_000L, 5, 42);
        SessionId sid = new(oid);

        Assert.Equal(oid, sid.Value);
        Assert.False(sid.IsNone);
    }

    [Fact]
    public void SessionId_None_IsDefault()
    {
        Assert.True(SessionId.None.IsNone);
        Assert.True(default(SessionId).IsNone);
    }

    [Fact]
    public void SessionId_PartitionKey_MatchesUnderlyingObjectId()
    {
        ObjectId oid = ObjectId.Create(1_700_000_000_000L, 5, 42);

        Assert.Equal(oid.ToPartitionKey(), new SessionId(oid).ToPartitionKey());
    }

    [Fact]
    public void JobId_SameLocalKey_DifferentOwners_DoNotCollide()
    {
        // 이것이 JobId 가 소유자 범위인 이유다.
        Assert.NotEqual(new JobId(owner: 1, local: 100), new JobId(owner: 2, local: 100));
    }

    [Fact]
    public void JobId_SameOwnerAndLocal_AreEqual()
    {
        Assert.Equal(new JobId(1, 100), new JobId(1, 100));
    }

    [Fact]
    public void JobId_None_IsDefault()
    {
        Assert.True(JobId.None.IsNone);
        Assert.False(new JobId(0, 1).IsNone);
        Assert.False(new JobId(1, 0).IsNone);
    }

    [Fact]
    public void NodeId_AcceptsFullObjectIdRange()
    {
        NodeId id = new(ObjectId.MaxNodeId);

        Assert.Equal(ObjectId.MaxNodeId, id.Value);
    }

    [Fact]
    public void NodeId_BeyondObjectIdCapacity_Throws()
    {
        // ObjectId 에 담기지 못할 노드 번호를 만들어 두면
        // 발급 시점이 아니라 조립 시점에 터진다 — 그때는 원인을 찾기 어렵다.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NodeId((ushort)(ObjectId.MaxNodeId + 1)));
    }

    [Fact]
    public void NodeId_Zero_IsReservedForNone()
    {
        // 감사 2026-08-18 C-6, 결정: 번호 0은 None 센티넬로 예약한다. 0이 유효한 노드면
        // "미설정"과 "0번 노드"가 구분되지 않아 번호 미기입이 유효 구성으로 통과한다.
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeId(0));
        Assert.True(NodeId.None.IsNone);
        Assert.False(new NodeId(1).IsNone);
        Assert.Equal(0, NodeId.None.Value);
    }
}
