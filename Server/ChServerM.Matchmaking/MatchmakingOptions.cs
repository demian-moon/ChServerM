using System;

namespace ChServerM.Matchmaking;

/// <summary>
/// 매치메이킹 큐 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>대기 시간 vs 매치 품질 트레이드오프가 이 타입의 전부다</b>(ADR-0068 결정 3).
/// 티켓의 허용 레이팅 창은 <c>min(InitialRatingWindow + RatingWindowGrowthPerSecond × 대기초,
/// MaxRatingWindow)</c> 로 자라고, 두 티켓은 <b>양쪽 창이 서로를 덮을 때만</b> 호환이다 —
/// 오래 기다린 쪽의 관대함이 방금 온 참가자를 나쁜 매치로 끌어들이지 않게 한다.
/// </para>
/// <para>
/// 기본값은 관례적 레이팅 척도(Elo 류, 표준편차 수백)를 가정한 출발점이지 권장값이 아니다 —
/// 자기 레이팅 분포에 맞게 명시하는 것이 맞다.
/// </para>
/// </remarks>
public sealed class MatchmakingOptions
{
    /// <summary>팀당 인원 기본값.</summary>
    public const int DefaultTeamSize = 1;

    /// <summary>팀 수 기본값.</summary>
    public const int DefaultTeamCount = 2;

    /// <summary>초기 레이팅 창 기본값.</summary>
    public const double DefaultInitialRatingWindow = 100;

    /// <summary>초당 창 확장 기본값.</summary>
    public const double DefaultRatingWindowGrowthPerSecond = 50;

    /// <summary>창 상한 기본값.</summary>
    public const double DefaultMaxRatingWindow = 1000;

    /// <summary>큐 깊이 상한 기본값.</summary>
    public const int DefaultMaxQueueDepth = 4096;

    /// <summary>팀당 인원. 파티는 이 값을 넘을 수 없다.</summary>
    public int TeamSize { get; set; } = DefaultTeamSize;

    /// <summary>매치를 이루는 팀 수.</summary>
    public int TeamCount { get; set; } = DefaultTeamCount;

    /// <summary>등록 직후의 허용 레이팅 창(± 값).</summary>
    public double InitialRatingWindow { get; set; } = DefaultInitialRatingWindow;

    /// <summary>대기 1초당 창이 넓어지는 양. 0 이면 창이 자라지 않는다.</summary>
    public double RatingWindowGrowthPerSecond { get; set; } = DefaultRatingWindowGrowthPerSecond;

    /// <summary>창의 상한 — 아무리 기다려도 이 이상 관대해지지 않는다.</summary>
    public double MaxRatingWindow { get; set; } = DefaultMaxRatingWindow;

    /// <summary>
    /// 최대 대기 시간. 넘긴 티켓은 매치가 아니라 <b>만료로 드러난다</b> —
    /// 조용한 억지 매치는 조용한 유실과 같은 부류다(ADR-0068 결정 3).
    /// <see langword="null"/> 이면 만료 없음.
    /// </summary>
    public TimeSpan? MaxWaitTime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>큐 깊이 상한. 초과 등록은 <see cref="MatchEnqueueStatus.QueueFull"/> 로 거부된다(9.6).</summary>
    public int MaxQueueDepth { get; set; } = DefaultMaxQueueDepth;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 성립하지 않는다.</exception>
    public void Validate()
    {
        if (TeamSize < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(TeamSize)} 는 1 이상이어야 한다. 현재 값: {TeamSize}.");
        }

        if (TeamCount < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(TeamCount)} 는 1 이상이어야 한다. 현재 값: {TeamCount}. "
                + "1 이면 협동(팀 하나 채우기), 2 이상이면 대전 매칭이다.");
        }

        if (double.IsNaN(InitialRatingWindow) || InitialRatingWindow < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(InitialRatingWindow)} 는 0 이상이어야 한다. 현재 값: {InitialRatingWindow}.");
        }

        if (double.IsNaN(RatingWindowGrowthPerSecond) || RatingWindowGrowthPerSecond < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(RatingWindowGrowthPerSecond)} 는 0 이상이어야 한다. 현재 값: {RatingWindowGrowthPerSecond}. "
                + "0 이면 창이 자라지 않아 초기 창 밖 상대와는 영원히 매치되지 않는다 — 의도한 경우에만 쓴다.");
        }

        if (double.IsNaN(MaxRatingWindow) || MaxRatingWindow < InitialRatingWindow)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxRatingWindow)}({MaxRatingWindow}) 는 {nameof(InitialRatingWindow)}"
                + $"({InitialRatingWindow}) 이상이어야 한다 — 상한이 초기값보다 작으면 창이 시작부터 모순이다.");
        }

        if (MaxWaitTime is { } wait && wait <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxWaitTime)}({MaxWaitTime}) 는 양수여야 한다. 만료를 끄려면 null 을 쓴다.");
        }

        if (MaxQueueDepth < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxQueueDepth)} 는 1 이상이어야 한다. 현재 값: {MaxQueueDepth}. "
                + "무제한 큐는 만들 수 없다 — 거부가 붕괴보다 낫다(9.6).");
        }
    }
}
