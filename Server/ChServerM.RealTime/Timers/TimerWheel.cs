using System;
using System.Threading;
using ChServerM.Diagnostics;

namespace ChServerM.RealTime;

/// <summary>
/// 계층적 타이밍 휠. 만료 타이머 수만 개를 삽입 O(1)·진행 O(1)로 관리한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 버프·쿨다운·세션 타임아웃처럼 만료 타이머가 대량인 워크로드에서
/// 우선순위 큐는 삽입·추출 O(log n)이고, <c>System.Threading.Timer</c>를 개체마다 만들면
/// 타이머 수만큼 스레드풀 작업이 생긴다(CLAUDE.md 9.5 위반 — 레거시 <c>TimerM</c>이 그랬다).
/// 계층적 타이밍 휠은 레거시 전체에서 가장 정교한 자산(<c>TimeEventSchedulerM</c>,
/// Kafka TimingWheel·Netty HashedWheelTimer 계열)이며 <b>설계를 승계</b>한다(ADR-0062).
/// </para>
/// <para>
/// <b>레거시와 다른 점(= 막는 재발).</b>
/// </para>
/// <list type="number">
///   <item><description><b>휠 원점을 생성 시각으로 초기화</b> — 원점 0 시작은 첫 진행에서 슬롯
///   4,650회 빈 순회를 만들었다.</description></item>
///   <item><description><b>만료와 취소가 다른 콜백</b>(<see cref="ITimerJob"/>) — 원본은 발화도
///   <c>Cancel()</c>이라 구별이 불가능했다.</description></item>
///   <item><description><b>ID 가 문자열이 아니라 노드+세대 핸들</b> — 문자열 해싱 제거,
///   재사용 노드의 오취소(ABA)는 상태·세대를 한 <c>long</c>에 패킹한 CAS 로 차단한다.</description></item>
///   <item><description><b>시간이 <c>Frequency/1000</c> 정수 나눗셈이 아니라 정확 변환</b>
///   (<see cref="MicrosecondArithmetic"/>) — 원본은 30일 타이머에서 6.5분 오차.</description></item>
///   <item><description><b>노드 풀·살아 있는 타이머 수에 상한</b> — 무제한 풀·큐 금지(9.6),
///   초과는 거부로 관측된다.</description></item>
///   <item><description><b><c>Volatile</c> 일관 적용, 스핀은 CAS 재시도 시에만</b>(9.3) — 원본은
///   <c>IsEmpty</c>만 평문 읽기, 첫 시도 전 무조건 스핀.</description></item>
/// </list>
/// <para>
/// <b>스레드 규약.</b> <see cref="TrySchedule"/>·<see cref="TimerHandle.TryCancel"/>은 아무
/// 스레드에서나 안전하다. <see cref="Advance"/>·<see cref="Shutdown"/>은 <b>단일 드라이버
/// 전용</b>이다 — 동시에 부르면 안 된다. 드라이버는 대개 <see cref="TickLoop"/> 핸들러다.
/// 이 분리가 슬롯 자료구조에서 락과 <c>Concurrent*</c>를 없앤다(9.1 — 공유하지 않는 것이 1순위).
/// </para>
/// <para>
/// <b>해상도 규약.</b> 만료는 최대 <see cref="TimerWheelOptions.TickDuration"/> + 드라이버 호출
/// 간격만큼 늦게 관측된다. 일찍 발화하는 일은 없다.
/// </para>
/// </remarks>
public sealed class TimerWheel
{
    private const int StateFree = 0;
    private const int StatePending = 1;
    private const int StateCanceled = 2;
    private const int StateFired = 3;

    /// <summary>휠에 등록된 타이머 하나. 풀로 재사용된다.</summary>
    internal sealed class TimerNode
    {
        /// <summary>상위 32비트 세대 | 하위 32비트 상태. <see cref="Interlocked"/> 전용.</summary>
        /// <remarks>
        /// 세대와 상태를 한 워드에 패킹하는 이유: 취소(임의 스레드)와 발화(드라이버)와
        /// 재사용(풀)이 경합할 때, "이 세대의 Pending 을 이 세대의 Canceled 로"라는 전이를
        /// CAS 한 번으로 원자화해야 재사용된 노드를 오취소하는 ABA 가 구조적으로 불가능해진다.
        /// </remarks>
        internal long StateAndGeneration;

        internal ITimerJob? Job;
        internal long DeadlineRaw;
        internal TimerNode? SlotNext;   // 슬롯 연결 리스트. 드라이버 전용.
        internal TimerNode? StackNext;  // 유입/풀 Treiber 스택 링크.
    }

    private readonly TimerWheelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;
    private readonly IMetricsSink? _metrics;
    private readonly IntervalGate _rejectionLogGate;

    private readonly TimerNode?[][] _slots;        // [레벨][슬롯]. 드라이버 전용.
    private readonly long[] _slotDurationRaw;      // 레벨별 슬롯 길이(raw).
    private readonly long[] _currentSlotTick;      // 레벨별 진행 위치(원점 기준 절대 슬롯 번호). 드라이버 전용.
    private readonly int _slotMask;
    private readonly long _originRaw;              // 휠 원점 = 생성 시각 (레거시 결함 #1 수정).

    private TimerNode? _incomingHead;              // 예약 유입 Treiber 스택 (MPSC, 드라이버가 일괄 추출).
    private TimerNode? _poolHead;                  // 노드 풀 Treiber 스택.
    private int _poolCount;
    private int _shutdown;

    // 통계 — scheduled/canceled/rejected/faulted/pending 은 임의 스레드(Interlocked),
    // fired 는 드라이버 단일 작성자(Volatile).
    private long _scheduledCount;
    private long _firedCount;
    private long _canceledCount;
    private long _rejectedCount;
    private long _faultedCount;
    private long _pendingCount;

    /// <summary>휠을 만든다. 원점은 생성 시각이다.</summary>
    /// <param name="options">설정. 생성 시점에 검증·스냅샷된다.</param>
    /// <exception cref="InvalidOperationException">옵션이 유효하지 않거나 휠 범위가 오버플로할 때.</exception>
    public TimerWheel(TimerWheelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options.Snapshot();
        _timeProvider = _options.TimeProvider;
        _logger = _options.Logger;
        _metrics = _options.MetricsSink;
        _rejectionLogGate = new IntervalGate(_options.RejectionLogInterval, _timeProvider);

        long frequency = _timeProvider.TimestampFrequency;
        MicrosecondArithmetic.ValidateFrequency(frequency);

        long tickRaw = Math.Max(1, MicrosecondArithmetic.ToRawTicks(_options.TickDuration, frequency));
        int levels = _options.LevelCount;
        int slotsPerLevel = _options.SlotsPerLevel;
        _slotMask = slotsPerLevel - 1;

        _slots = new TimerNode?[levels][];
        _slotDurationRaw = new long[levels];
        _currentSlotTick = new long[levels];

        long duration = tickRaw;
        for (int level = 0; level < levels; level++)
        {
            _slots[level] = new TimerNode?[slotsPerLevel];
            _slotDurationRaw[level] = duration;

            // 다음 레벨 슬롯 길이 = 이번 레벨 전체 범위. 오버플로는 조립 시점에 실패시킨다.
            if (level < levels - 1 && duration > long.MaxValue / slotsPerLevel)
            {
                throw new InvalidOperationException(
                    $"휠 범위가 오버플로한다: {nameof(TimerWheelOptions.TickDuration)} × " +
                    $"{nameof(TimerWheelOptions.SlotsPerLevel)}^{nameof(TimerWheelOptions.LevelCount)} 를 줄여야 한다.");
            }

            duration *= slotsPerLevel;
        }

        _originRaw = _timeProvider.GetTimestamp();
    }

    /// <summary>현재 통계의 스냅샷.</summary>
    public TimerWheelStatistics Statistics =>
        new(
            Volatile.Read(ref _scheduledCount),
            Volatile.Read(ref _firedCount),
            Volatile.Read(ref _canceledCount),
            Volatile.Read(ref _rejectedCount),
            Volatile.Read(ref _faultedCount),
            Volatile.Read(ref _pendingCount));

    /// <summary>타이머를 예약한다. 아무 스레드에서나 안전하다.</summary>
    /// <param name="job">만료·취소 콜백.</param>
    /// <param name="delay">지금부터의 지연. 0 이하는 다음 <see cref="Advance"/>에서 즉시 발화한다.</param>
    /// <param name="handle">수락 시 취소에 쓸 핸들. 거부 시 <see cref="TimerHandle.None"/>.</param>
    /// <returns>수락 여부. 실패는 예외가 아니라 값이다(핫패스 제어 흐름).</returns>
    public TimerScheduleStatus TrySchedule(ITimerJob job, TimeSpan delay, out TimerHandle handle)
    {
        ArgumentNullException.ThrowIfNull(job);
        handle = TimerHandle.None;

        if (Volatile.Read(ref _shutdown) != 0)
        {
            return TimerScheduleStatus.Stopped;
        }

        if (Interlocked.Increment(ref _pendingCount) > _options.MaxPendingTimers)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _rejectedCount);
            _metrics?.Count(RealTimeMetricNames.TimerRejected, 1, default);

            if (_rejectionLogGate.TryConsume() && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Log(
                    LogLevel.Warning,
                    RealTimeEvents.TimerRejected,
                    _options.MaxPendingTimers,
                    null,
                    static (limit, _) => $"살아 있는 타이머가 상한({limit})에 도달해 예약을 거부했다.");
            }

            return TimerScheduleStatus.CapacityExceeded;
        }

        TimerNode node = RentNode();
        node.Job = job;
        node.DeadlineRaw = _timeProvider.GetTimestamp()
            + Math.Max(0, MicrosecondArithmetic.ToRawTicks(delay, _timeProvider.TimestampFrequency));

        uint generation = UnpackGeneration(Volatile.Read(ref node.StateAndGeneration));
        // 상태 공개는 필드 대입 뒤에 — Volatile.Write 가 Job/Deadline 의 release 장벽이다.
        Volatile.Write(ref node.StateAndGeneration, Pack(generation, StatePending));

        PushStack(ref _incomingHead, node);

        // 셧다운과의 경합: 플래그 검사 후 push 사이에 Shutdown 이 드레인을 끝냈을 수 있다.
        // 그 경우 이 노드는 아무도 처리하지 않으므로 여기서 직접 취소로 종결한다.
        if (Volatile.Read(ref _shutdown) != 0)
        {
            if (TryTransition(node, generation, StatePending, StateCanceled))
            {
                Interlocked.Decrement(ref _pendingCount);
            }

            return TimerScheduleStatus.Stopped;
        }

        Interlocked.Increment(ref _scheduledCount);
        _metrics?.Count(RealTimeMetricNames.TimerScheduled, 1, default);
        _metrics?.AdjustGauge(RealTimeMetricNames.TimerPending, 1, default);

        handle = new TimerHandle(this, node, generation);
        return TimerScheduleStatus.Accepted;
    }

    /// <summary>
    /// 현재 시각까지 휠을 진행시켜 만료 타이머를 발화한다. <b>단일 드라이버 전용.</b>
    /// </summary>
    /// <returns>이번 호출에서 발화한 타이머 수.</returns>
    /// <remarks>
    /// 상위(굵은) 레벨을 먼저 진행시킨다 — 상위에서 하위로 캐스케이딩된 타이머가 <b>같은
    /// 패스에서</b> 발화하기 위해서다(레거시 <c>ProcessExpired</c>의 순서 통찰 승계).
    /// 셧다운 후에는 아무것도 하지 않는다.
    /// </remarks>
    public int Advance()
    {
        if (Volatile.Read(ref _shutdown) != 0)
        {
            return 0;
        }

        long nowRaw = _timeProvider.GetTimestamp();
        int fired = 0;

        // 유입 드레인: Treiber 스택 전체를 원자 한 번으로 떼어낸다 (O(1) 배치 추출 — 승계).
        TimerNode? node = Interlocked.Exchange(ref _incomingHead, null);
        while (node is not null)
        {
            TimerNode? next = node.StackNext;
            node.StackNext = null;
            fired += PlaceOrFire(node, nowRaw);
            node = next;
        }

        for (int level = _slots.Length - 1; level >= 0; level--)
        {
            fired += AdvanceLevel(level, nowRaw);
        }

        return fired;
    }

    /// <summary>
    /// 휠을 닫는다. 남은 타이머 전부에 <see cref="ITimerJob.OnTimerCanceled"/>를 통지한다.
    /// <b>단일 드라이버 전용</b>이고, 이후의 예약은 <see cref="TimerScheduleStatus.Stopped"/>로 거부된다.
    /// </summary>
    /// <returns>취소 통지된 타이머 수.</returns>
    /// <remarks>
    /// 만료 콜백이 아니라 취소 콜백이다 — 핸들러는 "시간이 됐다"와 "서버가 내려간다"를
    /// 구별한다. 레거시는 이 구별이 없어 취소된 지연이 완료 신호를 Set 했다.
    /// </remarks>
    public int Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return 0;
        }

        int canceled = 0;

        TimerNode? node = Interlocked.Exchange(ref _incomingHead, null);
        while (node is not null)
        {
            TimerNode? next = node.StackNext;
            node.StackNext = null;
            canceled += CancelForShutdown(node);
            node = next;
        }

        foreach (TimerNode?[] level in _slots)
        {
            for (int slot = 0; slot < level.Length; slot++)
            {
                TimerNode? head = level[slot];
                level[slot] = null;
                while (head is not null)
                {
                    TimerNode? next = head.SlotNext;
                    head.SlotNext = null;
                    canceled += CancelForShutdown(head);
                    head = next;
                }
            }
        }

        return canceled;
    }

    /// <summary>핸들의 취소 진입점. 성공하면 <see cref="ITimerJob.OnTimerCanceled"/>를 이 스레드에서 부른다.</summary>
    internal bool TryCancelNode(TimerNode node, uint generation)
    {
        // Job 은 CAS 전에 읽는다 — CAS 가 성공하면 그 시점까지 노드가 우리 세대의 Pending
        // 이었다는 뜻이므로 이 읽기가 소급 유효해진다. CAS 가 실패하면 쓰지 않는다.
        ITimerJob? job = node.Job;

        if (!TryTransition(node, generation, StatePending, StateCanceled))
        {
            return false; // 이미 발화했거나, 이미 취소됐거나, 노드가 재사용됐다(세대 불일치).
        }

        Interlocked.Decrement(ref _pendingCount);
        Interlocked.Increment(ref _canceledCount);
        _metrics?.Count(RealTimeMetricNames.TimerCanceled, 1, default);
        _metrics?.AdjustGauge(RealTimeMetricNames.TimerPending, -1, default);

        InvokeCanceled(job!);
        return true;
        // 노드 자체는 아직 슬롯에 있다. 물리적 제거·풀 반납은 드라이버가 해당 슬롯에
        // 도달했을 때 한다 — 임의 스레드가 슬롯 리스트를 만지면 드라이버 전용 계약이 깨진다.
    }

    private int AdvanceLevel(int level, long nowRaw)
    {
        long slotDuration = _slotDurationRaw[level];
        long target = (nowRaw - _originRaw) / slotDuration;
        long current = _currentSlotTick[level];
        if (target <= current)
        {
            return 0;
        }

        int fired = 0;
        if (target - current >= _slots[level].Length)
        {
            // 한 바퀴 이상 밀렸다: 전 슬롯 1회 순회로 비용 상한을 지킨다.
            // 진행 위치를 먼저 갱신해야 순회 중 재배치가 "이미 지난 슬롯"에 꽂히지 않는다.
            _currentSlotTick[level] = target;
            for (int slot = 0; slot < _slots[level].Length; slot++)
            {
                fired += ProcessSlot(level, slot, nowRaw);
            }
        }
        else
        {
            for (long tick = current + 1; tick <= target; tick++)
            {
                // 슬롯 처리 전에 진행 위치를 갱신한다. 처리 중 재배치(Place)가 이 값을 보고
                // "최소 다음 슬롯"으로 클램프하므로, 갱신이 늦으면 방금 지나간 슬롯에 꽂혀
                // 한 바퀴를 헛돈다.
                _currentSlotTick[level] = tick;
                fired += ProcessSlot(level, (int)(tick & _slotMask), nowRaw);
            }
        }

        return fired;
    }

    private int ProcessSlot(int level, int slot, long nowRaw)
    {
        TimerNode? node = _slots[level][slot];
        _slots[level][slot] = null;
        int fired = 0;
        while (node is not null)
        {
            TimerNode? next = node.SlotNext;
            node.SlotNext = null;
            fired += PlaceOrFire(node, nowRaw);
            node = next;
        }

        return fired;
    }

    /// <summary>만료면 발화, 아니면 (재)배치, 취소됐으면 회수. 항목별 격리(9.2).</summary>
    private int PlaceOrFire(TimerNode node, long nowRaw)
    {
        long stateAndGen = Volatile.Read(ref node.StateAndGeneration);
        if (UnpackState(stateAndGen) == StateCanceled)
        {
            // 취소 콜백은 취소자 스레드가 이미 호출했다. 여기서는 회수만 한다.
            ReturnNode(node);
            return 0;
        }

        if (node.DeadlineRaw <= nowRaw)
        {
            return Fire(node, stateAndGen) ? 1 : 0;
        }

        Place(node, nowRaw);
        return 0;
    }

    private void Place(TimerNode node, long nowRaw)
    {
        long deadlineFromOrigin = node.DeadlineRaw - _originRaw;
        int lastLevel = _slots.Length - 1;

        for (int level = 0; level <= lastLevel; level++)
        {
            long deadlineTick = deadlineFromOrigin / _slotDurationRaw[level];
            long currentTick = _currentSlotTick[level];

            if (deadlineTick - currentTick < _slots[level].Length || level == lastLevel)
            {
                if (deadlineTick <= currentTick)
                {
                    // 이미 지난 슬롯 경계다(마감은 미래) — 최소 다음 슬롯. 최대 슬롯 길이만큼
                    // 늦게 발화하는 해상도 비용이며, 일찍 발화하는 것보다 낫다.
                    deadlineTick = currentTick + 1;
                }
                else if (level == lastLevel && deadlineTick - currentTick >= _slots[level].Length)
                {
                    // 최상위 휠 범위 초과: 가장 먼 슬롯에 주차하고 재순회로 자기 교정한다.
                    deadlineTick = currentTick + _slots[level].Length - 1;
                }

                int slot = (int)(deadlineTick & _slotMask);
                node.SlotNext = _slots[level][slot];
                _slots[level][slot] = node;
                return;
            }
        }
    }

    private bool Fire(TimerNode node, long stateAndGen)
    {
        if (UnpackState(stateAndGen) != StatePending)
        {
            ReturnNode(node);
            return false;
        }

        uint generation = UnpackGeneration(stateAndGen);
        if (Interlocked.CompareExchange(
                ref node.StateAndGeneration, Pack(generation, StateFired), stateAndGen) != stateAndGen)
        {
            // 취소가 이겼다. 콜백·통계는 취소자가 처리했다.
            ReturnNode(node);
            return false;
        }

        Interlocked.Decrement(ref _pendingCount);
        Volatile.Write(ref _firedCount, _firedCount + 1);
        _metrics?.Count(RealTimeMetricNames.TimerFired, 1, default);
        _metrics?.AdjustGauge(RealTimeMetricNames.TimerPending, -1, default);

        ITimerJob job = node.Job!;
        ReturnNode(node);

        try
        {
            job.OnTimerExpired();
        }
#pragma warning disable CA1031 // 콜백은 애플리케이션 코드다. 항목별 격리(9.2) — 나쁜 콜백 하나가 나머지 타이머를 죽이지 않는다.
        catch (Exception exception)
        {
            Interlocked.Increment(ref _faultedCount);
            _metrics?.Count(RealTimeMetricNames.TimerFaults, 1, default);
            LogCallbackFault(exception);
        }
#pragma warning restore CA1031

        return true;
    }

    private int CancelForShutdown(TimerNode node)
    {
        long stateAndGen = Volatile.Read(ref node.StateAndGeneration);
        if (UnpackState(stateAndGen) != StatePending)
        {
            return 0; // 이미 취소자가 종결했다.
        }

        uint generation = UnpackGeneration(stateAndGen);
        if (!TryTransition(node, generation, StatePending, StateCanceled))
        {
            return 0;
        }

        Interlocked.Decrement(ref _pendingCount);
        Interlocked.Increment(ref _canceledCount);
        _metrics?.Count(RealTimeMetricNames.TimerCanceled, 1, default);
        _metrics?.AdjustGauge(RealTimeMetricNames.TimerPending, -1, default);

        InvokeCanceled(node.Job!);
        return 1;
        // 셧다운 뒤 휠은 통째로 버려진다 — 풀 반납은 생략한다.
    }

    private void InvokeCanceled(ITimerJob job)
    {
        try
        {
            job.OnTimerCanceled();
        }
#pragma warning disable CA1031 // 콜백은 애플리케이션 코드다. 취소 통지 실패가 취소 자체를 막지 않는다.
        catch (Exception exception)
        {
            Interlocked.Increment(ref _faultedCount);
            _metrics?.Count(RealTimeMetricNames.TimerFaults, 1, default);
            LogCallbackFault(exception);
        }
#pragma warning restore CA1031
    }

    private void LogCallbackFault(Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(
                LogLevel.Error,
                RealTimeEvents.TimerCallbackFaulted,
                0,
                exception,
                static (_, ex) => $"타이머 콜백이 예외로 끝났다: {ex?.Message}");
        }
    }

    private TimerNode RentNode()
    {
        var spinner = new SpinWait();
        while (true)
        {
            TimerNode? head = Volatile.Read(ref _poolHead);
            if (head is null)
            {
                return new TimerNode();
            }

            if (Interlocked.CompareExchange(ref _poolHead, head.StackNext, head) == head)
            {
                Interlocked.Decrement(ref _poolCount);
                head.StackNext = null;
                return head;
            }

            spinner.SpinOnce(); // 재시도 시에만 스핀한다 (9.3).
        }
    }

    private void ReturnNode(TimerNode node)
    {
        uint generation = UnpackGeneration(Volatile.Read(ref node.StateAndGeneration));
        node.Job = null;
        node.SlotNext = null;
        // 세대 증가가 곧 낡은 핸들의 무효화다. Free 공개 전에 참조를 끊는다.
        Volatile.Write(ref node.StateAndGeneration, Pack(generation + 1, StateFree));

        if (Interlocked.Increment(ref _poolCount) > _options.NodePoolCapacity)
        {
            Interlocked.Decrement(ref _poolCount);
            return; // 상한 초과분은 GC 에 맡긴다 — 무제한 풀 금지.
        }

        PushStack(ref _poolHead, node);
    }

    private static void PushStack(ref TimerNode? head, TimerNode node)
    {
        var spinner = new SpinWait();
        while (true)
        {
            TimerNode? current = Volatile.Read(ref head);
            node.StackNext = current;
            if (Interlocked.CompareExchange(ref head, node, current) == current)
            {
                return;
            }

            spinner.SpinOnce(); // 재시도 시에만 스핀한다 (9.3).
        }
    }

    private static bool TryTransition(TimerNode node, uint generation, int fromState, int toState)
    {
        long expected = Pack(generation, fromState);
        return Interlocked.CompareExchange(
            ref node.StateAndGeneration, Pack(generation, toState), expected) == expected;
    }

    private static long Pack(uint generation, int state) => ((long)generation << 32) | (uint)state;

    private static uint UnpackGeneration(long stateAndGeneration) => (uint)((ulong)stateAndGeneration >> 32);

    private static int UnpackState(long stateAndGeneration) => (int)(stateAndGeneration & 0xFFFF_FFFF);
}
