using System;
using ChServerM.Diagnostics;

namespace ChServerM.RealTime;

/// <summary>
/// <see cref="TimerWheel"/>의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 휠의 해상도·범위·상한은 워크로드마다 최적값이 다르다. 레거시는
/// 5단 이질 휠(100ms×3000, 1분×1440, …)을 상수로 박았다 — 여기서는 "슬롯 길이 × 레벨당
/// 슬롯 수 × 레벨 수"라는 기하 구성으로 일반화하고 옵션으로 노출한다
/// (레거시 분석의 개선안 그대로, ADR-0062).
/// </para>
/// <para>
/// 기본값(100ms × 512 × 3레벨)의 커버 범위: 레벨 0 = 51.2초, 레벨 1 ≈ 7.3시간,
/// 레벨 2 ≈ 155일. 그 너머의 지연은 최상위 휠을 재순회하며 자기 교정된다(비용은 재순회당
/// 재배치 1회 — 문서화된 트레이드오프다).
/// </para>
/// </remarks>
public sealed class TimerWheelOptions
{
    /// <summary>기본 슬롯 길이. 100ms — 타이머 발화 해상도이기도 하다.</summary>
    public static readonly TimeSpan DefaultTickDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>기본 레벨당 슬롯 수. 512.</summary>
    public const int DefaultSlotsPerLevel = 512;

    /// <summary>기본 레벨 수. 3.</summary>
    public const int DefaultLevelCount = 3;

    /// <summary>기본 살아 있는 타이머 상한. 2²⁰ = 1,048,576.</summary>
    public const int DefaultMaxPendingTimers = 1 << 20;

    /// <summary>기본 노드 풀 상한. 8,192.</summary>
    public const int DefaultNodePoolCapacity = 8_192;

    /// <summary>최하위 레벨의 슬롯 길이 = 타이머 발화 해상도. 만료는 최대 이 시간만큼 늦게 관측된다.</summary>
    public TimeSpan TickDuration { get; set; } = DefaultTickDuration;

    /// <summary>레벨당 슬롯 수. 2의 거듭제곱이어야 한다(슬롯 인덱스를 나눗셈 없이 마스크로 구한다).</summary>
    public int SlotsPerLevel { get; set; } = DefaultSlotsPerLevel;

    /// <summary>레벨 수. 레벨 k 의 슬롯 길이는 <c>TickDuration × SlotsPerLevel^k</c> 다.</summary>
    public int LevelCount { get; set; } = DefaultLevelCount;

    /// <summary>살아 있는(발화·취소 전) 타이머의 상한. 넘으면 예약이 거부된다.</summary>
    /// <remarks>거부가 붕괴보다 낫다(CLAUDE.md 9.6). 거부는 상태·메트릭·로그로 관측된다.</remarks>
    public int MaxPendingTimers { get; set; } = DefaultMaxPendingTimers;

    /// <summary>재사용 노드 풀의 상한. 초과분은 GC 에 맡긴다.</summary>
    /// <remarks>
    /// 상한이 없으면 한 번 폭주한 풀이 노드를 영원히 붙든다 — 레거시는 슬롯마다 무제한
    /// <c>ObjectPoolM&lt;Node&gt;</c>를 두어 3,000개 슬롯이 각자 최대 수위를 유지했다.
    /// </remarks>
    public int NodePoolCapacity { get; set; } = DefaultNodePoolCapacity;

    /// <summary>시간 원본. 테스트에서 대체할 수 있다.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>진단 로거. 기본은 무출력.</summary>
    public IServerLogger Logger { get; set; } = NullServerLogger.Instance;

    /// <summary>메트릭 싱크(Phase 11). <see langword="null"/>이면 기록하지 않는다.</summary>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>예약 거부 경고 로그의 최소 간격. 기본 5초.</summary>
    /// <remarks>포화 상태에서는 거부가 초당 수만 건이다 — 로그는 표본만, 집계는 메트릭이 한다.</remarks>
    public TimeSpan RejectionLogInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (TickDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(TickDuration)}은 0보다 커야 한다. 현재 값: {TickDuration}");
        }

        if (SlotsPerLevel < 2 || (SlotsPerLevel & (SlotsPerLevel - 1)) != 0)
        {
            throw new InvalidOperationException(
                $"{nameof(SlotsPerLevel)}은 2 이상의 2의 거듭제곱이어야 한다. 현재 값: {SlotsPerLevel}");
        }

        if (LevelCount is < 1 or > 8)
        {
            throw new InvalidOperationException(
                $"{nameof(LevelCount)}는 1~8 이어야 한다. 현재 값: {LevelCount}");
        }

        if (MaxPendingTimers < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxPendingTimers)}는 1 이상이어야 한다. 현재 값: {MaxPendingTimers}");
        }

        if (NodePoolCapacity < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(NodePoolCapacity)}는 음수일 수 없다. 풀 비활성은 0이다. 현재 값: {NodePoolCapacity}");
        }

        if (RejectionLogInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(RejectionLogInterval)}은 0보다 커야 한다. 현재 값: {RejectionLogInterval}");
        }

        ArgumentNullException.ThrowIfNull(TimeProvider, nameof(TimeProvider));
        ArgumentNullException.ThrowIfNull(Logger, nameof(Logger));
    }

    /// <summary>현재 값을 복사한 스냅샷을 만든다.</summary>
    internal TimerWheelOptions Snapshot() => new()
    {
        TickDuration = TickDuration,
        SlotsPerLevel = SlotsPerLevel,
        LevelCount = LevelCount,
        MaxPendingTimers = MaxPendingTimers,
        NodePoolCapacity = NodePoolCapacity,
        TimeProvider = TimeProvider,
        Logger = Logger,
        MetricsSink = MetricsSink,
        RejectionLogInterval = RejectionLogInterval,
    };
}
