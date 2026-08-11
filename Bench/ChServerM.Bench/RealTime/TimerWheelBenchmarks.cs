using System;
using BenchmarkDotNet.Attributes;
using ChServerM.RealTime;

namespace ChServerM.Bench.RealTime;

/// <summary>
/// 타이밍 휠 처리 용량 측정 — Phase 17 로드맵 항목 "틱당 처리 용량"의 근거.
/// </summary>
/// <remarks>
/// <para>
/// <b>무엇을 재는가.</b> ① 예약+취소 왕복(발화 없는 수명), ② 예약+일괄 발화(한 번의
/// <c>Advance</c> 가 만기 타이머 N 개를 발화시키는 비용 = 틱당 처리 용량),
/// ③ 만기 없는 진행(틱당 고정 비용 바닥).
/// </para>
/// <para>
/// <b>측정의 한계.</b> 시간은 가짜 프로바이더로 민다 — 슬립·OS 타이머 해상도는 여기 없다
/// (그쪽은 <c>tickjitter</c> 리포트가 잰다). 콜백은 빈 몸체라 실제 워크로드의 콜백 비용은
/// 포함되지 않는다. 단일 스레드 측정이므로 예약 경합(멀티 프로듀서)의 비용도 별개다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
public class TimerWheelBenchmarks
{
    private const int TimerCount = 10_000;

    private SteppingTimeProvider _provider = null!;
    private TimerWheel _wheel = null!;
    private TimerHandle[] _handles = null!;
    private NoOpJob _job = null!;

    /// <summary>시각을 수동으로 미는 프로바이더. 슬립 없이 휠만 측정하기 위한 장치다.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 10_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan delta) =>
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
    }

    private sealed class NoOpJob : ITimerJob
    {
        public void OnTimerExpired()
        {
        }

        public void OnTimerCanceled()
        {
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        _provider = new SteppingTimeProvider();
        _wheel = new TimerWheel(new TimerWheelOptions
        {
            TimeProvider = _provider,
            // 풀 상한을 측정 규모에 맞춘다 — 상한 미달로 노드가 버려지면
            // 할당량이 풀 정책이 아니라 상한 설정을 재게 된다.
            NodePoolCapacity = TimerCount,
            MaxPendingTimers = TimerCount * 2,
        });
        _handles = new TimerHandle[TimerCount];
        _job = new NoOpJob();

        // 풀 예열: 첫 왕복의 노드 할당을 정상 상태 측정에서 뺀다.
        ScheduleThenCancelAll();
    }

    /// <summary>예약 N 개 + 전부 취소. 발화 없는 타이머 수명의 왕복 비용.</summary>
    [Benchmark(OperationsPerInvoke = TimerCount)]
    public void ScheduleThenCancelAll()
    {
        for (int i = 0; i < TimerCount; i++)
        {
            _wheel.TrySchedule(_job, TimeSpan.FromMinutes(10), out _handles[i]);
        }

        for (int i = 0; i < TimerCount; i++)
        {
            _handles[i].TryCancel();
        }

        // 취소된 노드를 회수해 다음 호출이 풀에서 시작하게 한다.
        _provider.Advance(TimeSpan.FromMinutes(11));
        _wheel.Advance();
    }

    /// <summary>예약 N 개 + 한 번의 진행으로 전부 발화. 틱당 처리 용량의 상한 측정.</summary>
    [Benchmark(OperationsPerInvoke = TimerCount)]
    public int ScheduleThenFireAll()
    {
        for (int i = 0; i < TimerCount; i++)
        {
            _wheel.TrySchedule(_job, TimeSpan.FromMilliseconds(50), out _);
        }

        _provider.Advance(TimeSpan.FromMilliseconds(200));
        return _wheel.Advance();
    }

    /// <summary>만기가 없는 슬롯 하나 진행. 틱당 고정 비용의 바닥.</summary>
    [Benchmark]
    public int AdvanceOneEmptySlot()
    {
        _provider.Advance(TimeSpan.FromMilliseconds(100));
        return _wheel.Advance();
    }
}
