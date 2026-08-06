using System;
using ChServerM.Handshake;
using Xunit;

namespace ChServerM.Core.Tests.Handshake;

/// <summary>
/// 버전 구간의 불변식과 선택 규칙을 고정한다. 선택 규칙이 흔들리면 롤링 배포 중
/// 양쪽이 서로 다른 버전으로 말하게 된다 — 경계값을 전부 못박는다.
/// </summary>
public sealed class ProtocolVersionRangeTests
{
    [Fact]
    public void Constructor_RejectsZeroMin()
    {
        // 버전 0 은 "설정되지 않음" 센티넬이다 — 구간에 들어올 수 없다.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolVersionRange(0, 1));
    }

    [Fact]
    public void Constructor_RejectsMaxBelowMin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolVersionRange(3, 2));
    }

    [Fact]
    public void SingleVersionRange_IsValid()
    {
        ProtocolVersionRange range = new(1, 1);

        Assert.Equal(1, range.Min);
        Assert.Equal(1, range.Max);
    }

    [Theory]
    [InlineData((ushort)2, true)]
    [InlineData((ushort)5, true)]
    [InlineData((ushort)3, true)]
    [InlineData((ushort)1, false)]
    [InlineData((ushort)6, false)]
    [InlineData((ushort)0, false)]
    public void Contains_ChecksClosedInterval(ushort version, bool expected)
    {
        ProtocolVersionRange range = new(2, 5);

        Assert.Equal(expected, range.Contains(version));
    }

    [Fact]
    public void DefaultSentinel_ContainsNothing()
    {
        ProtocolVersionRange sentinel = default;

        Assert.False(sentinel.Contains(0));
        Assert.False(sentinel.Contains(1));
    }

    [Theory]
    [InlineData((ushort)1, (ushort)3, (ushort)2, (ushort)5, (ushort)3)] // 부분 겹침 → 공통 최고
    [InlineData((ushort)2, (ushort)5, (ushort)1, (ushort)3, (ushort)3)] // 좌우 대칭
    [InlineData((ushort)1, (ushort)1, (ushort)1, (ushort)1, (ushort)1)] // 현존 유일 조합
    [InlineData((ushort)1, (ushort)9, (ushort)4, (ushort)4, (ushort)4)] // 한쪽이 단일 버전
    [InlineData((ushort)3, (ushort)7, (ushort)3, (ushort)7, (ushort)7)] // 동일 구간 → 최고
    public void TrySelect_PicksHighestCommonVersion(
        ushort localMin, ushort localMax, ushort remoteMin, ushort remoteMax, ushort expected)
    {
        bool selectedOk = ProtocolVersionRange.TrySelect(
            new ProtocolVersionRange(localMin, localMax),
            new ProtocolVersionRange(remoteMin, remoteMax),
            out ushort selected);

        Assert.True(selectedOk);
        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData((ushort)1, (ushort)1, (ushort)2, (ushort)3)] // 인접하지만 겹치지 않음
    [InlineData((ushort)5, (ushort)9, (ushort)1, (ushort)4)] // 완전 분리
    public void TrySelect_FailsWithoutIntersection(
        ushort localMin, ushort localMax, ushort remoteMin, ushort remoteMax)
    {
        bool selectedOk = ProtocolVersionRange.TrySelect(
            new ProtocolVersionRange(localMin, localMax),
            new ProtocolVersionRange(remoteMin, remoteMax),
            out ushort selected);

        Assert.False(selectedOk);
        Assert.Equal(0, selected);
    }

    [Fact]
    public void TrySelect_FailsOnSentinel_EvenWhenBothAreSentinels()
    {
        // 양쪽 다 default 면 [0,0] 교집합이 "성립"해 버전 0을 고를 뻔한다 — 명시 차단 검증.
        Assert.False(ProtocolVersionRange.TrySelect(default, new ProtocolVersionRange(1, 1), out _));
        Assert.False(ProtocolVersionRange.TrySelect(new ProtocolVersionRange(1, 1), default, out _));
        Assert.False(ProtocolVersionRange.TrySelect(default, default, out ushort selected));
        Assert.Equal(0, selected);
    }

    [Fact]
    public void Equality_ComparesBothBounds()
    {
        Assert.Equal(new ProtocolVersionRange(1, 3), new ProtocolVersionRange(1, 3));
        Assert.NotEqual(new ProtocolVersionRange(1, 3), new ProtocolVersionRange(1, 4));
        Assert.True(new ProtocolVersionRange(2, 2) == new ProtocolVersionRange(2, 2));
        Assert.True(new ProtocolVersionRange(2, 2) != new ProtocolVersionRange(2, 3));
    }
}
