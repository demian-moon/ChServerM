using System;
using ChServerM.Diagnostics;

namespace ChServerM.RealTime;

/// <summary>
/// <see cref="TickLoop"/>의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 틱 간격·캐치업 상한·스핀 구간은 전부 <b>지연 대 CPU 트레이드오프</b>다.
/// 워크로드마다 최적값이 달라 상수로 박을 수 없다 — 16.6ms(60Hz 시뮬레이션)와
/// 1s(느린 하우스키핑)는 같은 루프의 다른 설정일 뿐이다.
/// </para>
/// <para>
/// 값은 <see cref="TickLoop"/> 생성 시점에 스냅샷된다. 이후 변경은 무시된다
/// (<c>TcpTransportOptions</c> 와 같은 규약 — 검증 통과 후 조합이 바뀌는 구멍을 막는다).
/// </para>
/// </remarks>
public sealed class TickLoopOptions
{
    /// <summary>기본 틱 간격. 50ms(20Hz).</summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>기본 캐치업 상한. 4틱.</summary>
    public const int DefaultMaxCatchUpTicks = 4;

    /// <summary>기본 스핀 구간. <see cref="TimeSpan.Zero"/>(순수 슬립).</summary>
    /// <remarks>
    /// <b>기본값 변경(감사 2026-08-18 R-9).</b> 이전 기본 1ms 는 자체 실측(BENCHMARKS.md 틱 지터
    /// 절)이 "효과 없음"으로 못박은 구성이었다 — OS 슬립 해상도(≈15.6ms)보다 짧은 스핀 구간은
    /// 슬립의 초과 수면이 통째로 건너뛴다. 효과 없는 스핀을 기본으로 켜 두는 것은 "지터가
    /// 억제되고 있다"는 거짓 신호이므로, 기본을 정직한 순수 슬립으로 내렸다.
    /// 밀리초 미만 지터가 필요하면 <see cref="SpinWaitWindow"/> 문서의 조건에 맞게 명시 설정한다.
    /// </remarks>
    public static readonly TimeSpan DefaultSpinWaitWindow = TimeSpan.Zero;

    /// <summary>고정 타임스텝 간격. 틱 하나의 실행 예산이기도 하다.</summary>
    public TimeSpan TickInterval { get; set; } = DefaultTickInterval;

    /// <summary>밀린 틱을 연달아 따라잡는 최대 개수. 그 너머는 <b>건너뛴다</b>.</summary>
    /// <remarks>
    /// <para>
    /// 상한이 없으면 한 번 밀린 루프가 밀린 틱을 전부 실행하느라 더 밀리는
    /// <b>죽음의 나선(spiral of death)</b>에 빠진다. 무제한 큐 금지(CLAUDE.md 9.6)와 같은
    /// 원리다 — <b>건너뜀(거부)이 붕괴보다 낫다.</b> 건너뛴 수는
    /// <see cref="TickLoopStatistics.SkippedTicks"/>와 메트릭으로 관측된다.
    /// </para>
    /// <para><c>0</c>이면 캐치업하지 않는다 — 밀린 틱은 전부 건너뛰고 다음 예정 시각부터 재개한다.</para>
    /// </remarks>
    public int MaxCatchUpTicks { get; set; } = DefaultMaxCatchUpTicks;

    /// <summary>마감 직전 이 구간만큼은 슬립 대신 스핀 대기한다.</summary>
    /// <remarks>
    /// <para>
    /// <c>Thread.Sleep</c> 해상도는 OS 기본 타이머(Windows 15.6ms)에 묶인다. 마감 직전
    /// 이 구간을 스핀(<see cref="System.Threading.SpinWait"/>, yield 포함)으로 채우면 지터가
    /// 밀리초 미만으로 내려간다 — 대신 틱당 최대 이 시간만큼 코어를 태운다.
    /// </para>
    /// <para>
    /// <b>⚠ 실측이 밝힌 함정(BENCHMARKS.md 틱 지터 절, ENV-B):</b> 구간이 OS 슬립 해상도보다
    /// 작으면 <b>슬립의 초과 수면이 스핀 구간을 통째로 건너뛰어 효과가 없다</b>(50ms 틱 +
    /// 1ms 스핀에서 p99 13.8ms — 순수 슬립과 사실상 같다). 스핀이 효과를 내는 조건은 둘뿐이다:
    /// ① 간격 ≤ 스핀 구간(슬립 없이 전 구간 스핀 — 1ms 틱에서 p99 0µs),
    /// ② 스핀 구간 &gt; OS 해상도(슬립이 구간 안쪽에 착지 — 16ms 스핀에서 p99 밀리초 미만).
    /// 밀리초 미만 지터가 필요하면 틱 간격과 OS 해상도(Windows 15.6ms)의 관계에 맞게
    /// <b>명시적으로</b> 설정한다 — 16ms 이상(50ms 틱 기준 CPU 상한 32%), 또는 간격 전체.
    /// 그 사이의 값은 CPU 만 태우고 지터를 줄이지 못하며, <see cref="Validate"/>가
    /// <see cref="Logger"/>로 경고를 남긴다(예외는 아니다 — 동작은 하되 효과가 없을 뿐이다).
    /// </para>
    /// <para>
    /// 기본값은 <see cref="TimeSpan.Zero"/>(순수 슬립)다 — 지터가 OS 해상도만큼 커지는 대신
    /// CPU 를 태우지 않고, "스핀이 켜져 있으니 지터가 억제된다"는 거짓 신호가 없다
    /// (<see cref="DefaultSpinWaitWindow"/>의 변경 이유 참조, 감사 2026-08-18 R-9).
    /// </para>
    /// </remarks>
    public TimeSpan SpinWaitWindow { get; set; } = DefaultSpinWaitWindow;

    /// <summary>루프 전용 스레드 이름. 덤프·프로파일에서 루프를 찾는 열쇠다.</summary>
    public string ThreadName { get; set; } = "chserverm-tick";

    /// <summary>시간 원본. 테스트에서 대체할 수 있다.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>진단 로거. 기본은 무출력.</summary>
    public IServerLogger Logger { get; set; } = NullServerLogger.Instance;

    /// <summary>메트릭 싱크(Phase 11). <see langword="null"/>이면 기록하지 않는다.</summary>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>예산 초과 경고 로그의 최소 간격. 기본 5초.</summary>
    /// <remarks>
    /// 과부하 상태에서는 <b>모든 틱이</b> 예산을 넘는다 — 틱마다 경고를 찍으면 로그가
    /// 과부하를 가속한다. 집계는 메트릭이 하고, 로그는 이 간격으로 표본만 남긴다.
    /// </remarks>
    public TimeSpan OverrunLogInterval { get; set; } = TimeSpan.FromSeconds(5);

    // Windows 기본 타이머 해상도. 이보다 짧은 스핀 구간은 슬립의 초과 수면에 먹힌다
    // (BENCHMARKS.md 틱 지터 절 실측 — 감사 2026-08-18 R-9).
    private static readonly TimeSpan OsSleepResolution = TimeSpan.FromMilliseconds(15.6);

    /// <summary>설정을 검증한다.</summary>
    /// <remarks>
    /// "0 &lt; 스핀 구간 &lt; OS 슬립 해상도(15.6ms)이면서 틱 간격 &gt; 스핀 구간"인 조합은
    /// 실측상 스핀 효과가 없는 함정이다(<see cref="SpinWaitWindow"/> 문서). 유효하긴 하므로
    /// 예외 대신 <see cref="Logger"/>에 경고를 남긴다 — 기본 로거(무출력)면 이 문서가 유일한
    /// 경고다.
    /// </remarks>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (TickInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(TickInterval)}은 0보다 커야 한다. 현재 값: {TickInterval}");
        }

        if (MaxCatchUpTicks < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxCatchUpTicks)}는 음수일 수 없다. 캐치업 비활성은 0이다. 현재 값: {MaxCatchUpTicks}");
        }

        if (SpinWaitWindow < TimeSpan.Zero || SpinWaitWindow > TickInterval)
        {
            throw new InvalidOperationException(
                $"{nameof(SpinWaitWindow)}({SpinWaitWindow})는 0 이상 {nameof(TickInterval)}({TickInterval}) 이하여야 한다. " +
                "간격보다 긴 스핀은 슬립 없이 코어를 통째로 태운다.");
        }

        if (string.IsNullOrWhiteSpace(ThreadName))
        {
            throw new InvalidOperationException($"{nameof(ThreadName)}은 비울 수 없다.");
        }

        if (OverrunLogInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(OverrunLogInterval)}은 0보다 커야 한다. 현재 값: {OverrunLogInterval}");
        }

        ArgumentNullException.ThrowIfNull(TimeProvider, nameof(TimeProvider));
        ArgumentNullException.ThrowIfNull(Logger, nameof(Logger));

        // 함정 조합 경고(감사 2026-08-18 R-9): 스핀이 켜져 있지만 실측상 효과가 없는 구성.
        // 던지지 않는 이유 — 동작 자체는 올바르고, 손해는 "기대한 지터 억제가 없다"뿐이다.
        if (SpinWaitWindow > TimeSpan.Zero
            && SpinWaitWindow < OsSleepResolution
            && TickInterval > SpinWaitWindow
            && Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.Log(
                LogLevel.Warning,
                RealTimeEvents.SpinWindowIneffective,
                (Spin: SpinWaitWindow, Interval: TickInterval),
                null,
                static (state, _) =>
                    $"SpinWaitWindow({state.Spin.TotalMilliseconds:F1}ms)가 OS 슬립 해상도(15.6ms)보다 짧고 " +
                    $"TickInterval({state.Interval.TotalMilliseconds:F1}ms)보다도 짧다 — 실측상 스핀 효과가 없는 " +
                    "조합이다(BENCHMARKS.md 틱 지터 절). 밀리초 미만 지터가 필요하면 16ms 이상 또는 간격 전체를, " +
                    "아니면 0(순수 슬립)을 준다.");
        }
    }

    /// <summary>현재 값을 복사한 스냅샷을 만든다.</summary>
    internal TickLoopOptions Snapshot() => new()
    {
        TickInterval = TickInterval,
        MaxCatchUpTicks = MaxCatchUpTicks,
        SpinWaitWindow = SpinWaitWindow,
        ThreadName = ThreadName,
        TimeProvider = TimeProvider,
        Logger = Logger,
        MetricsSink = MetricsSink,
        OverrunLogInterval = OverrunLogInterval,
    };
}
