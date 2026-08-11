using System;
using System.Collections.Generic;
using System.Linq;
using ChServerM.Time;
using Xunit;

namespace ChServerM.Matchmaking.Tests;

/// <summary>
/// <see cref="Matchmaker"/> 판정 테스트 — 확장 창·상호 호환·파티 패킹·만료·유계(ADR-0068).
/// </summary>
/// <remarks>
/// 시간은 전부 <see cref="ManualTimeProvider"/> 로 민다 — 대기 시간에 따른 창 확장이
/// 이 축의 본질이라, 실제 시계로는 결정적 테스트가 성립하지 않는다.
/// </remarks>
public sealed class MatchmakerTests
{
    // ── 기본 매칭 ────────────────────────────────────────────────────

    [Fact]
    public void 같은_레이팅_둘은_첫_패스에_매치된다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1();
        Enqueue(matchmaker, time, id: 1, rating: 1500);
        Enqueue(matchmaker, time, id: 2, rating: 1500);

        List<MatchProposal> matches = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches);

        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.Waiting);
        MatchProposal match = Assert.Single(matches);
        Assert.Equal(2, match.Teams.Count);
        Assert.All(match.Teams, team => Assert.Single(team));
    }

    [Fact]
    public void 창_밖의_상대와는_매치되지_않는다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1(initialWindow: 100, growth: 0);
        Enqueue(matchmaker, time, id: 1, rating: 1500);
        Enqueue(matchmaker, time, id: 2, rating: 1700);

        List<MatchProposal> matches = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches);

        Assert.Equal(0, result.Matched);
        Assert.Equal(2, result.Waiting);
    }

    [Fact]
    public void 기다리면_창이_자라서_매치된다()
    {
        // 격차 200, 초기 창 100, 초당 +50 → 양쪽 창이 200 이 되는 2초 후부터 호환.
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1(initialWindow: 100, growth: 50);
        Enqueue(matchmaker, time, id: 1, rating: 1500);
        Enqueue(matchmaker, time, id: 2, rating: 1700);

        List<MatchProposal> matches = [];
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.Equal(0, matchmaker.RunPass(Now(time), matches).Matched);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.Equal(1, matchmaker.RunPass(Now(time), matches).Matched);
    }

    [Fact]
    public void 호환은_양쪽_창이_서로를_덮어야_한다()
    {
        // 오래 기다린 티켓의 창은 격차를 덮지만, 방금 온 티켓의 창은 좁다 —
        // 한쪽 창만 검사하면 새 참가자가 품질 나쁜 매치로 끌려 들어간다(ADR-0068 결정 3).
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1(initialWindow: 100, growth: 100);
        Enqueue(matchmaker, time, id: 1, rating: 1500);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(10));   // 티켓 1 의 창: 1000(상한)
        Enqueue(matchmaker, time, id: 2, rating: 1800);    // 티켓 2 의 창: 100 < 격차 300

        List<MatchProposal> matches = [];
        Assert.Equal(0, matchmaker.RunPass(Now(time), matches).Matched);

        // 티켓 2 도 2초를 기다리면 창이 300 이 된다 → 호환.
        time.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        Assert.Equal(1, matchmaker.RunPass(Now(time), matches).Matched);
    }

    [Fact]
    public void 창_상한을_넘어서는_관대해지지_않는다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1(initialWindow: 100, growth: 100, maxWindow: 300);
        Enqueue(matchmaker, time, id: 1, rating: 1500);
        Enqueue(matchmaker, time, id: 2, rating: 1900);    // 격차 400 > 상한 300

        List<MatchProposal> matches = [];
        time.AdvanceMonotonic(TimeSpan.FromMinutes(1));

        Assert.Equal(0, matchmaker.RunPass(Now(time), matches).Matched);
    }

    [Fact]
    public void 가장_오래_기다린_티켓이_먼저_매치된다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1();
        Enqueue(matchmaker, time, id: 1, rating: 1500);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Enqueue(matchmaker, time, id: 2, rating: 1500);
        Enqueue(matchmaker, time, id: 3, rating: 1500);

        List<MatchProposal> matches = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches);

        // 셋 중 매치는 하나 — 최장 대기(1번)가 반드시 포함된다.
        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Waiting);
        Assert.Contains(
            matches[0].Teams.SelectMany(team => team),
            ticket => ticket.Id == new MatchTicketId(1));
    }

    [Fact]
    public void 한_패스가_성립하는_매치를_전부_뽑는다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1();
        for (ulong id = 1; id <= 6; id++)
        {
            Enqueue(matchmaker, time, id, rating: 1500);
        }

        List<MatchProposal> matches = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches);

        Assert.Equal(3, result.Matched);
        Assert.Equal(0, result.Waiting);
    }

    // ── 파티 패킹 (2v2) ──────────────────────────────────────────────

    [Fact]
    public void 파티2_솔로2가_2대2로_묶인다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create(teamSize: 2, teamCount: 2);
        Enqueue(matchmaker, time, id: 1, rating: 1500, partySize: 2);
        Enqueue(matchmaker, time, id: 2, rating: 1500);
        Enqueue(matchmaker, time, id: 3, rating: 1500);

        List<MatchProposal> matches = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches);

        Assert.Equal(1, result.Matched);
        MatchProposal match = Assert.Single(matches);

        // 각 팀 정원이 정확히 2 — 파티(2인)는 한 팀에 통째로 들어간다.
        Assert.All(match.Teams, team => Assert.Equal(2, team.Sum(ticket => ticket.PartySize)));
        int partyTeam = match.Teams[0].Any(ticket => ticket.Id == new MatchTicketId(1)) ? 0 : 1;
        Assert.Single(match.Teams[partyTeam]);
    }

    [Fact]
    public void 인원이_정확히_안_채워지면_매치는_없다()
    {
        // 2v2 에 3명(파티2 + 솔로1) — 팀 하나가 미달이므로 매치 불가.
        (Matchmaker matchmaker, ManualTimeProvider time) = Create(teamSize: 2, teamCount: 2);
        Enqueue(matchmaker, time, id: 1, rating: 1500, partySize: 2);
        Enqueue(matchmaker, time, id: 2, rating: 1500);

        List<MatchProposal> matches = [];
        Assert.Equal(0, matchmaker.RunPass(Now(time), matches).Matched);
        Assert.Equal(2, matchmaker.Count);
    }

    [Fact]
    public void 협동_모드도_성립한다_팀_하나()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create(teamSize: 4, teamCount: 1);
        Enqueue(matchmaker, time, id: 1, rating: 1500, partySize: 3);
        Enqueue(matchmaker, time, id: 2, rating: 1500);

        List<MatchProposal> matches = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches);

        Assert.Equal(1, result.Matched);
        Assert.Equal(4, Assert.Single(Assert.Single(matches).Teams.ToArray()).Sum(t => t.PartySize));
    }

    // ── 등록 거부 (실패는 값이다) ────────────────────────────────────

    [Fact]
    public void 팀_정원을_넘는_파티는_거부된다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create(teamSize: 2, teamCount: 2);

        MatchEnqueueStatus status = matchmaker.TryEnqueue(
            new MatchTicket(new MatchTicketId(1), 1500, partySize: 3, Now(time)));

        Assert.Equal(MatchEnqueueStatus.InvalidPartySize, status);
        Assert.Equal(0, matchmaker.Count);
    }

    [Fact]
    public void 가득_찬_큐는_거부한다_거부가_붕괴보다_낫다()
    {
        ManualTimeProvider time = new();
        Matchmaker matchmaker = new(new MatchmakingOptions { MaxQueueDepth = 2 }, time);

        Assert.Equal(MatchEnqueueStatus.Accepted, EnqueueStatus(matchmaker, time, 1));
        Assert.Equal(MatchEnqueueStatus.Accepted, EnqueueStatus(matchmaker, time, 2));
        Assert.Equal(MatchEnqueueStatus.QueueFull, EnqueueStatus(matchmaker, time, 3));
    }

    [Fact]
    public void 중복_ID_는_거부된다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1();

        Assert.Equal(MatchEnqueueStatus.Accepted, EnqueueStatus(matchmaker, time, 1));
        Assert.Equal(MatchEnqueueStatus.DuplicateTicket, EnqueueStatus(matchmaker, time, 1));
    }

    [Fact]
    public void NaN_레이팅은_거부된다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1();

        MatchEnqueueStatus status = matchmaker.TryEnqueue(
            new MatchTicket(new MatchTicketId(1), double.NaN, 1, Now(time)));

        Assert.Equal(MatchEnqueueStatus.InvalidRating, status);
    }

    // ── 취소·만료 ────────────────────────────────────────────────────

    [Fact]
    public void 취소된_티켓은_매치되지_않는다()
    {
        (Matchmaker matchmaker, ManualTimeProvider time) = Create1v1();
        Enqueue(matchmaker, time, id: 1, rating: 1500);
        Enqueue(matchmaker, time, id: 2, rating: 1500);

        Assert.True(matchmaker.TryCancel(new MatchTicketId(1)));
        Assert.False(matchmaker.TryCancel(new MatchTicketId(1)));

        List<MatchProposal> matches = [];
        Assert.Equal(0, matchmaker.RunPass(Now(time), matches).Matched);
        Assert.Equal(1, matchmaker.Count);
    }

    [Fact]
    public void 최대_대기를_넘기면_매치가_아니라_만료로_드러난다()
    {
        ManualTimeProvider time = new();
        Matchmaker matchmaker = new(
            new MatchmakingOptions { MaxWaitTime = TimeSpan.FromSeconds(30) }, time);
        Enqueue(matchmaker, time, id: 1, rating: 1500);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(31));
        Enqueue(matchmaker, time, id: 2, rating: 1500);   // 새 티켓 — 만료 대상 아님

        List<MatchProposal> matches = [];
        List<MatchTicket> expired = [];
        MatchmakingPassResult result = matchmaker.RunPass(Now(time), matches, expired);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Expired);
        Assert.Equal(new MatchTicketId(1), Assert.Single(expired).Id);
        Assert.Equal(1, result.Waiting);
    }

    // ── 도우미 ───────────────────────────────────────────────────────

    private static (Matchmaker Matchmaker, ManualTimeProvider Time) Create1v1(
        double initialWindow = 100, double growth = 50, double maxWindow = 1000)
        => Create(1, 2, initialWindow, growth, maxWindow);

    private static (Matchmaker Matchmaker, ManualTimeProvider Time) Create(
        int teamSize, int teamCount,
        double initialWindow = 100, double growth = 50, double maxWindow = 1000)
    {
        ManualTimeProvider time = new();
        Matchmaker matchmaker = new(
            new MatchmakingOptions
            {
                TeamSize = teamSize,
                TeamCount = teamCount,
                InitialRatingWindow = initialWindow,
                RatingWindowGrowthPerSecond = growth,
                MaxRatingWindow = maxWindow,
                MaxWaitTime = null,
            },
            time);
        return (matchmaker, time);
    }

    private static MonotonicTimestamp Now(ManualTimeProvider time) => MonotonicTimestamp.Now(time);

    private static void Enqueue(
        Matchmaker matchmaker, ManualTimeProvider time, ulong id, double rating, int partySize = 1)
        => Assert.Equal(
            MatchEnqueueStatus.Accepted,
            matchmaker.TryEnqueue(new MatchTicket(new MatchTicketId(id), rating, partySize, Now(time))));

    private static MatchEnqueueStatus EnqueueStatus(
        Matchmaker matchmaker, ManualTimeProvider time, ulong id)
        => matchmaker.TryEnqueue(new MatchTicket(new MatchTicketId(id), 1500, 1, Now(time)));

    /// <summary>테스트 전용 수동 시계 — Core.Tests 의 것과 같은 형태.</summary>
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
