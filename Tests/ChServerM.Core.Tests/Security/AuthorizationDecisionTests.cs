using ChServerM.Security;
using Xunit;

namespace ChServerM.Core.Tests.Security;

/// <summary>
/// 인가 판정 값의 불변식 — "default = 거부"가 핵심이다. 이것이 흔들리면
/// 초기화 누락이 인가 통과로 위장한다.
/// </summary>
public sealed class AuthorizationDecisionTests
{
    [Fact]
    public void Default_IsDeny()
    {
        AuthorizationDecision decision = default;

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.DenyDescription);
    }

    [Fact]
    public void Allow_CarriesNoDescription()
    {
        AuthorizationDecision decision = AuthorizationDecision.Allow();

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.DenyDescription);
    }

    [Fact]
    public void Deny_CarriesDescription()
    {
        AuthorizationDecision decision = AuthorizationDecision.Deny("소유자 불일치");

        Assert.False(decision.IsAllowed);
        Assert.Equal("소유자 불일치", decision.DenyDescription);
    }

    [Fact]
    public void Equality_ComparesAllFields()
    {
        Assert.Equal(AuthorizationDecision.Allow(), AuthorizationDecision.Allow());
        Assert.NotEqual(AuthorizationDecision.Allow(), AuthorizationDecision.Deny());
        Assert.True(AuthorizationDecision.Deny("a") != AuthorizationDecision.Deny("b"));
        Assert.True(AuthorizationDecision.Deny() == default);
    }
}
