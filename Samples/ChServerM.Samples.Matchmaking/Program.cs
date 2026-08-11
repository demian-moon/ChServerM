using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ChServerM.Matchmaking;
using ChServerM.Time;

namespace ChServerM.Samples.Matchmaking;

/// <summary>
/// 매치메이킹 축 + 레이팅 반영의 조립 예제 — 32명 리그 시뮬레이션.
/// </summary>
/// <remarks>
/// <para>이 프로그램이 실증하는 것.</para>
/// <list type="number">
///   <item><description><b>큐 사용법</b> — 등록 → 틱마다 <c>RunPass</c> → 매치 소비.
///     드라이버(여기서는 시뮬레이션 루프)가 시간을 밀고 패스를 부른다 — 큐는 스레드를
///     갖지 않는 수동 자료구조다(ADR-0068 결정 2).</description></item>
///   <item><description><b>결과 반영은 프레임워크 밖</b> — 매치 결과로 Elo 를 갱신해
///     다음 등록의 <c>Rating</c> 으로 쓴다. 큐는 결과를 모른다(결정 5).</description></item>
///   <item><description><b>확장 창의 효과</b> — 라운드가 갈수록 레이팅이 벌어져도
///     대기 시간이 창을 넓혀 매치가 계속 성립한다.</description></item>
/// </list>
/// <para>고정 시드 난수 + 수동 시계라 실행마다 결과가 같다. 자체 검증은 "숨은 실력과
/// 최종 레이팅의 순위 상관"을 본다 — 매치와 반영이 실제로 맞물려 돌았다는 증거다.</para>
/// </remarks>
internal static class Program
{
    private const int Players = 32;

    /// <summary>라운드 수 — Elo 는 비슷한 상대끼리 붙을수록 전역 순위 발견이 느려서(매치메이킹의
    /// 아이러니), 상위권 순위가 안정되려면 이 정도가 필요하다.</summary>
    private const int Rounds = 80;

    // CA5394 억제: 이 난수는 보안이 아니라 "고정 시드 결정적 시뮬레이션"이 목적이다 —
    // 암호학적 난수를 쓰면 오히려 실행마다 결과가 달라져 자체 검증이 무너진다.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5394:안전하지 않은 임의성을 사용하지 마세요.",
        Justification = "고정 시드 시뮬레이션 — 재현성이 요구 사항이고 보안 문맥이 아니다.")]
    private static int Main()
    {
        UseUtf8Console();
        Console.WriteLine("ChServerM 매치메이킹 샘플 — 확장 창 큐 + Elo 반영, 32명 리그 시뮬레이션.");
        Console.WriteLine();

        ManualTimeProvider time = new();
        Matchmaker matchmaker = new(
            new MatchmakingOptions
            {
                TeamSize = 1,
                TeamCount = 2,
                InitialRatingWindow = 50,
                RatingWindowGrowthPerSecond = 25,
                // 상한은 시뮬레이션의 최대 레이팅 폭보다 넉넉히 — 상한이 좁으면 마지막 남은
                // 원거리 짝이 영원히 매치되지 않는다(아래 안전 상한이 그 경우를 소리내 알린다).
                MaxRatingWindow = 800,
                MaxWaitTime = null,
            },
            time);

        // 숨은 실력(시뮬레이션의 정답)과 공개 레이팅(큐가 보는 값). 전원 1500 에서 출발한다.
        Random rng = new(Seed: 7);
        double[] trueSkill = new double[Players];
        double[] rating = new double[Players];
        for (int i = 0; i < Players; i++)
        {
            trueSkill[i] = 1500 + (rng.NextDouble() - 0.5) * 600;
            rating[i] = 1500;
        }

        List<MatchProposal> matches = [];
        List<MatchTicket> expired = [];
        int totalMatches = 0;

        for (int round = 0; round < Rounds; round++)
        {
            // 전원 등록 — 티켓 ID = 플레이어 번호, 레이팅은 직전 라운드까지의 Elo.
            for (int player = 0; player < Players; player++)
            {
                MatchEnqueueStatus status = matchmaker.TryEnqueue(new MatchTicket(
                    new MatchTicketId((ulong)player), rating[player], 1, MonotonicTimestamp.Now(time)));

                if (status != MatchEnqueueStatus.Accepted)
                {
                    Console.Error.WriteLine($"  등록 실패: 플레이어 {player} → {status}");
                    return 1;
                }
            }

            // 매치가 마를 때까지 시간을 밀며 패스를 돈다 — 창이 자라며 나머지가 묶인다.
            matches.Clear();
            int simulatedSeconds = 0;
            while (matchmaker.Count > 0)
            {
                if (++simulatedSeconds > 300)
                {
                    Console.Error.WriteLine(
                        $"  라운드 {round}: 300초(모의)에도 {matchmaker.Count}건이 남았다 — MaxRatingWindow 를 확인한다.");
                    return 1;
                }

                time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
                matchmaker.RunPass(MonotonicTimestamp.Now(time), matches, expired);
            }

            // 결과 반영 — 숨은 실력이 높은 쪽이 확률적으로 이긴다(고정 시드 → 결정적).
            foreach (MatchProposal match in matches)
            {
                int a = (int)match.Teams[0][0].Id.Value;
                int b = (int)match.Teams[1][0].Id.Value;

                double chanceA = 1.0 / (1.0 + Math.Pow(10, (trueSkill[b] - trueSkill[a]) / 400.0));
                (int winner, int loser) = rng.NextDouble() < chanceA ? (a, b) : (b, a);

                (rating[winner], rating[loser]) = EloRating.ApplyResult(rating[winner], rating[loser]);
                totalMatches++;
            }
        }

        // ── 자체 검증 ────────────────────────────────────────────────

        bool ok = true;

        // 1) 전원 짝수이므로 라운드마다 전원이 매치돼야 한다 — 만료·잔류 0.
        ok &= Check(totalMatches == Players / 2 * Rounds, $"매치 {Players / 2 * Rounds}건 전부 성립 (실제 {totalMatches}건)");
        ok &= Check(expired.Count == 0, "만료 0건 — 창 확장이 전원을 묶었다");

        // 2) 최종 레이팅 순위가 숨은 실력 순위를 따라간다 — 상위 8명 중 6명 이상 일치.
        int[] bySkill = [.. Enumerable.Range(0, Players).OrderByDescending(i => trueSkill[i]).Take(8)];
        int[] byRating = [.. Enumerable.Range(0, Players).OrderByDescending(i => rating[i]).Take(8)];
        int overlap = bySkill.Intersect(byRating).Count();
        ok &= Check(overlap >= 6, $"실력 상위 8명 중 {overlap}명이 레이팅 상위 8명과 일치 (기준 ≥6)");

        // 3) 반영이 실제로 일어났다 — 레이팅이 퍼졌다.
        double spread = rating.Max() - rating.Min();
        ok &= Check(spread > 100, $"최종 레이팅 폭 {spread:F0}점 — 반영이 동작했다 (기준 >100)");

        Console.WriteLine();
        if (ok)
        {
            Console.WriteLine("통과 — 큐(프레임워크)와 레이팅(도메인)이 이음새 하나로 맞물려 돈다.");
            return 0;
        }

        Console.Error.WriteLine("실패 — 위 결과를 확인한다.");
        return 1;
    }

    private static bool Check(bool condition, string description)
    {
        Console.WriteLine($"  {(condition ? "성공" : "실패")}  {description}");
        return condition;
    }

    /// <summary>콘솔 출력을 UTF-8 로 맞춘다. (EchoServer 와 같은 이유 — 리다이렉트 환경에서는 삼킨다.)</summary>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // 콘솔이 없거나 리다이렉트됐다.
        }
        catch (PlatformNotSupportedException)
        {
            // 이 플랫폼은 인코딩 변경을 지원하지 않는다.
        }
    }

    /// <summary>시뮬레이션 전용 수동 시계 — 실행마다 같은 결과를 위해 실제 시계를 쓰지 않는다.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 10_000_000;

        public override long GetTimestamp() => _timestamp;

        /// <summary>단조 시각만 앞으로 민다.</summary>
        public void AdvanceMonotonic(TimeSpan delta) =>
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
    }
}
