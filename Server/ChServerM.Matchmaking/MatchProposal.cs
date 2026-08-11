using System;
using System.Collections.Generic;

namespace ChServerM.Matchmaking;

/// <summary>
/// <see cref="Matchmaker.TryEnqueue"/> 의 결과.
/// </summary>
public enum MatchEnqueueStatus
{
    /// <summary>등록됐다.</summary>
    Accepted = 0,

    /// <summary>큐가 가득 찼다(<see cref="MatchmakingOptions.MaxQueueDepth"/>). 거부가 붕괴보다 낫다(9.6).</summary>
    QueueFull = 1,

    /// <summary>같은 ID 의 티켓이 이미 대기 중이다.</summary>
    DuplicateTicket = 2,

    /// <summary>파티 인원이 1 미만이거나 팀 정원(<see cref="MatchmakingOptions.TeamSize"/>)을 넘는다 — 파티는 쪼개지지 않는다.</summary>
    InvalidPartySize = 3,

    /// <summary>레이팅이 NaN 또는 무한대다.</summary>
    InvalidRating = 4,
}

/// <summary>
/// 성립한 매치 하나 — 팀별 티켓 목록.
/// </summary>
/// <remarks>
/// <para>
/// 티켓 배열은 매치마다 새로 할당된다. **의도된 결정이다**(ADR-0068 결정 5) — 매치 빈도는
/// 메시지 빈도보다 자릿수로 낮아 핫패스가 아니고, 산출물은 큐 밖(세션 생성·통지)으로
/// 넘어가므로 수명이 큐와 무관하다. 측정으로 뒤집히면 그때 풀링한다.
/// </para>
/// <para>
/// 큐는 여기서 손을 뗀다 — 매치를 수락으로 볼지, 참가자 확인(ready check)을 거칠지,
/// 결과를 레이팅에 어떻게 반영할지는 전부 호출자 도메인이다.
/// </para>
/// </remarks>
public sealed class MatchProposal
{
    internal MatchProposal(MatchTicket[][] teams) => Teams = teams;

    /// <summary>팀별 티켓. 바깥 배열 길이 = <see cref="MatchmakingOptions.TeamCount"/>, 각 팀의 인원 합 = <see cref="MatchmakingOptions.TeamSize"/>.</summary>
    public IReadOnlyList<IReadOnlyList<MatchTicket>> Teams { get; }
}

/// <summary>
/// <see cref="Matchmaker.RunPass"/> 한 번의 집계.
/// </summary>
/// <param name="Matched">이번 패스에 성립한 매치 수.</param>
/// <param name="Expired">이번 패스에 만료된 티켓 수. <b>버리지 않는다</b> — 관측되지 않는 유실은 존재하지 않는 것과 같다(9.6).</param>
/// <param name="Waiting">패스 후에도 대기 중인 티켓 수.</param>
public readonly record struct MatchmakingPassResult(int Matched, int Expired, int Waiting);
