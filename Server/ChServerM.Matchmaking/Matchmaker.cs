using System;
using System.Collections.Generic;
using System.Diagnostics;
using ChServerM.Time;

namespace ChServerM.Matchmaking;

/// <summary>
/// 확장 창(expanding window) 매치 대기열 — 오래 기다릴수록 허용 레이팅 격차가 넓어진다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "대기 시간 vs 매치 품질" 트레이드오프의 참조 구현이다(ADR-0068).
/// 티켓의 허용 창은 대기 시간에 따라 자라고(<see cref="MatchmakingOptions"/>), 두 티켓은
/// <b>양쪽 창이 서로를 덮을 때만</b> 호환이다. 최대 대기를 넘긴 티켓은 억지로 매치되는
/// 것이 아니라 만료로 드러난다.
/// </para>
/// <para>
/// <b>스레드 규약 — 스레드 안전하지 않다.</b> 모든 멤버는 <b>단일 소유자</b>(파티션 하나
/// 또는 틱 루프)에서 부른다. 매치메이킹은 모든 후보를 함께 보는 전역 연산이라 파티셔닝으로
/// 쪼갤 수 없으므로, 한 실행 컨텍스트에 소유시키는 것이 share-nothing(9.1)의 답이다 —
/// 타이밍 휠과 같은 "수동 자료구조 + 외부 드라이버" 패턴(ADR-0068 결정 2). 다른 파티션의
/// 등록 요청은 소유 파티션으로 작업을 넘겨서 한다.
/// </para>
/// <para>
/// <b>알고리즘.</b> <see cref="RunPass"/> 마다: ① 만료 제거 → ② 가장 오래 기다린 티켓을
/// 앵커로, 앵커와 호환인 후보를 파티 크기 내림차순 first-fit 으로 팀에 채운다. 후보는
/// 이미 뽑힌 전원과도 상호 호환이어야 한다(앵커만 검사하면 전이적 관용으로 최악 격차가
/// 창의 2배까지 벌어진다). 전역 최적 빈 패킹(NP-hard)은 추구하지 않는다 — 패스당 비용
/// 유계가 우선이다(ADR-0068 결정 4).
/// </para>
/// <para>
/// <b>수명 규약.</b> 성립한 매치의 티켓은 큐에서 제거된다. <see cref="MatchProposal"/> 이후
/// (수락·ready check·결과 반영)는 호출자 도메인이다 — 큐는 매치 결과를 모른다(결정 5).
/// </para>
/// </remarks>
[DebuggerDisplay("대기 {Count}건")]
public sealed class Matchmaker
{
    private readonly TimeProvider _timeProvider;
    private readonly int _teamSize;
    private readonly int _teamCount;
    private readonly double _initialWindow;
    private readonly double _growthPerSecond;
    private readonly double _maxWindow;
    private readonly TimeSpan? _maxWaitTime;
    private readonly int _maxQueueDepth;

    /// <summary>도착 순 티켓 목록 — 인덱스 0 이 최장 대기(= 앵커 우선순위).</summary>
    private readonly List<MatchTicket> _tickets = [];

    private readonly HashSet<MatchTicketId> _ids = [];

    // ── 패스 스크래치 (재사용 — 패스당 할당은 매치 산출물뿐) ──
    private readonly List<int> _candidates = [];
    private readonly List<int> _selected = [];
    private readonly List<int> _selectedTeam = [];
    private readonly int[] _teamFill;
    private readonly Comparison<int> _candidateOrder;

    /// <summary>큐를 만든다.</summary>
    /// <param name="options">설정. <see langword="null"/>이면 기본값. 생성 후 원본 변경은 반영되지 않는다.</param>
    /// <param name="timeProvider">시간 원천. <see langword="null"/>이면 시스템 시계.</param>
    /// <exception cref="InvalidOperationException">설정이 성립하지 않을 때.</exception>
    public Matchmaker(MatchmakingOptions? options = null, TimeProvider? timeProvider = null)
    {
        options ??= new MatchmakingOptions();
        options.Validate();

        _timeProvider = timeProvider ?? TimeProvider.System;
        _teamSize = options.TeamSize;
        _teamCount = options.TeamCount;
        _initialWindow = options.InitialRatingWindow;
        _growthPerSecond = options.RatingWindowGrowthPerSecond;
        _maxWindow = options.MaxRatingWindow;
        _maxWaitTime = options.MaxWaitTime;
        _maxQueueDepth = options.MaxQueueDepth;

        _teamFill = new int[_teamCount];
        _candidateOrder = CompareCandidates;
    }

    /// <summary>대기 중인 티켓 수.</summary>
    public int Count => _tickets.Count;

    /// <summary>티켓을 등록한다.</summary>
    /// <returns>수락 여부와 거부 사유. 실패는 예외가 아니라 값이다.</returns>
    public MatchEnqueueStatus TryEnqueue(in MatchTicket ticket)
    {
        if (double.IsNaN(ticket.Rating) || double.IsInfinity(ticket.Rating))
        {
            return MatchEnqueueStatus.InvalidRating;
        }

        if (ticket.PartySize < 1 || ticket.PartySize > _teamSize)
        {
            return MatchEnqueueStatus.InvalidPartySize;
        }

        if (_tickets.Count >= _maxQueueDepth)
        {
            return MatchEnqueueStatus.QueueFull;
        }

        if (!_ids.Add(ticket.Id))
        {
            return MatchEnqueueStatus.DuplicateTicket;
        }

        _tickets.Add(ticket);
        return MatchEnqueueStatus.Accepted;
    }

    /// <summary>대기 중인 티켓을 물린다.</summary>
    /// <returns>있어서 제거했으면 참. 이미 매치·만료·취소된 티켓이면 거짓.</returns>
    public bool TryCancel(MatchTicketId id)
    {
        if (!_ids.Remove(id))
        {
            return false;
        }

        // O(n) 선형 탐색 — 취소는 드문 경로라 등록·패스 쪽 자료구조를 복잡하게 만들 이유가 없다.
        for (int i = 0; i < _tickets.Count; i++)
        {
            if (_tickets[i].Id == id)
            {
                _tickets.RemoveAt(i);
                break;
            }
        }

        return true;
    }

    /// <summary>매칭 패스 한 번 — 만료를 걷어내고, 성립하는 매치를 전부 뽑는다.</summary>
    /// <param name="now">현재 단조 시각. 드라이버(틱 루프 등)가 준다.</param>
    /// <param name="matches">성립한 매치가 추가되는 목록. 호출자 소유 — 재사용하려면 비우고 넘긴다.</param>
    /// <param name="expired">만료 티켓이 추가되는 목록. <see langword="null"/>이면 개수만 집계된다 — 유실을 관측하려면 넘긴다(9.6).</param>
    /// <returns>이번 패스의 집계.</returns>
    public MatchmakingPassResult RunPass(
        MonotonicTimestamp now,
        ICollection<MatchProposal> matches,
        ICollection<MatchTicket>? expired = null)
    {
        ArgumentNullException.ThrowIfNull(matches);

        int expiredCount = RemoveExpired(now, expired);

        int matchedCount = 0;
        bool progress = true;
        while (progress)
        {
            progress = false;

            // 인덱스 0 = 최장 대기. 매치가 나오면 목록이 바뀌므로 처음(최장 대기)부터 다시 본다.
            for (int anchor = 0; anchor < _tickets.Count; anchor++)
            {
                if (TryBuildMatch(anchor, now, out MatchProposal? match))
                {
                    matches.Add(match!);
                    matchedCount++;
                    progress = true;
                    break;
                }
            }
        }

        return new MatchmakingPassResult(matchedCount, expiredCount, _tickets.Count);
    }

    private int RemoveExpired(MonotonicTimestamp now, ICollection<MatchTicket>? expired)
    {
        if (_maxWaitTime is not { } maxWait)
        {
            return 0;
        }

        int removed = 0;
        for (int i = _tickets.Count - 1; i >= 0; i--)
        {
            if (_tickets[i].EnqueuedAt.ElapsedTo(_timeProvider, now) >= maxWait)
            {
                expired?.Add(_tickets[i]);
                _ids.Remove(_tickets[i].Id);
                _tickets.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private bool TryBuildMatch(int anchorIndex, MonotonicTimestamp now, out MatchProposal? match)
    {
        match = null;

        MatchTicket anchor = _tickets[anchorIndex];
        int playersNeeded = _teamSize * _teamCount;

        // 후보 수집 — 앵커와 상호 호환인 티켓 전부.
        _candidates.Clear();
        int reachablePlayers = anchor.PartySize;
        for (int i = 0; i < _tickets.Count; i++)
        {
            if (i == anchorIndex)
            {
                continue;
            }

            if (AreCompatible(anchor, _tickets[i], now))
            {
                _candidates.Add(i);
                reachablePlayers += _tickets[i].PartySize;
            }
        }

        if (reachablePlayers < playersNeeded)
        {
            return false;
        }

        // 파티 크기 내림차순(동률이면 오래 기다린 순) — 큰 파티부터 놓아야 빈 패킹 실패가 준다.
        _candidates.Sort(_candidateOrder);

        // 앵커를 먼저 팀 0 에 — 이 매치는 앵커의 대기를 끝내기 위한 것이다.
        _selected.Clear();
        _selectedTeam.Clear();
        Array.Clear(_teamFill);

        _selected.Add(anchorIndex);
        _selectedTeam.Add(0);
        _teamFill[0] = anchor.PartySize;
        int placed = anchor.PartySize;

        foreach (int index in _candidates)
        {
            MatchTicket candidate = _tickets[index];

            // 이미 뽑힌 전원과 상호 호환이어야 한다 — 앵커만 검사하면 전이적 관용으로
            // 최악 레이팅 격차가 창의 2배까지 벌어진다(모듈 주석의 알고리즘 절).
            bool compatibleWithSelected = true;
            foreach (int selectedIndex in _selected)
            {
                if (selectedIndex != anchorIndex
                    && !AreCompatible(candidate, _tickets[selectedIndex], now))
                {
                    compatibleWithSelected = false;
                    break;
                }
            }

            if (!compatibleWithSelected)
            {
                continue;
            }

            for (int team = 0; team < _teamCount; team++)
            {
                if (_teamFill[team] + candidate.PartySize <= _teamSize)
                {
                    _selected.Add(index);
                    _selectedTeam.Add(team);
                    _teamFill[team] += candidate.PartySize;
                    placed += candidate.PartySize;
                    break;
                }
            }

            if (placed == playersNeeded)
            {
                break;
            }
        }

        if (placed != playersNeeded)
        {
            return false;
        }

        match = BuildProposalAndRemoveSelected();
        return true;
    }

    private MatchProposal BuildProposalAndRemoveSelected()
    {
        // 매치 산출물 할당은 의도된 결정이다(ADR-0068 결정 5) — 매치는 핫패스가 아니다.
        var teams = new MatchTicket[_teamCount][];
        for (int team = 0; team < _teamCount; team++)
        {
            int members = 0;
            for (int i = 0; i < _selectedTeam.Count; i++)
            {
                if (_selectedTeam[i] == team)
                {
                    members++;
                }
            }

            teams[team] = new MatchTicket[members];
        }

        Span<int> writeCursor = stackalloc int[_teamCount];
        for (int i = 0; i < _selected.Count; i++)
        {
            int team = _selectedTeam[i];
            teams[team][writeCursor[team]++] = _tickets[_selected[i]];
        }

        // 큰 인덱스부터 제거해야 앞선 인덱스가 밀리지 않는다.
        _selected.Sort();
        for (int i = _selected.Count - 1; i >= 0; i--)
        {
            int index = _selected[i];
            _ids.Remove(_tickets[index].Id);
            _tickets.RemoveAt(index);
        }

        return new MatchProposal(teams);
    }

    /// <summary>두 티켓이 지금 호환인가 — 양쪽 창이 서로의 격차를 덮어야 한다(ADR-0068 결정 3).</summary>
    private bool AreCompatible(in MatchTicket left, in MatchTicket right, MonotonicTimestamp now)
    {
        double delta = Math.Abs(left.Rating - right.Rating);
        return delta <= Math.Min(WindowFor(left, now), WindowFor(right, now));
    }

    private double WindowFor(in MatchTicket ticket, MonotonicTimestamp now)
    {
        double waitedSeconds = Math.Max(0, ticket.EnqueuedAt.ElapsedTo(_timeProvider, now).TotalSeconds);
        return Math.Min(_initialWindow + (_growthPerSecond * waitedSeconds), _maxWindow);
    }

    private int CompareCandidates(int left, int right)
    {
        int bySize = _tickets[right].PartySize.CompareTo(_tickets[left].PartySize);
        return bySize != 0 ? bySize : left.CompareTo(right);
    }
}
