using Xunit;

namespace ChServerM.RealTime.Spatial.Tests;

public sealed class MortonCodeTests
{
    [Theory]
    [InlineData(0, 0, 0u)]
    [InlineData(1, 0, 1u)]
    [InlineData(0, 1, 2u)]
    [InlineData(1, 1, 3u)]
    [InlineData(2, 2, 12u)]
    [InlineData(3, 5, 39u)]              // 교차 배치 수기 검증: y=101, x=011 → 100111
    [InlineData(65535, 65535, 0xFFFFFFFFu)]
    public void 알려진_값이_맞는다(int x, int y, uint expected)
    {
        Assert.Equal(expected, MortonCode.Encode((ushort)x, (ushort)y));
    }

    [Fact]
    public void 인코딩과_디코딩은_역이다()
    {
        for (int x = 0; x < 64; x += 7)
        {
            for (int y = 0; y < 64; y += 5)
            {
                (ushort dx, ushort dy) = MortonCode.Decode(MortonCode.Encode((ushort)x, (ushort)y));
                Assert.Equal(x, dx);
                Assert.Equal(y, dy);
            }
        }
    }

    [Fact]
    public void 공간_지역성이_보존된다()
    {
        // Z-order 의 핵심 성질: 2×2 블록의 네 셀은 연속한 키 4개다.
        uint baseKey = MortonCode.Encode(4, 6);
        Assert.Equal(baseKey + 1, MortonCode.Encode(5, 6));
        Assert.Equal(baseKey + 2, MortonCode.Encode(4, 7));
        Assert.Equal(baseKey + 3, MortonCode.Encode(5, 7));
    }
}
