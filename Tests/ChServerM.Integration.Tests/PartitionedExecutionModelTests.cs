using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Diagnostics;
using ChServerM.Execution;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// ADR-0005 의 전제를 검증한다 — 같은 키는 같은 스레드로, 다른 키는 병렬로.
/// </summary>
/// <remarks>
/// 이 성질이 깨지면 "락 없이 순서 보장"이라는 주장 전체가 무효가 된다.
/// 동시성 버그는 단발 테스트로 재현되지 않으므로 반복 횟수를 크게 잡는다 (CLAUDE.md 9.9).
/// </remarks>
public sealed class PartitionedExecutionModelTests
{
    /// <summary>실행 스레드를 기록하는 작업.</summary>
    private readonly struct RecordWork(int value, ConcurrentQueue<(int Value, int ThreadId)> log)
        : IPartitionWork
    {
        public void Execute() => log.Enqueue((value, Environment.CurrentManagedThreadId));
    }

    /// <summary>카운트다운을 하나 줄이는 작업.</summary>
    private readonly struct SignalWork(CountdownEvent countdown) : IPartitionWork
    {
        public void Execute() => countdown.Signal();
    }

    /// <summary>예외를 던지는 작업. 소비 루프가 죽지 않는지 확인한다.</summary>
    private readonly struct FaultingWork : IPartitionWork
    {
        public void Execute() => throw new InvalidOperationException("의도적 실패");
    }

    /// <summary>아무것도 하지 않는 작업. 큐 용량만 확인할 때 쓴다.</summary>
    /// <remarks>
    /// 동기화 프리미티브를 붙잡지 않는다 — 실행 시점이 불확실한 작업이 <c>IDisposable</c>
    /// 을 참조하면 그 수명 관리가 곧 경합이 된다.
    /// </remarks>
    private readonly struct NoOpWork : IPartitionWork
    {
        public void Execute()
        {
            // 의도적으로 비어 있다.
        }
    }

    [Fact]
    public async Task PartitionCount_MatchesOptions()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 7 });

        Assert.Equal(7, model.PartitionCount);
    }

    [Fact]
    public async Task SameKey_AlwaysResolvesToTheSamePartition()
    {
        // 이 성질이 순서 보장의 전부다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 8 });

        PartitionKey key = PartitionKey.FromValue(0xDEAD_BEEF);
        IExecutionPartition first = model.GetPartition(key);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Same(first, model.GetPartition(PartitionKey.FromValue(0xDEAD_BEEF)));
        }
    }

    [Fact]
    public async Task Keys_SpreadAcrossAllPartitions()
    {
        // 한 파티션에 몰리면 나머지 코어가 논다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 8 });

        HashSet<int> touched = [];
        for (ulong i = 0; i < 10_000; i++)
        {
            touched.Add(model.GetPartition(PartitionKey.FromValue(i)).Index);
        }

        Assert.Equal(8, touched.Count);
    }

    [Fact]
    public async Task GetPartition_ByIndex_OutOfRange_Throws()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 4 });

        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetPartition(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetPartition(4));
    }

    [Fact]
    public async Task PostedWork_RunsOnASingleThreadPerPartition_AndInOrder()
    {
        // 순서와 단일 스레드 — 둘 다 깨지면 안 된다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 4 });

        IExecutionPartition partition = model.GetPartition(PartitionKey.FromValue(42));
        ConcurrentQueue<(int Value, int ThreadId)> log = new();

        const int WorkCount = 2000;
        for (int i = 0; i < WorkCount; i++)
        {
            Assert.True(partition.TryPost(new RecordWork(i, log)));
        }

        await WaitUntilAsync(() => log.Count == WorkCount);

        (int Value, int ThreadId)[] entries = [.. log];

        // 전부 같은 스레드에서 돌았어야 한다.
        Assert.Single(entries.Select(e => e.ThreadId).Distinct());

        // 넣은 순서 그대로 실행됐어야 한다.
        Assert.Equal(Enumerable.Range(0, WorkCount), entries.Select(e => e.Value));
    }

    [Fact]
    public async Task DifferentPartitions_RunOnDifferentThreads()
    {
        // 다른 키가 같은 스레드로 가면 병렬성이 없는 것이다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 4 });

        ConcurrentQueue<(int Value, int ThreadId)> log = new();

        for (int index = 0; index < 4; index++)
        {
            IExecutionPartition partition = model.GetPartition(index);
            Assert.True(partition.TryPost(new RecordWork(index, log)));
        }

        await WaitUntilAsync(() => log.Count == 4);

        Assert.Equal(4, log.Select(e => e.ThreadId).Distinct().Count());
    }

    [Fact]
    public async Task Scheduler_PinsTaskContinuationsToThePartitionThread()
    {
        // 주 경로 검증 — await 를 넘어가도 같은 스레드에 머물러야 한다.
        // 여기가 깨지면 "프레임당 큐 비용 0"이라는 설계가 성립하지 않는다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 2 });

        IExecutionPartition partition = model.GetPartition(PartitionKey.FromValue(1));
        int[] observed = new int[4];

        await Task.Factory.StartNew(
            async () =>
            {
                observed[0] = Environment.CurrentManagedThreadId;
                await Task.Yield();
                observed[1] = Environment.CurrentManagedThreadId;
                await Task.Yield();
                observed[2] = Environment.CurrentManagedThreadId;
                await Task.Delay(10).ConfigureAwait(true);
                observed[3] = Environment.CurrentManagedThreadId;
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            partition.Scheduler).Unwrap();

        Assert.Single(observed.Distinct());
    }

    [Fact]
    public async Task Scheduler_DoesNotUseThreadPoolThreads()
    {
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 2 });

        IExecutionPartition partition = model.GetPartition(0);

        bool onThreadPool = await Task.Factory.StartNew(
            () => Thread.CurrentThread.IsThreadPoolThread,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            partition.Scheduler);

        Assert.False(onThreadPool);
    }

    [Fact]
    public async Task QueueFull_RejectsInsteadOfGrowing()
    {
        // 거부가 붕괴보다 낫다 (CLAUDE.md 9.6). 무제한이면 부하 시 메모리로 죽는다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions
        {
            PartitionCount = 1,
            QueueCapacity = 16,
        });

        IExecutionPartition partition = model.GetPartition(0);

        // 파티션 스레드를 붙잡아 큐가 소비되지 않게 한다.
        //
        // 동기화 프리미티브의 수명에 주의한다. 대기 중인 스레드가 있는 상태에서
        // ManualResetEventSlim 을 Dispose 하면 동작이 정의되지 않는다 —
        // 내부 핸들이 사라져 프로세스가 통째로 죽을 수 있다.
        // 그래서 blocker 가 끝난 것을 확인한 뒤에야 스코프를 벗어난다.
        ManualResetEventSlim gate = new();
        ManualResetEventSlim started = new();
        Task blocker;

        try
        {
            blocker = Task.Factory.StartNew(
                () =>
                {
                    started.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                partition.Scheduler);

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "파티션 스레드가 시작되지 않았다.");

            int accepted = 0;
            int rejected = 0;

            for (int i = 0; i < 100; i++)
            {
                if (partition.TryPost(new NoOpWork()))
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                }
            }

            Assert.Equal(16, accepted);
            Assert.Equal(84, rejected);
            Assert.True(model.TotalRejectedCount >= 84);
        }
        finally
        {
            gate.Set();
        }

        // 대기 스레드가 빠져나온 뒤에 정리한다.
        await blocker;
        gate.Dispose();
        started.Dispose();
    }

    [Fact]
    public async Task FaultingWork_DoesNotKillThePartition()
    {
        // 레거시 ExecutableTaskDispatcherM 은 작업 예외 하나로 영구 정지했다 (CLAUDE.md 9.2).
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });

        IExecutionPartition partition = model.GetPartition(0);
        ConcurrentQueue<(int Value, int ThreadId)> log = new();

        for (int i = 0; i < 50; i++)
        {
            Assert.True(partition.TryPost(new FaultingWork()));
        }

        // 예외 뒤에도 정상 작업이 처리되어야 한다.
        Assert.True(partition.TryPost(new RecordWork(1, log)));

        await WaitUntilAsync(() => log.Count == 1);
    }

    [Fact]
    public async Task FaultingWork_ReleasesTheQueueSlot()
    {
        // finally 로 카운터를 복원하지 않으면 파티션이 영구히 "가득 찬" 상태가 된다.
        await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions
        {
            PartitionCount = 1,
            QueueCapacity = 8,
        });

        IExecutionPartition partition = model.GetPartition(0);

        // 용량의 몇 배를 예외 작업으로 밀어 넣는다. 슬롯이 반환되지 않으면 곧 거부된다.
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 8; i++)
            {
                partition.TryPost(new FaultingWork());
            }

            await WaitUntilAsync(() => ((ExecutionPartition)partition).PendingExternalWork == 0);
        }

        Assert.Equal(0, ((ExecutionPartition)partition).PendingExternalWork);
        Assert.True(partition.TryPost(new FaultingWork()));
    }

    [Fact]
    public async Task ConcurrentProducers_AllWorkExecutesExactlyOnce()
    {
        // 반복 "실행"이어야 한다(CLAUDE.md 9.9) — 실행 안의 대량 반복은 인터리빙 기회가
        // 실행당 한 번뿐이다(2026-08-04 감사). 모델 생성·소멸까지 포함해 반복해야
        // 시작·종료 경로의 경합도 함께 노린다.
        for (int iteration = 0; iteration < 10; iteration++)
        {
            await using PartitionedExecutionModel model = new(new PartitionedExecutionOptions
            {
                PartitionCount = 4,
                QueueCapacity = 100_000,
            });

            const int ProducerCount = 8;
            const int PerProducer = 2000;
            const int Total = ProducerCount * PerProducer;

            using CountdownEvent completed = new(Total);
            Task[] producers = new Task[ProducerCount];

            for (int p = 0; p < ProducerCount; p++)
            {
                int producer = p;
                producers[p] = Task.Run(() =>
                {
                    for (int i = 0; i < PerProducer; i++)
                    {
                        PartitionKey key = PartitionKey.FromValue((ulong)((producer * PerProducer) + i));
                        IExecutionPartition partition = model.GetPartition(key);

                        while (!partition.TryPost(new SignalWork(completed)))
                        {
                            Thread.SpinWait(50);
                        }
                    }
                });
            }

            await Task.WhenAll(producers);

            Assert.True(
                completed.Wait(TimeSpan.FromSeconds(30)),
                $"반복 {iteration}: 모든 작업이 실행되지 않았다.");
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 2 });

        await model.DisposeAsync();
        await model.DisposeAsync();
    }

    [Fact]
    public async Task TryPost_AfterDispose_ReturnsFalse()
    {
        // 종료 후 게시를 조용히 받아버리면 그 작업은 영원히 실행되지 않는다.
        PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });
        IExecutionPartition partition = model.GetPartition(0);

        await model.DisposeAsync();

        Assert.False(partition.TryPost(new NoOpWork()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PartitionedExecutionOptions.AbsoluteMaxPartitionCount + 1)]
    public void InvalidPartitionCount_Throws(int partitionCount)
    {
        Assert.Throws<InvalidOperationException>(
            () => new PartitionedExecutionModel(new PartitionedExecutionOptions { PartitionCount = partitionCount }));
    }

    [Fact]
    public void InvalidQueueCapacity_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => new PartitionedExecutionModel(new PartitionedExecutionOptions { QueueCapacity = 0 }));
    }

    [Fact]
    public async Task QueueFull_EmitsPartitionWorkRejectedMetric()
    {
        // 유계 큐 포화(백프레셔)는 메트릭으로 관측돼야 한다. 카운터가 없으면 거부가
        // 조용한 유실이 된다 — 대시보드가 이 값을 보고 용량 부족을 안다(9.6).
        RecordingMetricsSink sink = new();
        await using PartitionedExecutionModel model = new(
            new PartitionedExecutionOptions { PartitionCount = 1, QueueCapacity = 16 },
            logger: null,
            metricsSink: sink);

        IExecutionPartition partition = model.GetPartition(0);

        // 소비 스레드를 붙잡아 큐가 비워지지 않게 한다(수명 규약은 아래 finally 참조).
        ManualResetEventSlim gate = new();
        ManualResetEventSlim started = new();
        Task blocker;

        try
        {
            blocker = Task.Factory.StartNew(
                () =>
                {
                    started.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                partition.Scheduler);

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "파티션 스레드가 시작되지 않았다.");

            int rejected = 0;
            for (int i = 0; i < 100; i++)
            {
                if (!partition.TryPost(new NoOpWork()))
                {
                    rejected++;
                }
            }

            Assert.Equal(84, rejected);

            // 방출된 카운터가 실제 거부 수와 일치하고, 파티션 태그가 붙는다.
            Assert.Equal(rejected, sink.CounterValue(MetricNames.PartitionWorkRejected));
            Assert.Equal(model.TotalRejectedCount, sink.CounterValue(MetricNames.PartitionWorkRejected));
            Assert.Equal("0", sink.LastPartitionTag);
        }
        finally
        {
            gate.Set();
        }

        await blocker;
        gate.Dispose();
        started.Dispose();
    }

    [Fact]
    public async Task QueueDepth_GaugeRisesWhilePendingAndReturnsToZero()
    {
        // 게이지는 게시(+1)와 완료(-1)가 대칭이어야 한다 — 어긋나면 큐 깊이가 실제와
        // 달라져 관측이 거짓말을 한다. 소비를 막아 쌓인 깊이를 관측하고, 드레인 후 0 을 확인한다.
        RecordingMetricsSink sink = new();
        await using PartitionedExecutionModel model = new(
            new PartitionedExecutionOptions { PartitionCount = 1, QueueCapacity = 32 },
            logger: null,
            metricsSink: sink);

        IExecutionPartition partition = model.GetPartition(0);

        ManualResetEventSlim gate = new();
        ManualResetEventSlim started = new();
        Task blocker;

        try
        {
            blocker = Task.Factory.StartNew(
                () =>
                {
                    started.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                partition.Scheduler);

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "파티션 스레드가 시작되지 않았다.");

            for (int i = 0; i < 10; i++)
            {
                Assert.True(partition.TryPost(new NoOpWork()));
            }

            // 소비가 막혀 있으므로 게시한 10건이 그대로 큐에 남아 있다.
            Assert.Equal(10, sink.QueueDepth);
        }
        finally
        {
            gate.Set();
        }

        await blocker;

        // 드레인 후 게이지는 0 으로 돌아온다. 최고점은 10 이었다(전부 막힌 동안 게시됨).
        await WaitUntilAsync(() => sink.QueueDepth == 0);
        Assert.Equal(10, sink.QueueDepthPeak);

        gate.Dispose();
        started.Dispose();
    }

    /// <summary>방출된 메트릭을 집계해 검증하는 <see cref="IMetricsSink"/>.</summary>
    /// <remarks>
    /// 파티션 스레드들이 동시에 호출하므로 스레드 안전해야 한다(<see cref="IMetricsSink"/> 규약).
    /// 게이지는 순증감 합과 최고점을 함께 기록해 대칭성과 피크를 모두 검증한다.
    /// </remarks>
    private sealed class RecordingMetricsSink : IMetricsSink
    {
        private readonly ConcurrentDictionary<string, long> _counters = new();
        private long _queueDepth;
        private long _queueDepthPeak;
        private string? _lastPartitionTag;

        public long QueueDepth => Interlocked.Read(ref _queueDepth);

        public long QueueDepthPeak => Interlocked.Read(ref _queueDepthPeak);

        public string? LastPartitionTag => Volatile.Read(ref _lastPartitionTag);

        public long CounterValue(string name) => _counters.TryGetValue(name, out long value) ? value : 0;

        public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            _counters.AddOrUpdate(name, delta, (_, current) => current + delta);
            CapturePartitionTag(tags);
        }

        public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
        {
            // 이 테스트는 히스토그램을 검증하지 않는다.
        }

        public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            long updated = Interlocked.Add(ref _queueDepth, delta);

            // 최고점 CAS — 여러 파티션 스레드가 동시에 갱신해도 최댓값을 놓치지 않는다.
            long peak = Interlocked.Read(ref _queueDepthPeak);
            while (updated > peak)
            {
                long previous = Interlocked.CompareExchange(ref _queueDepthPeak, updated, peak);
                if (previous == peak)
                {
                    break;
                }

                peak = previous;
            }

            CapturePartitionTag(tags);
        }

        private void CapturePartitionTag(ReadOnlySpan<MetricTag> tags)
        {
            foreach (MetricTag tag in tags)
            {
                if (string.Equals(tag.Name, TagNames.Partition, StringComparison.Ordinal))
                {
                    Volatile.Write(ref _lastPartitionTag, tag.Value);
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("조건이 제한 시간 안에 만족되지 않았다.");
            }

            await Task.Delay(5);
        }
    }
}
