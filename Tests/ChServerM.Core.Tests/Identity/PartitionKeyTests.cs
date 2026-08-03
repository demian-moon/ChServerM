using System;
using System.Collections.Generic;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Identity;

/// <summary>
/// ADR-0005 의 전제를 검증한다. 파티션 배정이 결정적이고 고르게 퍼지지 않으면
/// "같은 키 → 같은 소비자" 모델은 순서만 보장하고 확장은 못 하는 구조가 된다.
/// </summary>
public sealed class PartitionKeyTests
{
    [Fact]
    public void FromValue_IsDeterministic()
    {
        Assert.Equal(PartitionKey.FromValue(12345), PartitionKey.FromValue(12345));
    }

    [Fact]
    public void FromValue_DifferentInputs_ProduceDifferentKeys()
    {
        Assert.NotEqual(PartitionKey.FromValue(1), PartitionKey.FromValue(2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(1024)]
    public void ToIndex_StaysInRange(int partitionCount)
    {
        // 2의 거듭제곱이 아닌 개수도 포함한다 — 마스킹이 아니라 곱셈-시프트를 쓰는 이유다.
        for (ulong i = 0; i < 10_000; i++)
        {
            int index = PartitionKey.FromValue(i).ToIndex(partitionCount);
            Assert.InRange(index, 0, partitionCount - 1);
        }
    }

    [Fact]
    public void ToIndex_SameKeyAlwaysSamePartition()
    {
        // ADR-0005 의 순서 보장은 전적으로 이 성질에 의존한다.
        PartitionKey key = PartitionKey.FromValue(0xDEAD_BEEF);
        int expected = key.ToIndex(32);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(expected, PartitionKey.FromValue(0xDEAD_BEEF).ToIndex(32));
        }
    }

    [Fact]
    public void ToIndex_SequentialIds_SpreadAcrossPartitions()
    {
        // 레거시 oid % n 이 실패한 지점: 순차 ID 가 특정 샤드에 몰렸다.
        // 피보나치 해싱은 상위 비트에 엔트로피를 모으므로 고르게 퍼져야 한다.
        const int PartitionCount = 16;
        const int SampleCount = 160_000;
        const int Expected = SampleCount / PartitionCount;

        int[] histogram = new int[PartitionCount];
        for (ulong i = 0; i < SampleCount; i++)
        {
            histogram[PartitionKey.FromValue(i).ToIndex(PartitionCount)]++;
        }

        // 완전 균등(10,000)에서 ±5% 이내를 요구한다. 실제로는 훨씬 촘촘하다.
        foreach (int count in histogram)
        {
            Assert.InRange(count, (int)(Expected * 0.95), (int)(Expected * 1.05));
        }
    }

    [Fact]
    public void ToIndex_HighBitIds_AlsoSpread()
    {
        // ObjectId 는 타임스탬프가 상위 비트에 있어 하위 비트만 변한다.
        // 이 패턴에서도 분포가 무너지지 않아야 한다.
        const int PartitionCount = 8;
        HashSet<int> touched = new();

        long baseTimestamp = 1_700_000_000_000L;
        for (int seq = 0; seq < 4096; seq++)
        {
            ObjectId id = ObjectId.Create(baseTimestamp & ((1L << ObjectId.TimestampBits) - 1), 3, seq);
            touched.Add(id.ToPartitionKey().ToIndex(PartitionCount));
        }

        Assert.Equal(PartitionCount, touched.Count);
    }

    [Fact]
    public void ToIndex_SinglePartition_AlwaysZero()
    {
        Assert.Equal(0, PartitionKey.FromValue(ulong.MaxValue).ToIndex(1));
    }

    [Fact]
    public void FromPrecomputedHash_RoundTrips()
    {
        Assert.Equal(0xABCD_1234_5678_9EF0UL, PartitionKey.FromPrecomputedHash(0xABCD_1234_5678_9EF0UL).Value);
    }
}
