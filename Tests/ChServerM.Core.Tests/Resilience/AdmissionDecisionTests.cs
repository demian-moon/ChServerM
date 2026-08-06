using ChServerM.Resilience;
using Xunit;

namespace ChServerM.Core.Tests.Resilience;

/// <summary>
/// 수용 판정 값의 불변식 — "default = 거부"가 핵심이다. 초기화 누락이 수용으로
/// 위장하면 과부하 방어가 조용히 무력화된다.
/// </summary>
public sealed class AdmissionDecisionTests
{
    [Fact]
    public void Default_IsReject()
    {
        AdmissionDecision decision = default;

        Assert.False(decision.IsAdmitted);
        Assert.Null(decision.RejectionReason);
    }

    [Fact]
    public void Admit_CarriesNoReason()
    {
        AdmissionDecision decision = AdmissionDecision.Admit();

        Assert.True(decision.IsAdmitted);
        Assert.Null(decision.RejectionReason);
    }

    [Fact]
    public void Reject_CarriesReason()
    {
        AdmissionDecision decision = AdmissionDecision.Reject("rate exceeded");

        Assert.False(decision.IsAdmitted);
        Assert.Equal("rate exceeded", decision.RejectionReason);
    }

    [Fact]
    public void Equality_ComparesAllFields()
    {
        Assert.Equal(AdmissionDecision.Admit(), AdmissionDecision.Admit());
        Assert.NotEqual(AdmissionDecision.Admit(), AdmissionDecision.Reject());
        Assert.True(AdmissionDecision.Reject("a") != AdmissionDecision.Reject("b"));
        Assert.True(AdmissionDecision.Reject() == default);
    }
}
