using System;
using ChServerM.Security;
using Xunit;

namespace ChServerM.Core.Tests.Security;

/// <summary>
/// 인증 결과 값의 불변식을 고정한다 — 특히 "default = 실패"와 "전이 0 성공 금지".
/// 이 둘이 흔들리면 초기화 누락이 인증 통과로, 죽은 설정이 성공으로 위장한다.
/// </summary>
public sealed class AuthenticationResultTests
{
    [Fact]
    public void Default_IsFailure()
    {
        // 초기화를 빠뜨린 경로는 가장 제한적인 결과로 수렴해야 한다.
        AuthenticationResult result = default;

        Assert.False(result.IsAuthenticated);
        Assert.Equal(0u, result.GrantedStates);
        Assert.Null(result.FailureDescription);
    }

    [Fact]
    public void Success_CarriesGrantedStates()
    {
        AuthenticationResult result = AuthenticationResult.Success(0b0110);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(0b0110u, result.GrantedStates);
        Assert.Null(result.FailureDescription);
    }

    [Fact]
    public void Success_RejectsZeroStates()
    {
        // 전부-거부 상태로의 "성공"은 어떤 메시지도 통과 못 하는 죽은 설정이다.
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthenticationResult.Success(0));
    }

    [Fact]
    public void Failure_CarriesDescription_ButNoStates()
    {
        AuthenticationResult result = AuthenticationResult.Failure("만료된 토큰");

        Assert.False(result.IsAuthenticated);
        Assert.Equal(0u, result.GrantedStates);
        Assert.Equal("만료된 토큰", result.FailureDescription);
    }

    [Fact]
    public void Equality_ComparesAllFields()
    {
        Assert.Equal(AuthenticationResult.Success(1), AuthenticationResult.Success(1));
        Assert.NotEqual(AuthenticationResult.Success(1), AuthenticationResult.Success(2));
        Assert.NotEqual(AuthenticationResult.Success(1), AuthenticationResult.Failure());
        Assert.True(AuthenticationResult.Failure("a") != AuthenticationResult.Failure("b"));
        Assert.True(AuthenticationResult.Failure() == default);
    }
}
