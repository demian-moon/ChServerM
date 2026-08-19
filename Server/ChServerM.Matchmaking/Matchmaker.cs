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
/// <b>앵커 재개(감사 2026-08-18 R-4 ②).</b> 매치가 성립하면 앵커 0부터 재시작하지 않고
/// 현재 앵커 위치(제거된 티켓 수만큼 보정)에서 스캔을 계속한다 — 티켓 제거는 후보 집합을
/// 줄이기만 하므로 이미 실패한 앞선 앵커가 같은 패스에서 다시 성공할 수는 없다. 결과는
/// 재시작 방식과 동일하고, 성공한 매치마다 붙던 O(n) 재스캔 낭비만 사라진다.
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
    private readonly int _maxChecksPerPass;

    /// <summary>상한 도달로 끊긴 패스의 재개 앵커. 0 이면 처음부터(완주한 패스가 되돌린다).</summary>
    /// <remarks>
    /// 정확한 이어달리기가 아니라 <b>기아 방지 장치</b>다 — 등록·취소·만료로 인덱스가 밀릴 수
    /// 있고, 그 오차는 다음 완주 패스가 해소한다. 정확한 위치 추적(ID 기반 커서 등)은 단순성을
    /// 해쳐 택하지 않았다(감사 2026-08-18 R-4 ③, 단순성 우선으로 결정·문서화).
    /// </remarks>
    private int _resumeAnchor;

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
        _maxChecksPerPass = options.MaxCompatibilityChecksPerPass;

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

    /// <summary>매칭 패스 한 번 — 만료를 걷어내고, 성립하는 매치를 뽑는다.</summary>
    /// <param name="now">현재 단조 시각. 드라이버(틱 루프 등)가 준다.</param>
    /// <param name="matches">성립한 매치가 추가되는 목록. 호출자 소유 — 재사용하려면 비우고 넘긴다.</param>
    /// <param name="expired">만료 티켓이 추가되는 목록. <see langword="null"/>이면 개수만 집계된다 — 유실을 관측하려면 넘긴다(9.6).</param>
    /// <returns>이번 패스의 집계.</returns>
    /// <remarks>
    /// <para>
    /// 상한(<see cref="MatchmakingOptions.MaxCompatibilityChecksPerPass"/>)이 없으면 성립하는
    /// 매치를 전부 뽑는다. 상한이 있으면 도달 시점의 앵커 위치에서 패스를 중단하고, 다음
    /// 패스가 그 위치에서 이어서 본다 — 완주한 패스는 재개 위치를 처음(최장 대기)으로
    /// 되돌린다. 재개 위치는 대략적이다(등록·취소·만료로 밀릴 수 있다) — 기아 방지가 목적이지
    /// 정확한 이어달리기가 아니다(감사 2026-08-18 R-4 ③).
    /// </para>
    /// </remarks>
    public MatchmakingPassResult RunPass(
        MonotonicTimestamp now,
        ICollection<MatchProposal> matches,
        ICollection<MatchTicket>? expired = null)
    {
        ArgumentNullException.ThrowIfNull(matches);

        int expiredCount = RemoveExpired(now, expired);

        int matchedCount = 0;
        int checksUsed = 0;
        bool truncated = false;

        // 인덱스 0 = 최장 대기. 상한으로 끊긴 직전 패스가 있으면 그 근처에서 이어서 본다.
        int anchor = _resumeAnchor < _tickets.Count ? _resumeAnchor : 0;
        while (anchor < _tickets.Count)
        {
            // 상한 검사는 앵커 경계에서 — 초과분은 최대 앵커 1개 분량이다(옵션 문서).
            if (_maxChecksPerPass > 0 && checksUsed >= _maxChecksPerPass)
            {
                truncated = true;
                break;
            }

            if (TryBuildMatch(anchor, now, ref checksUsed, out MatchProposal? match, out int removedBelowAnchor))
            {
                matches.Add(match!);
                matchedCount++;

                // 앵커 0 재시작 대신 제자리 재개(감사 2026-08-18 R-4 ②). 제거된 티켓 중 앵커보다
                // 앞에 있던 수만큼 당기면, 앵커 다음의 첫 미검사 티켓이 정확히 이 위치로 내려온다.
                // 앞선 앵커들은 후보가 줄기만 했으므로 다시 볼 필요가 없다.
                anchor -= removedBelowAnchor;
            }
            else
            {
                anchor++;
            }
        }

        _resumeAnchor = truncated ? anchor : 0;

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

    private bool TryBuildMatch(
        int anchorIndex,
        MonotonicTimestamp now,
        ref int checksUsed,
        out MatchProposal? match,
        out int removedBelowAnchor)
    {
        match = null;
        removedBelowAnchor = 0;

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

            checksUsed++;
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
                if (selectedIndex == anchorIndex)
                {
                    continue; // 앵커와의 호환은 후보 수집에서 이미 검사했다.
                }

                checksUsed++;
                if (!AreCompatible(candidate, _tickets[selectedIndex], now))
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

        match = BuildProposalAndRemoveSelected(anchorIndex, out removedBelowAnchor);
        return true;
    }

    /// <param name="anchorIndex">이번 매치의 앵커 인덱스(제거 전 기준).</param>
    /// <param name="removedBelowAnchor">
    /// 제거된 티켓 중 앵커보다 앞(인덱스가 작은 쪽)에 있던 수. 호출자가 스캔 위치를 이만큼
    /// 당기면 앵커 다음의 첫 미검사 티켓 위치가 된다(감사 2026-08-18 R-4 ②의 인덱스 보정).
    /// </param>
    private MatchProposal BuildProposalAndRemoveSelected(int anchorIndex, out int removedBelowAnchor)
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
        removedBelowAnchor = 0;
        for (int i = _selected.Count - 1; i >= 0; i--)
        {
            int index = _selected[i];
            if (index < anchorIndex)
            {
                removedBelowAnchor++;
            }

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
