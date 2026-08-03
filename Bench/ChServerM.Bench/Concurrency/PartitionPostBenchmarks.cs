using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using ChServerM.Concurrency;
using ChServerM.Execution;

namespace ChServerM.Bench.Concurrency;

/// <summary>
/// <see cref="IExecutionPartition.TryPost{TWork}"/> 의 왕복 비용 — 보조 경로의 큐 비용.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 따로 재는가.</b> 주 경로(스케줄러)는 읽기 루프를 파티션에 고정해 프레임당 큐 비용이
/// 0이다. 하지만 타이머 만료나 다른 파티션에서 오는 작업은 <c>TryPost</c> 를 거친다.
/// 그 비용이 얼마인지 모르면 "타이머를 파티션에 주입해도 되는가"를 판단할 수 없다.
/// </para>
/// <para>
/// <b>작업 자체는 거의 0으로 둔다.</b> 여기서 알고 싶은 것은 계산 비용이 아니라
/// <b>큐 왕복 비용</b>이기 때문이다 — 게시 → 채널 → 소비 스레드 → 실행 → 박스 반납까지.
/// </para>
/// <para>
/// <b>정상 상태에서 게시당 할당이 0인지도 함께 확인한다.</b> 구조체 작업을 채널(참조 타입)에
/// 넣으려면 상자가 필요하고, 그 상자를 풀링하지 않으면 게시마다 박싱된다.
/// <c>MemoryDiagnoser</c> 의 Allocated 열이 그것을 드러낸다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
public class PartitionPostBenchmarks
{
    /// <summary>한 번의 측정에서 게시할 작업 수.</summary>
    /// <remarks>
    /// 큐 용량보다 크게 잡는다. 그래야 <b>게시와 소비가 겹치는</b> 실제 동작을 재게 된다 —
    /// 용량 안에 다 들어가면 소비가 시작되기 전에 게시가 끝나 큐가 버퍼로만 작동한다.
    /// </remarks>
    private const int PostCount = 50_000;

    private PartitionedExecutionModel _model = null!;
    private IExecutionPartition _partition = null!;
    private CountdownEvent _completed = null!;

    /// <summary>게시 대상 파티션 수. 1이면 순수 큐 비용, 그 이상이면 분배까지 포함한다.</summary>
    [Params(1, 8)]
    public int PartitionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _model = new PartitionedExecutionModel(new PartitionedExecutionOptions
        {
            PartitionCount = PartitionCount,

            // 게시가 거부되면 재시도 루프가 측정에 섞인다. 그것은 별도 관심사이므로
            // 여기서는 거부가 일어나지 않을 만큼 크게 잡는다.
            QueueCapacity = PostCount + 1024,
        });

        _partition = _model.GetPartition(0);
        _completed = new CountdownEvent(PostCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _model.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _completed.Dispose();
    }

    [IterationSetup]
    public void ResetCounter() => _completed.Reset(PostCount);

    /// <summary>작업을 전부 게시하고 전부 실행되기를 기다린다.</summary>
    [Benchmark(Description = "TryPost 후 전부 소비될 때까지", OperationsPerInvoke = PostCount)]
    public void PostAndDrain()
    {
        CountdownEvent completed = _completed;

        for (int i = 0; i < PostCount; i++)
        {
            IExecutionPartition target = PartitionCount == 1
                ? _partition
                : _model.GetPartition(i % PartitionCount);

            if (!target.TryPost(new SignalWork(completed)))
            {
                // 용량을 넉넉히 잡았으므로 여기 들어오면 설정이 잘못된 것이다.
                // 조용히 넘기지 않는다 — 거부된 작업은 실행되지 않고,
                // 그러면 아래 Wait 가 영원히 걸린다.
                throw new InvalidOperationException(
                    $"게시가 거부됐다. QueueCapacity 설정을 확인한다 (i={i}).");
            }
        }

        if (!completed.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("작업이 제한 시간 안에 소비되지 않았다.");
        }
    }

    /// <summary>카운트다운을 하나 줄이는 최소 작업.</summary>
    /// <remarks>
    /// 구조체다. <c>TryPost</c> 가 <c>struct</c> 로 제약한 이유가 박싱 회피이므로,
    /// 벤치마크도 같은 조건으로 재야 한다.
    /// </remarks>
    private readonly struct SignalWork(CountdownEvent completed) : IPartitionWork
    {
        public void Execute() => completed.Signal();
    }
}
