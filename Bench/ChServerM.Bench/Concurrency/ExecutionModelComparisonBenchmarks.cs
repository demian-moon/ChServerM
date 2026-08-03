using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Concurrency;
using ChServerM.Execution;

namespace ChServerM.Bench.Concurrency;

/// <summary>
/// 순서 보장의 비용을 잰다 — 파티션 모델 vs 무순서 병렬 vs 직렬.
/// </summary>
/// <remarks>
/// <para>
/// <b>이것이 ADR-0005 의 진짜 질문이다.</b> "확장되는가"만으로는 부족하다.
/// 순서 보장을 위해 <b>얼마를 지불하는가</b>를 알아야 그 대가가 합당한지 판단할 수 있다.
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="Serial"/></term>
///     <description>단일 스레드. 확장 배수를 계산하는 분모</description>
///   </item>
///   <item>
///     <term><see cref="ThreadPoolParallel"/></term>
///     <description>
///       스레드풀에 그냥 던진다. <b>순서 보장이 없다</b> — 이 머신에서 도달 가능한 병렬성의
///       상한이다. 파티션 모델이 이것에 얼마나 근접하는가가 순서 보장의 비용이다
///     </description>
///   </item>
///   <item>
///     <term><see cref="Partitioned"/></term>
///     <description>파티션 모델. 순서 보장을 제공한다</description>
///   </item>
///   <item>
///     <term><see cref="GlobalLock"/></term>
///     <description>
///       전역 락 하나로 순서를 보장하는 방식. 레거시가 고민했던 대안이고
///       ADR-0005 의 "탈락 이유"에 적힌 것이 실제로 그런지 확인한다
///     </description>
///   </item>
/// </list>
/// <para>
/// 파티션 수는 <b>논리 코어 수로 고정</b>한다. 이 비교의 변수는 실행 모델이지 파티션 수가 아니다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
public class ExecutionModelComparisonBenchmarks
{
    private static readonly int WorkerCount = Environment.ProcessorCount;

    private PartitionedExecutionModel _model = null!;
    private int _unitsPerWorker;

    [GlobalSetup]
    public void Setup()
    {
        _unitsPerWorker = PartitionWorkload.TotalUnits / WorkerCount;
        _model = new PartitionedExecutionModel(new PartitionedExecutionOptions
        {
            PartitionCount = WorkerCount,
            QueueCapacity = 1024,
        });
    }

    [GlobalCleanup]
    public void Cleanup() => _model.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true, Description = "직렬 (단일 스레드)")]
    public long Serial() => PartitionWorkload.ExecuteUnits(PartitionWorkload.TotalUnits, 1);

    [Benchmark(Description = "스레드풀 병렬 (순서 보장 없음 — 상한)")]
    public long ThreadPoolParallel()
    {
        long[] results = new long[WorkerCount];
        int units = _unitsPerWorker;

        Parallel.For(0, WorkerCount, i => results[i] = PartitionWorkload.ExecuteUnits(units, i + 1));

        long total = 0;
        foreach (long value in results)
        {
            total += value;
        }

        return total;
    }

    [Benchmark(Description = "파티션 모델 (순서 보장)")]
    public long Partitioned()
    {
        Task<long>[] tasks = new Task<long>[WorkerCount];
        int units = _unitsPerWorker;

        for (int i = 0; i < WorkerCount; i++)
        {
            IExecutionPartition partition = _model.GetPartition(i);
            long seed = i + 1;

            tasks[i] = Task.Factory.StartNew(
                () => PartitionWorkload.ExecuteUnits(units, seed),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                partition.Scheduler);
        }

        Task.WaitAll(tasks);

        long total = 0;
        foreach (Task<long> task in tasks)
        {
            total += task.Result;
        }

        return total;
    }

    [Benchmark(Description = "전역 락 (순서 보장, 확장 안 됨)")]
    public long GlobalLock()
    {
        object gate = new();
        long total = 0;
        int units = _unitsPerWorker;

        Parallel.For(0, WorkerCount, i =>
        {
            // 작업 자체를 락 안에서 한다 — 전역 락으로 순서를 보장하면 이 모양이 된다.
            // 그래서 코어를 늘려도 처리량이 늘지 않는다는 것을 수치로 보인다.
            lock (gate)
            {
                total += PartitionWorkload.ExecuteUnits(units, i + 1);
            }
        });

        return total;
    }
}
