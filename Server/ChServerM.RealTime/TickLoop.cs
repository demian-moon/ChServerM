using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Time;

namespace ChServerM.RealTime;

/// <summary>
/// 고정 타임스텝 틱 루프. 전용 스레드 하나가 일정한 간격으로 <see cref="ITickHandler"/>를 호출한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 실시간 워크로드(시뮬레이션·룸 갱신·주기 브로드캐스트)는 "일정한 간격으로,
/// 밀리면 관측되게" 실행되는 루프가 필요하다. <c>Task.Delay</c> 루프는 간격 오차가
/// <b>누적</b>되고(상대 스케줄), <c>System.Threading.Timer</c> 는 콜백 겹침·스레드풀 경유로
/// 순서 계약이 없다. 이 루프는 둘 다 계약으로 해결한다.
/// </para>
/// <para>
/// <b>드리프트 보정 — 절대 스케줄.</b> n번째 틱의 마감은 <c>원점 + n × 간격</c>이다.
/// "직전 틱 + 간격"(상대 스케줄)이 아니다 — 상대 스케줄은 틱마다 생긴 오차가 전부 누적되어
/// 한 시간이면 초 단위로 밀린다. 절대 스케줄에서는 개별 틱이 늦어도 다음 마감이 당겨져
/// 평균 주기가 정확히 유지된다.
/// </para>
/// <para>
/// <b>밀렸을 때의 계약.</b> 밀린 틱은 <see cref="TickLoopOptions.MaxCatchUpTicks"/>개까지
/// 연달아 실행(캐치업)하고, 그 너머는 <b>건너뛴다</b>. 무제한 캐치업은 죽음의 나선이다 —
/// 거부가 붕괴보다 낫다(CLAUDE.md 9.6). 건너뜀은 <see cref="TickLoopStatistics.SkippedTicks"/>와
/// 메트릭·로그로 반드시 관측된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Start"/>·<see cref="DisposeAsync"/>는 아무 스레드에서나
/// 호출해도 되고, 핸들러는 항상 전용 스레드에서 겹침 없이 실행된다. 통계 필드는
/// 루프 스레드만 쓰고(단일 작성자) 독자는 <see cref="Volatile"/>로 읽는다(CLAUDE.md 9.3).
/// </para>
/// <para>
/// <b>막는 레거시 결함.</b> 커넥션·객체마다 타이머/스레드를 만들던 구조
/// (<c>TimerM</c>, 9.5 위반)를 "루프 하나 + 스레드 하나"로 대체하고, 루프 스레드의 예외 하나가
/// 루프를 영구 정지시키던 패턴(<c>ExecutableTaskDispatcherM</c>, 9.2)을 틱 단위 격리로 막는다.
/// </para>
/// </remarks>
public sealed class TickLoop : IAsyncDisposable
{
    private const int StateCreated = 0;
    private const int StateRunning = 1;
    private const int StateDisposed = 2;

    // 슬립 한 번의 상한. 정지 요청이 이 시간 안에 감지되도록 슬립을 조각낸다.
    private static readonly TimeSpan MaxSleepSlice = TimeSpan.FromMilliseconds(50);

    private readonly TickLoopOptions _options;
    private readonly ITickHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;
    private readonly IMetricsSink? _metrics;
    private readonly long _intervalRaw;
    private readonly long _spinWindowRaw;
    private readonly IntervalGate _overrunLogGate;

    // 루프 스레드가 종료를 알리는 신호. RunContinuationsAsynchronously 가 없으면
    // DisposeAsync 의 연속이 루프 스레드에서 인라인 실행된다.
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private volatile bool _stopRequested;
    private int _state;

    // 통계 — 루프 스레드만 쓴다(단일 작성자). 독자는 Volatile.Read.
    private long _totalTicks;
    private long _overrunTicks;
    private long _skippedTicks;
    private long _faultedTicks;
    private long _lastDurationRaw;
    private long _maxDurationRaw;
    private long _maxStartDriftRaw;

    /// <summary>틱 루프를 만든다. <see cref="Start"/> 전에는 스레드가 없다.</summary>
    /// <param name="handler">틱마다 호출할 작업.</param>
    /// <param name="options">설정. 생성 시점에 검증·스냅샷된다.</param>
    /// <exception cref="InvalidOperationException">옵션이 유효하지 않을 때.</exception>
    public TickLoop(ITickHandler handler, TickLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _handler = handler;
        _options = options.Snapshot();
        _timeProvider = _options.TimeProvider;
        _logger = _options.Logger;
        _metrics = _options.MetricsSink;

        long frequency = _timeProvider.TimestampFrequency;
        MicrosecondArithmetic.ValidateFrequency(frequency);
        _intervalRaw = MicrosecondArithmetic.ToRawTicks(_options.TickInterval, frequency);
        if (_intervalRaw < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(TickLoopOptions.TickInterval)}({_options.TickInterval})이 시간 원본 해상도보다 짧다.");
        }

        _spinWindowRaw = MicrosecondArithmetic.ToRawTicks(_options.SpinWaitWindow, frequency);
        _overrunLogGate = new IntervalGate(_options.OverrunLogInterval, _timeProvider);
    }

    /// <summary>루프가 시작되어 아직 정지하지 않았는지 여부.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == StateRunning && !_stopped.Task.IsCompleted;

    /// <summary>현재 통계의 스냅샷.</summary>
    public TickLoopStatistics Statistics =>
        new(
            Volatile.Read(ref _totalTicks),
            Volatile.Read(ref _overrunTicks),
            Volatile.Read(ref _skippedTicks),
            Volatile.Read(ref _faultedTicks),
            RawToTimeSpan(Volatile.Read(ref _lastDurationRaw)),
            RawToTimeSpan(Volatile.Read(ref _maxDurationRaw)),
            RawToTimeSpan(Volatile.Read(ref _maxStartDriftRaw)));

    /// <summary>전용 스레드를 만들고 루프를 시작한다. 한 번만 부를 수 있다.</summary>
    /// <exception cref="InvalidOperationException">이미 시작했거나 폐기됐을 때.</exception>
    public void Start()
    {
        int previous = Interlocked.CompareExchange(ref _state, StateRunning, StateCreated);
        if (previous != StateCreated)
        {
            throw new InvalidOperationException(
                previous == StateRunning ? "이미 시작된 틱 루프다." : "폐기된 틱 루프다.");
        }

        var thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = _options.ThreadName,
        };
        _thread = thread;
        thread.Start();
    }

    /// <summary>정지를 요청하고 루프 스레드가 끝날 때까지 기다린다. 여러 번 불러도 된다.</summary>
    /// <remarks>
    /// 실행 중인 틱은 끝까지 완주한다 — 틱 도중 강제 중단은 시뮬레이션 상태를 반쯤 갱신된
    /// 채로 남긴다. 대기 상한은 슬립 조각(50ms) + 마지막 틱의 실행 시간이다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        int previous = Interlocked.Exchange(ref _state, StateDisposed);
        _stopRequested = true;

        if (previous == StateRunning)
        {
            await _stopped.Task.ConfigureAwait(false);
        }
        else
        {
            // 시작한 적이 없으면 기다릴 스레드도 없다.
            _stopped.TrySetResult();
        }
    }

    private void RunLoop()
    {
        try
        {
            long originRaw = _timeProvider.GetTimestamp();
            long tickIndex = 0;

            while (!_stopRequested)
            {
                // 절대 스케줄: 마감은 원점 기준이다. 직전 틱이 늦어도 오차가 누적되지 않는다.
                long deadlineRaw = originRaw + (tickIndex * _intervalRaw);
                WaitUntil(deadlineRaw);
                if (_stopRequested)
                {
                    break;
                }

                RunOneTick(tickIndex, deadlineRaw);
                tickIndex++;

                long behindTicks = (_timeProvider.GetTimestamp() - (originRaw + (tickIndex * _intervalRaw))) / _intervalRaw + 1;
                if (behindTicks > _options.MaxCatchUpTicks)
                {
                    // 캐치업 상한 초과분은 실행하지 않고 순번만 넘긴다. 다음 틱의 ScheduledAt 이
                    // 현재 시각 근처로 오면서 루프가 실시간에 재정렬된다.
                    long skip = behindTicks - _options.MaxCatchUpTicks;
                    tickIndex += skip;
                    Volatile.Write(ref _skippedTicks, _skippedTicks + skip);
                    _metrics?.Count(RealTimeMetricNames.TickSkipped, skip, default);

                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Log(
                            LogLevel.Warning,
                            RealTimeEvents.TicksSkipped,
                            (Skipped: skip, Interval: _options.TickInterval),
                            null,
                            static (state, _) =>
                                $"틱 {state.Skipped}개를 건너뛰었다(간격 {state.Interval.TotalMilliseconds:F1}ms). " +
                                "핸들러가 예산을 계속 넘고 있다는 뜻이다.");
                    }
                }
            }
        }
#pragma warning disable CA1031 // 루프 스레드의 미처리 예외는 프로세스를 죽인다 — 마지막 방어선이다.
        catch (Exception exception)
        {
            // 여기 도달하면 핸들러 예외(틱 단위 격리)가 아니라 루프 자체의 결함이다.
            if (_logger.IsEnabled(LogLevel.Critical))
            {
                _logger.Log(
                    LogLevel.Critical,
                    RealTimeEvents.TickLoopCrashed,
                    _options.ThreadName,
                    exception,
                    static (name, ex) => $"틱 루프 '{name}'가 중단됐다: {ex?.Message}");
            }
        }
        finally
        {
            // 어떤 경로로 끝나든 대기자를 깨운다 — 이걸 빠뜨리면 DisposeAsync 가 영원히 멈춘다(9.2).
            _stopped.TrySetResult();
        }
#pragma warning restore CA1031
    }

    private void WaitUntil(long deadlineRaw)
    {
        while (!_stopRequested)
        {
            long nowRaw = _timeProvider.GetTimestamp();
            long remainingRaw = deadlineRaw - nowRaw;
            if (remainingRaw <= 0)
            {
                return;
            }

            if (remainingRaw > _spinWindowRaw)
            {
                TimeSpan sleep = _timeProvider.GetElapsedTime(nowRaw, deadlineRaw - _spinWindowRaw);
                if (sleep > MaxSleepSlice)
                {
                    sleep = MaxSleepSlice;
                }

                if (sleep >= TimeSpan.FromMilliseconds(1))
                {
                    Thread.Sleep(sleep);
                }
                else
                {
                    // 1ms 미만은 Sleep 해상도 밖이다. 양보만 하고 재검사한다.
                    Thread.Sleep(0);
                }
            }
            else
            {
                // 마감 직전 스핀 구간: Sleep(1) 승격을 막아야 밀리초 미만 지터가 유지된다.
                var spinner = new SpinWait();
                while (!_stopRequested && _timeProvider.GetTimestamp() < deadlineRaw)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                }

                return;
            }
        }
    }

    private void RunOneTick(long tickIndex, long deadlineRaw)
    {
        long startRaw = _timeProvider.GetTimestamp();
        long driftRaw = startRaw - deadlineRaw;
        if (driftRaw > _maxStartDriftRaw)
        {
            Volatile.Write(ref _maxStartDriftRaw, driftRaw);
        }

        var context = new TickContext(
            tickIndex,
            MonotonicTimestamp.FromRaw(deadlineRaw),
            MonotonicTimestamp.FromRaw(startRaw),
            _options.TickInterval,
            _timeProvider.GetElapsedTime(deadlineRaw, startRaw));

        try
        {
            _handler.OnTick(in context);
        }
#pragma warning disable CA1031 // 핸들러는 애플리케이션 코드다. 무엇을 던지든 루프를 죽이지 않는다.
        catch (Exception exception)
        {
            // 틱 단위 격리(9.2): 나쁜 틱 하나가 루프를 죽이지 않는다.
            Volatile.Write(ref _faultedTicks, _faultedTicks + 1);
            _metrics?.Count(RealTimeMetricNames.TickFaults, 1, default);

            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.Log(
                    LogLevel.Error,
                    RealTimeEvents.TickFaulted,
                    tickIndex,
                    exception,
                    static (tick, ex) => $"틱 {tick} 핸들러가 예외로 끝났다: {ex?.Message}");
            }
        }
#pragma warning restore CA1031

        long durationRaw = _timeProvider.GetTimestamp() - startRaw;
        Volatile.Write(ref _lastDurationRaw, durationRaw);
        if (durationRaw > _maxDurationRaw)
        {
            Volatile.Write(ref _maxDurationRaw, durationRaw);
        }

        Volatile.Write(ref _totalTicks, _totalTicks + 1);
        _metrics?.Record(
            RealTimeMetricNames.TickDuration,
            _timeProvider.GetElapsedTime(startRaw, startRaw + durationRaw).TotalSeconds,
            default);

        if (durationRaw > _intervalRaw)
        {
            // 틱 예산 초과: 집계는 메트릭이, 로그는 간격 게이트로 표본만.
            Volatile.Write(ref _overrunTicks, _overrunTicks + 1);
            _metrics?.Count(RealTimeMetricNames.TickOverruns, 1, default);

            if (_overrunLogGate.TryConsume() && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Log(
                    LogLevel.Warning,
                    RealTimeEvents.TickOverrun,
                    (Tick: tickIndex,
                     Duration: _timeProvider.GetElapsedTime(startRaw, startRaw + durationRaw),
                     Budget: _options.TickInterval),
                    null,
                    static (state, _) =>
                        $"틱 {state.Tick}이 예산을 넘었다: {state.Duration.TotalMilliseconds:F2}ms / " +
                        $"예산 {state.Budget.TotalMilliseconds:F1}ms.");
            }
        }
    }

    private TimeSpan RawToTimeSpan(long raw) => _timeProvider.GetElapsedTime(0, raw);
}
