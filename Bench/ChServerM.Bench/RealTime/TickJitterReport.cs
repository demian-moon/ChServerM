using System;
using System.Globalization;
using System.Threading;
using ChServerM.RealTime;

namespace ChServerM.Bench.RealTime;

/// <summary>
/// 틱 지터 리포트 — Phase 17 로드맵 항목 "틱 지터"의 근거. <c>dotnet run -- tickjitter</c> 로 돈다.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 BenchmarkDotNet 이 아닌가.</b> BDN 이 재는 것은 연산당 평균 시간이고, 여기서 알고
/// 싶은 것은 <b>예정 대비 시작 지연의 분포</b>(p50/p99/최대)다. 슬립·OS 타이머 해상도가
/// 측정 대상 그 자체이므로 시간을 가짜로 밀 수도 없다. <c>retention</c> 리포트와 같은
/// 이유로 커스텀 하네스를 쓴다.
/// </para>
/// <para>
/// <b>무엇을 검증하는가.</b> 스핀 구간(<see cref="TickLoopOptions.SpinWaitWindow"/>) 유무가
/// 지터에 주는 영향 — "마감 직전 1ms 스핀이 지터를 OS 해상도(Windows 15.6ms)에서 밀리초
/// 미만으로 내린다"는 옵션 문서의 주장을 수치로 방어한다.
/// </para>
/// </remarks>
internal static class TickJitterReport
{
    private sealed class DriftRecorder : ITickHandler
    {
        private readonly long[] _driftMicros;
        private int _count;

        public DriftRecorder(int capacity) => _driftMicros = new long[capacity];

        public int Count => Volatile.Read(ref _count);

        public void OnTick(in TickContext context)
        {
            int index = _count;
            if (index < _driftMicros.Length)
            {
                _driftMicros[index] = context.StartDrift.Ticks / TimeSpan.TicksPerMicrosecond;
                Volatile.Write(ref _count, index + 1);
            }
        }

        public (long P50, long P99, long Max) Percentiles()
        {
            int count = Count;
            var sorted = new long[count];
            Array.Copy(_driftMicros, sorted, count);
            Array.Sort(sorted);
            return (sorted[count / 2], sorted[(int)(count * 0.99)], sorted[count - 1]);
        }
    }

    public static void Run()
    {
        Console.WriteLine("=== 틱 지터 리포트 (예정 대비 시작 지연, µs) ===");
        Console.WriteLine("  각 조합을 5초씩 돈다. 수치는 docs/BENCHMARKS.md 틱 지터 절에 기록한다.");
        Console.WriteLine();
        Console.WriteLine("  간격      스핀 구간   틱 수    p50       p99       최대      초과");
        Console.WriteLine("  --------  ---------  ------  --------  --------  --------  ----");

        foreach (TimeSpan interval in new[]
                 {
                     TimeSpan.FromMilliseconds(1),
                     TimeSpan.FromMilliseconds(10),
                     TimeSpan.FromMilliseconds(50),
                 })
        {
            foreach (TimeSpan spin in new[]
                     {
                         TimeSpan.Zero,
                         TimeSpan.FromMilliseconds(1),
                         TimeSpan.FromMilliseconds(16), // Windows 기본 타이머 해상도(15.6ms)보다 크게
                     })
            {
                Measure(interval, spin);
            }
        }

        Console.WriteLine();
        Console.WriteLine("  스핀 구간 0 = 순수 슬립(OS 타이머 해상도에 묶인다).");
        Console.WriteLine("  스핀 구간은 간격보다 크면 간격으로 클램프된다(간격 전체 스핀).");
        Console.WriteLine("  ⚠ 스핀 구간이 OS 슬립 해상도(Windows ≈15.6ms)보다 작으면 슬립 초과 수면이");
        Console.WriteLine("    스핀 구간을 건너뛰어 효과가 없다 — 정밀 대기가 필요하면 16ms 이상을 준다.");
        Console.WriteLine("  CPU 비용: 스핀 구간만큼 틱마다 코어를 태운다(16ms/50ms 틱 = 상한 32%).");
    }

    private static void Measure(TimeSpan interval, TimeSpan spinWindow)
    {
        var recorder = new DriftRecorder(capacity: 60_000);
        var loop = new TickLoop(recorder, new TickLoopOptions
        {
            TickInterval = interval,
            SpinWaitWindow = spinWindow > interval ? interval : spinWindow,
        });

        loop.Start();
        Thread.Sleep(TimeSpan.FromSeconds(5));
        loop.DisposeAsync().AsTask().GetAwaiter().GetResult();

        (long p50, long p99, long max) = recorder.Percentiles();
        TickLoopStatistics stats = loop.Statistics;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {interval.TotalMilliseconds,6:F0}ms  {spinWindow.TotalMilliseconds,7:F0}ms  {stats.TotalTicks,6}  {p50,8}  {p99,8}  {max,8}  {stats.OverrunTicks,4}"));
    }
}
