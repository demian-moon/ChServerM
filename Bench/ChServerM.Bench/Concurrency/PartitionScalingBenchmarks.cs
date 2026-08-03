using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Concurrency;
using ChServerM.Execution;

namespace ChServerM.Bench.Concurrency;

/// <summary>
/// <b>ADR-0005 의 검증 조건을 측정한다 — "코어 수 대비 처리량이 선형에 근접"하는가.</b>
/// </summary>
/// <remarks>
/// <para>
/// ADR-0005 는 스스로 무효 조건을 달아뒀다. 이 곡선이 선형에 근접하지 않으면
/// 키 기반 파티션 샤딩이라는 기본 전략 자체가 무효다. 그래서 이 벤치마크는
/// "성능 튜닝"이 아니라 <b>설계 결정의 생존 여부</b>를 판정한다.
/// </para>
/// <para>
/// <b>측정 방법.</b> 총 작업량을 고정하고(<see cref="PartitionWorkload.TotalUnits"/>)
/// 파티션 수만 바꾼다. 각 파티션은 자기 스케줄러에서 <c>총량 / 파티션 수</c>만큼 실행한다.
/// 선형이면 파티션을 2배로 늘릴 때 소요 시간이 절반이 된다.
/// </para>
/// <para>
/// <b>왜 스케줄러 경로인가.</b> 이것이 프로덕션의 주 경로다 —
/// <c>PartitionedConnectionHandler</c> 가 읽기 루프를 파티션 스케줄러에 고정하고,
/// 그 뒤 모든 작업이 그 위에서 돈다. <c>TryPost</c> 는 보조 경로이므로 별도로 잰다
/// (<see cref="PartitionPostBenchmarks"/>).
/// </para>
/// <para>
/// <b>단일 생산자 병목을 피한 이유이기도 하다.</b> 한 스레드가 480,000건을 큐에 밀어넣으면
/// 게시 비용이 병목이 되어 파티션이 아무리 많아도 확장되지 않는다. 그때 측정되는 것은
/// 파티션 모델이 아니라 생산자다.
/// </para>
/// <para>
/// <b>⚠ 이 측정의 한계 — 반드시 결과와 함께 읽어야 한다.</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>파티션 수를 바꾸는 것은 "코어 수를 바꾸는 것"의 근사다.</b> OS 는 여전히 모든 코어를
///     쓸 수 있다. 진짜 코어 제한은 프로세스 어피니티가 필요하고, .NET 의
///     <c>Process.ProcessorAffinity</c> 는 <b>리눅스에서 지원되지 않는다</b>.
///     실제 코어 제한 측정은 <c>taskset -c 0-3 dotnet run ...</c> 처럼 밖에서 감싸야 한다
///   </description></item>
///   <item><description>
///     <b>SMT 구간은 판정에서 제외한다.</b> 물리 코어 수를 넘으면 처리량이 선형으로 늘지 않는 것이
///     <b>정상</b>이다. 그 구간을 판정에 넣으면 멀쩡한 설계를 실패로 오판한다
///   </description></item>
/// </list>
/// </remarks>
[Config(typeof(BenchConfig))]
public class PartitionScalingBenchmarks
{
    private PartitionedExecutionModel _model = null!;

    /// <summary>파티션 수. 이 머신의 물리 12 / 논리 24 를 기준으로 잡았다.</summary>
    /// <remarks>
    /// 12 까지가 물리 코어 구간이고 그 이상은 SMT 다. 판정은 12 까지만 본다.
    /// 24 를 포함하는 이유는 <b>SMT 구간에서 무너지지 않는지</b>를 보기 위해서다 —
    /// 처리량이 오히려 떨어지면 경합 문제가 있다는 신호다.
    /// </remarks>
    [Params(1, 2, 4, 8, 12, 24)]
    public int PartitionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _model = new PartitionedExecutionModel(new PartitionedExecutionOptions
        {
            PartitionCount = PartitionCount,

            // 이 벤치마크는 스케줄러 경로만 쓴다. 큐 용량은 영향을 주지 않지만,
            // 기본값에 의존해 나중에 조용히 달라지지 않게 명시한다.
            QueueCapacity = 1024,
        });
    }

    [GlobalCleanup]
    public void Cleanup() => _model.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>총 작업량을 파티션에 균등 분배해 실행하고 전부 끝나기를 기다린다.</summary>
    /// <returns>계산 결과 합. 최적화로 사라지지 않게 반환한다.</returns>
    [Benchmark(Description = "고정 작업량을 파티션에 분배")]
    public long ExecuteFixedWork()
    {
        int unitsPerPartition = PartitionWorkload.TotalUnits / PartitionCount;
        Task<long>[] tasks = new Task<long>[PartitionCount];

        for (int i = 0; i < PartitionCount; i++)
        {
            IExecutionPartition partition = _model.GetPartition(i);
            long seed = i + 1;

            tasks[i] = Task.Factory.StartNew(
                () => PartitionWorkload.ExecuteUnits(unitsPerPartition, seed),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                partition.Scheduler);
        }

        // 벤치마크 본문에서의 블로킹은 허용된다 — 측정하려는 것이 벽시계 시간이다.
        Task.WaitAll(tasks);

        long total = 0;
        foreach (Task<long> task in tasks)
        {
            total += task.Result;
        }

        return total;
    }
}
