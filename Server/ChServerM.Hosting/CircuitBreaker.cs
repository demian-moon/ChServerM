using System;
using System.Threading;
using ChServerM.Resilience;

namespace ChServerM.Hosting;

/// <summary>
/// <see cref="ICircuitBreaker"/> 의 무락 구현 — 연속 실패로 열고, 시간이 지나면 시험한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 죽은 대상에 계속 호출하면 호출자의 스레드·커넥션이 타임아웃을 기다리며
/// 묶여 <b>장애가 호출자 쪽으로 번진다</b>. 이 타입이 그 전파를 끊는다. ADR-0027 의 보류를
/// 푼 첫 구현이며, 대상은 Redis 세션 저장소다(ADR-0034).
/// </para>
///
/// <para>
/// <b>스레드 규약 — 스레드 안전하다.</b> 상태 전이는 전부
/// <see cref="Interlocked"/> CAS 이며 락이 없다. 여러 요청 경로가 동시에 지나가는 것을
/// 전제한다.
/// </para>
///
/// <para>
/// <b>⚠ 반열림의 시험 자리는 <c>finally</c> 로 반납해야 한다.</b> <see cref="TryEnter"/> 가
/// <see langword="true"/> 를 준 뒤 결과 보고가 누락되면 시험 자리가 영구히 점유되어
/// <b>회로가 영원히 닫히지 않는다</b> — 레거시 <c>ExecutableTaskDispatcherM</c> 이
/// <c>try/finally</c> 누락으로 카운터를 복원하지 못해 디스패처를 영구 정지시킨 것과 정확히
/// 같은 부류다(CLAUDE.md 9.2). 데코레이터
/// (<see cref="CircuitBreakingSessionStore"/>)가 그 규율을 지키는 참조 사용법이다.
/// </para>
///
/// <para>
/// <b>상태 전이</b>
/// </para>
/// <code>
///   Closed --(연속 실패 N회)--> Open
///   Open   --(BreakDuration 경과, 첫 TryEnter)--> HalfOpen
///   HalfOpen --(연속 성공 M회)--> Closed
///   HalfOpen --(실패 1회)--> Open        // 시험에 실패하면 즉시 다시 닫는다
/// </code>
///
/// <para>
/// <b>왜 반열림에서 실패 하나로 즉시 여는가.</b> 시험은 "회복했는가" 를 묻는 것이므로
/// 한 번의 실패가 곧 "아직 아니다" 다. 여기서 관대하면 아직 아픈 대상에 부하를 몰아준다.
/// </para>
/// </remarks>
public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>현재 상태. <see cref="CircuitState"/> 를 int 로 담아 CAS 한다.</summary>
    private int _state = (int)CircuitState.Closed;

    /// <summary>닫힘 상태의 연속 실패 수.</summary>
    private int _consecutiveFailures;

    /// <summary>반열림 상태의 연속 성공 수.</summary>
    private int _halfOpenSuccesses;

    /// <summary>반열림에서 현재 진행 중인 시험 호출 수.</summary>
    private int _halfOpenProbesInFlight;

    /// <summary>열림 상태가 끝나는 시각(틱). 이 시각 이후 첫 <see cref="TryEnter"/> 가 시험을 연다.</summary>
    private long _openUntilTicks;

    /// <summary>서킷 브레이커를 만든다.</summary>
    /// <param name="options">설정. <see langword="null"/> 이면 기본값.</param>
    /// <param name="timeProvider">시간 원천. <see langword="null"/> 이면 시스템 시계.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않다.</exception>
    public CircuitBreaker(CircuitBreakerOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new CircuitBreakerOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Name => _options.Name;

    /// <inheritdoc/>
    public CircuitState State => (CircuitState)Volatile.Read(ref _state);

    /// <inheritdoc/>
    public bool TryEnter()
    {
        while (true)
        {
            CircuitState current = (CircuitState)Volatile.Read(ref _state);

            switch (current)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (_timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _openUntilTicks))
                    {
                        return false;
                    }

                    // 차단 시간이 지났다. 시험 상태로 전이를 시도한다 — 이긴 스레드만 전이하고
                    // 나머지는 루프를 돌아 새 상태에서 다시 판정한다.
                    if (Interlocked.CompareExchange(
                            ref _state, (int)CircuitState.HalfOpen, (int)CircuitState.Open) == (int)CircuitState.Open)
                    {
                        Volatile.Write(ref _halfOpenSuccesses, 0);
                        Volatile.Write(ref _halfOpenProbesInFlight, 0);
                    }

                    continue;

                case CircuitState.HalfOpen:
                    return TryTakeProbeSlot();

                default:
                    return true;
            }
        }
    }

    /// <inheritdoc/>
    public void RecordSuccess()
    {
        CircuitState current = (CircuitState)Volatile.Read(ref _state);

        if (current == CircuitState.HalfOpen)
        {
            // 시험 자리를 반납한다. 이것을 빠뜨리면 회로가 영원히 닫히지 않는다.
            Interlocked.Decrement(ref _halfOpenProbesInFlight);

            if (Interlocked.Increment(ref _halfOpenSuccesses) >= _options.HalfOpenSuccessThreshold)
            {
                Close();
            }

            return;
        }

        // 닫힘 상태의 성공은 연속 실패 카운터를 되돌린다 — "연속" 의 정의다.
        Volatile.Write(ref _consecutiveFailures, 0);
    }

    /// <inheritdoc/>
    public void RecordFailure(Exception? exception = null)
    {
        _ = exception; // 판정에는 쓰지 않는다. 로깅은 호출자의 몫이다(계약 문서).

        CircuitState current = (CircuitState)Volatile.Read(ref _state);

        if (current == CircuitState.HalfOpen)
        {
            Interlocked.Decrement(ref _halfOpenProbesInFlight);

            // 시험 실패는 곧 "아직 아니다" — 관대하면 아픈 대상에 부하를 몰아준다.
            Open();
            return;
        }

        if (current == CircuitState.Open)
        {
            // 이미 열려 있다. 늦게 도착한 실패 보고이므로 무시한다.
            return;
        }

        if (Interlocked.Increment(ref _consecutiveFailures) >= _options.FailureThreshold)
        {
            Open();
        }
    }

    /// <summary>회로를 강제로 닫는다(운영 도구·테스트용).</summary>
    public void Reset() => Close();

    /// <summary>반열림에서 시험 자리를 하나 잡는다.</summary>
    /// <remarks>
    /// CAS 루프로 상한을 지킨다 — <see cref="Interlocked.Increment(ref int)"/> 후 초과분을
    /// 되돌리는 방식은 그 찰나에 다른 스레드가 상한을 잘못 읽게 한다.
    /// </remarks>
    private bool TryTakeProbeSlot()
    {
        while (true)
        {
            int inFlight = Volatile.Read(ref _halfOpenProbesInFlight);
            if (inFlight >= _options.HalfOpenConcurrentProbes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _halfOpenProbesInFlight, inFlight + 1, inFlight) == inFlight)
            {
                return true;
            }

            // 경합 — 재시도할 때만 스핀한다(9.3).
        }
    }

    private void Open()
    {
        Volatile.Write(ref _openUntilTicks, _timeProvider.GetUtcNow().UtcTicks + _options.BreakDuration.Ticks);
        Volatile.Write(ref _halfOpenSuccesses, 0);
        Volatile.Write(ref _halfOpenProbesInFlight, 0);
        Volatile.Write(ref _state, (int)CircuitState.Open);
    }

    private void Close()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
        Volatile.Write(ref _halfOpenSuccesses, 0);
        Volatile.Write(ref _halfOpenProbesInFlight, 0);
        Volatile.Write(ref _state, (int)CircuitState.Closed);
    }
}
