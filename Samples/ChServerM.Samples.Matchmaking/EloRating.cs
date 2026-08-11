using System;

namespace ChServerM.Samples.Matchmaking;

/// <summary>
/// Elo 레이팅 갱신 — <b>프레임워크가 아니라 샘플에 있는 이유가 이 파일의 요점이다.</b>
/// </summary>
/// <remarks>
/// <para>
/// 레이팅 공식은 도메인이다(ADR-0004·ADR-0068 결정 5). 프레임워크의
/// <c>MatchTicket.Rating</c> 은 값 하나를 나를 뿐, 그 값을 어떻게 만들고 갱신하는지는
/// 조립하는 쪽이 정한다. 여기서는 가장 단순한 Elo 를 쓴다 — Glicko-2·WengLin 같은
/// 불확실성 추적 공식도 같은 이음새(매치 결과 → 새 레이팅)에 그대로 꽂힌다.
/// </para>
/// <para>레거시의 GlickoM(301줄)·WengLinM(626줄)은 참조 0 인 준비 코드였다 — 승계하지 않고,
/// 필요해지면 이 자리에서 새로 구현한다.</para>
/// </remarks>
internal static class EloRating
{
    /// <summary>K-팩터 — 한 판이 레이팅을 흔드는 최대 폭.</summary>
    private const double KFactor = 32;

    /// <summary>1대1 결과를 반영한 새 레이팅 쌍을 돌려준다.</summary>
    /// <param name="winner">승자의 현재 레이팅.</param>
    /// <param name="loser">패자의 현재 레이팅.</param>
    public static (double Winner, double Loser) ApplyResult(double winner, double loser)
    {
        // 기대 승률: 400점 차이가 10배의 승산.
        double expectedWinner = 1.0 / (1.0 + Math.Pow(10, (loser - winner) / 400.0));

        double delta = KFactor * (1.0 - expectedWinner);
        return (winner + delta, loser - delta);
    }
}
