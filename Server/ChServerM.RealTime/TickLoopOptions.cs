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

    /// <summary>기본 스핀 구간. 1ms.</summary>
    public static readonly TimeSpan DefaultSpinWaitWindow = TimeSpan.FromMilliseconds(1);

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
    /// 밀리초 미만 지터가 필요하면 <b>16ms 이상</b>을 준다(50ms 틱 기준 CPU 상한 32%).
    /// </para>
    /// <para><see cref="TimeSpan.Zero"/>면 순수 슬립이다. 지터가 OS 해상도만큼 커진다.</para>
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

    /// <summary>설정을 검증한다.</summary>
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
