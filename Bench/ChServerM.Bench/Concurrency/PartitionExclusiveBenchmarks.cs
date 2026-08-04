using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using ChServerM.Concurrency;
using ChServerM.Execution;

namespace ChServerM.Bench.Concurrency;

/// <summary>
/// <b>ADR-0008 주 경로의 메시지 단위 비용을 측정한다</b> — 프레임 하나가
/// 배타 게시 → 파티션 실행 → 완료 신호 → 게시자 재개를 왕복하는 비용.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 구 확장성 곡선(<see cref="PartitionScalingBenchmarks"/>)은 파티션당
/// 태스크 하나가 통짜 루프를 도는 <b>청크 단위</b> 측정이라, 큐·완료 신호·스레드 인계 같은
/// 파티션 모델 고유의 프레임당 비용이 거의 들어가지 않았다 — 감사(2026-08-04)에서
/// "어떤 모델이든 좋게 나오는 측정"으로 지적된 지점이다. 이 벤치마크는 프로덕션의 실제
/// 주 경로와 같은 구조로 잰다: 게시자(읽기 루프 역할)가 프레임마다
/// <see cref="IExecutionPartition.TryEnqueueExclusive"/> 로 게시하고 완료를 <c>await</c> 한다.
/// </para>
/// <para>
/// <b>기준선은 같은 생산자 구조의 인라인 실행이다.</b> 같은 수의 스레드풀 태스크가
/// 같은 작업을 파티션 없이 실행한다. 두 측정의 차이가 곧 <b>배타성의 프레임당 가격</b>이다.
/// </para>
/// <para>
/// <b>한계.</b> ① 파티션 수 스윕은 코어 제한의 근사다(OS 는 모든 코어를 쓴다).
/// ② 게시자와 파티션 스레드가 함께 돌므로 파티션 수 P 에서 활성 스레드는 최대 2P 다 —
/// 물리 코어 수의 절반을 넘는 P 구간은 그 자체로 초과 구독이다. ③ 실제 핸들러는 I/O 를
/// 섞으므로 이 곡선은 순수 CPU 상한이다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
public class PartitionExclusiveBenchmarks
{
    private PartitionedExecutionModel _model = null!;
    private ExclusiveProbe[] _probes = null!;

    /// <summary>파티션 수. 판정은 물리 코어 수(12)까지만 본다.</summary>
    /// <remarks>
    /// 게시자+파티션 스레드가 쌍으로 돌므로 P=12 는 이미 논리 코어 24개를 채운다.
    /// 24 는 무너지는지만 본다.
    /// </remarks>
    [Params(1, 2, 4, 8, 12, 24)]
    public int PartitionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _model = new PartitionedExecutionModel(new PartitionedExecutionOptions
        {
            PartitionCount = PartitionCount,
            QueueCapacity = 1024,
        });

        _probes = new ExclusiveProbe[PartitionCount];
        for (int i = 0; i < PartitionCount; i++)
        {
            _probes[i] = new ExclusiveProbe(seed: i + 1);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _model.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>같은 생산자 구조로 파티션 없이 실행한 기준선.</summary>
    [Benchmark(Baseline = true, Description = "인라인 실행 (파티션 없음)")]
    public long InlineBaseline()
    {
        int frames = PartitionWorkload.TotalUnits / PartitionCount;
        Task<long>[] producers = new Task<long>[PartitionCount];

        for (int i = 0; i < PartitionCount; i++)
        {
            long seed = i + 1;
            producers[i] = Task.Run(() => PartitionWorkload.ExecuteUnits(frames, seed));
        }

        Task.WaitAll(producers);

        long total = 0;
        foreach (Task<long> producer in producers)
        {
            total += producer.Result;
        }

        return total;
    }

    /// <summary>프레임마다 배타 게시 → 완료 대기 왕복. 프로덕션 주 경로와 같은 구조.</summary>
    [Benchmark(Description = "프레임 단위 배타 왕복")]
    public long ExclusiveRoundTrips()
    {
        int frames = PartitionWorkload.TotalUnits / PartitionCount;
        Task<long>[] producers = new Task<long>[PartitionCount];

        for (int i = 0; i < PartitionCount; i++)
        {
            producers[i] = ProduceAsync(_model.GetPartition(i), _probes[i], frames);
        }

        // 벤치마크 본문에서의 블로킹은 허용된다 — 측정하려는 것이 벽시계 시간이다.
        Task.WaitAll(producers);

        long total = 0;
        foreach (Task<long> producer in producers)
        {
            total += producer.Result;
        }

        return total;
    }

    private static async Task<long> ProduceAsync(IExecutionPartition partition, ExclusiveProbe probe, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            // 읽기 루프와 같은 규율 — 프레임당 게시 1건, 완료까지 다음 게시 없음.
            await probe.RoundTripAsync(partition).ConfigureAwait(false);
        }

        return probe.Result;
    }

    /// <summary>
    /// <c>PartitionDispatchGate</c> 와 같은 구조의 측정용 게이트 — 재사용 가능한
    /// <see cref="IValueTaskSource"/> 로 프레임당 할당 0.
    /// </summary>
    /// <remarks>
    /// Hosting 의 실물 게이트는 internal 이고 디스패처·컨텍스트에 묶여 있어, 여기서는
    /// 같은 메커니즘(게시 → 파티션 실행 → 신호 → 재개)만 재현한다. 측정 대상은
    /// Hosting 이 아니라 <b>파티션의 배타 왕복 그 자체</b>다.
    /// </remarks>
    private sealed class ExclusiveProbe : IPartitionExclusiveWork, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _source;
        private long _acc;

        public ExclusiveProbe(long seed)
        {
            _acc = seed;

            // 게시자 재개를 파티션 스레드에서 인라인하지 않는다 — 실물 게이트와 같은 이유.
            _source.RunContinuationsAsynchronously = true;
        }

        public long Result => _acc;

        public ValueTask RoundTripAsync(IExecutionPartition partition)
        {
            _source.Reset();

            if (!partition.TryEnqueueExclusive(this))
            {
                throw new InvalidOperationException("파티션이 종료 중이다 — 벤치마크에서는 있을 수 없다.");
            }

            return new ValueTask(this, _source.Version);
        }

        /// <inheritdoc />
        public ValueTask ExecuteAsync()
        {
            // 프레임당 약 1µs 의 CPU 작업 — 실제 핸들러 규모(PartitionWorkload 문서 참조).
            _acc = PartitionWorkload.ExecuteUnit(_acc);
            _source.SetResult(true);
            return ValueTask.CompletedTask;
        }

        void IValueTaskSource.GetResult(short token) => _source.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _source.GetStatus(token);

        void IValueTaskSource.OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) => _source.OnCompleted(continuation, state, token, flags);
    }
}
