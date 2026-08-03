using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Execution;

namespace ChServerM.Concurrency;

/// <summary>
/// 전용 스레드 하나가 소비하는 실행 파티션.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0005 의 핵심 — 같은 키의 작업이 항상 이 하나의 스레드로 오므로
/// <b>락 없이 순서가 보장</b>되고, 다른 키는 다른 파티션에서 완전히 독립적으로 돈다.
/// 여기 안에서 도는 코드는 동기화가 필요 없다는 것을 <b>계약으로 보장받는다.</b>
/// </para>
/// <para>
/// <b>큐가 하나인 이유.</b> 스케줄러로 들어오는 연속(continuation)과
/// <see cref="TryPost{TWork}"/> 로 들어오는 외부 작업이 같은 FIFO 를 공유해야
/// 둘 사이의 순서도 보장된다. 큐를 나누면 "타이머 작업이 진행 중인 메시지보다
/// 먼저 실행되는" 일이 생긴다.
/// </para>
/// <para>
/// <b>큐는 무제한인데 왜 유계 규약(9.6)을 지키는가.</b> 채널 자체는 무제한이고,
/// 대신 <b>외부 유입만 카운터로 막는다.</b> 이유는 둘이다.
/// </para>
/// <list type="number">
///   <item><description>
///     <b>연속은 버릴 수 없다.</b> 스케줄된 <see cref="Task"/> 를 거부하면 그것을
///     <c>await</c> 하던 코드가 <b>영원히 깨어나지 못한다.</b> 커넥션 하나가 조용히 멈춘다
///   </description></item>
///   <item><description>
///     <b>연속은 이미 승인된 작업이다.</b> 진입 통제는 커넥션 수락과
///     <see cref="TryPost{TWork}"/> 에서 이미 이뤄졌다. 연속의 개수는 그 결과이지
///     새로운 유입이 아니다
///   </description></item>
/// </list>
/// <para>
/// 그래서 <b>거부는 진짜 유입 지점에서만</b> 한다. 그것이 유계 규약의 의도이기도 하다.
/// </para>
/// <para>
/// <b>전용 스레드에서 블로킹하는 것은 허용된다.</b> CLAUDE.md 9.5 가 금지하는 것은
/// <b>스레드풀 스레드</b>를 블로킹해 고갈시키는 것이다. 이 스레드는 이 파티션만을 위해
/// 존재하므로, 큐가 빌 때 여기서 대기하는 것이 정확히 의도된 동작이다.
/// </para>
/// <para>
/// <b>항목별 <c>try/catch</c>.</b> 작업 하나의 예외로 소비 루프가 죽으면 그 파티션에
/// 묶인 모든 커넥션이 함께 멈춘다 — 레거시 <c>ExecutableTaskDispatcherM</c> 이
/// 정확히 그렇게 영구 정지했다 (CLAUDE.md 9.2).
/// </para>
/// </remarks>
public sealed class ExecutionPartition : IExecutionPartition, IDisposable
{
    private static readonly EventId WorkFaultedEvent = new(5010, "PartitionWorkFaulted");
    private static readonly EventId WorkRejectedEvent = new(5000, "PartitionWorkRejected");

    private readonly Channel<object> _queue;
    private readonly ChannelWriter<object> _writer;
    private readonly ChannelReader<object> _reader;
    private readonly PartitionTaskScheduler _scheduler;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stopping = new();
    private readonly int _queueCapacity;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IServerLogger _logger;
    private int _disposed;

    /// <summary>외부에서 들어와 아직 처리되지 않은 작업 수.</summary>
    /// <remarks>
    /// <b>캐시 라인 패딩을 둔다.</b> 파티션마다 이 카운터를 자주 갱신하는데, 여러
    /// 파티션의 카운터가 같은 64바이트 라인에 있으면 서로의 캐시를 무효화한다
    /// (false sharing, CLAUDE.md 9.4). 파티션은 각각 별도 객체이므로 대개 떨어지지만,
    /// GC 가 인접 배치할 수 있으므로 보장하지 않는다.
    /// </remarks>
    private PaddedCounter _pendingExternalWork;

    private long _executedCount;
    private long _rejectedCount;

    internal ExecutionPartition(int index, PartitionedExecutionOptions options, IServerLogger logger)
    {
        Index = index;
        _queueCapacity = options.QueueCapacity;
        _shutdownTimeout = options.ShutdownTimeout;
        _logger = logger;

        // 단일 소비자다. 채널에 알려주면 내부 동기화가 줄어든다.
        _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _writer = _queue.Writer;
        _reader = _queue.Reader;
        _scheduler = new PartitionTaskScheduler(this);

        _thread = new Thread(ConsumeLoop)
        {
            // 백그라운드 스레드다. 이것을 빠뜨리면 프로세스가 종료되지 않는다.
            IsBackground = true,
            Name = $"{options.ThreadNamePrefix}-{index}",
            Priority = options.ThreadPriority,
        };
    }

    /// <inheritdoc />
    public int Index { get; }

    /// <inheritdoc />
    public TaskScheduler Scheduler => _scheduler;

    /// <summary>이 파티션이 지금까지 실행한 작업 수.</summary>
    /// <remarks>진단·테스트용이다. 메트릭은 Phase 11 에서 정식으로 붙인다.</remarks>
    public long ExecutedCount => Interlocked.Read(ref _executedCount);

    /// <summary>큐 포화로 거부한 작업 수.</summary>
    /// <remarks>
    /// <b>이 값이 0이 아니면 용량이 부족한 것이다.</b> 조용한 유실을 만들지 않기 위해
    /// 반드시 노출한다 — 레거시는 거부를 세지도 기록하지도 않았다.
    /// </remarks>
    public long RejectedCount => Interlocked.Read(ref _rejectedCount);

    /// <summary>외부에서 들어와 아직 처리되지 않은 작업 수.</summary>
    public int PendingExternalWork => Volatile.Read(ref _pendingExternalWork.Value);

    /// <summary>이 스레드가 이 파티션의 소비자인지 여부.</summary>
    internal bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    internal void Start() => _thread.Start();

    /// <inheritdoc />
    public bool TryPost<TWork>(in TWork work) where TWork : struct, IPartitionWork
    {
        // 유입 통제는 여기서만 한다. 상세 이유는 타입 문서 참조.
        if (Volatile.Read(ref _pendingExternalWork.Value) >= _queueCapacity)
        {
            Interlocked.Increment(ref _rejectedCount);
            LogRejected();
            return false;
        }

        // 박스를 풀에서 빌린다. struct 를 그대로 채널(object)에 넣으면 매번 박싱된다.
        WorkBox<TWork> box = WorkBoxPool<TWork>.Rent();
        box.Set(work);

        Interlocked.Increment(ref _pendingExternalWork.Value);

        if (_writer.TryWrite(box))
        {
            return true;
        }

        // 채널이 닫혔다(종료 중). 카운터와 박스를 반드시 되돌린다 —
        // 빠뜨리면 파티션이 영구히 "가득 찬" 상태가 된다 (CLAUDE.md 9.2).
        Interlocked.Decrement(ref _pendingExternalWork.Value);
        WorkBoxPool<TWork>.Return(box);
        return false;
    }

    /// <summary>스케줄러가 만든 <see cref="Task"/> 를 큐에 넣는다.</summary>
    /// <remarks>
    /// <b>거부하지 않는다.</b> 거부하면 그 태스크를 <c>await</c> 하던 코드가
    /// 영원히 깨어나지 못한다. 상세 이유는 타입 문서 참조.
    /// </remarks>
    internal void Enqueue(Task task)
    {
        if (_writer.TryWrite(task))
        {
            return;
        }

        // 종료 중이라 큐가 닫혔다. 태스크를 버리면 대기자가 매달리므로,
        // 스레드풀에서라도 반드시 실행한다.
        _ = Task.Run(() => _scheduler.ExecuteOutsidePartition(task));
    }

    /// <summary>소비 루프에 종료를 알린다. 기다리지 않는다.</summary>
    /// <remarks>
    /// <b>대기와 분리한 이유.</b> 파티션마다 "알리고 기다리기"를 순차로 하면 최악의
    /// 종료 시간이 <c>파티션 수 × 제한 시간</c>이 된다. 64 파티션 × 5초 = 5분 20초다.
    /// 전부에게 먼저 알린 뒤 함께 기다리면 제한 시간 한 번이면 된다.
    /// </remarks>
    internal void SignalStop()
    {
        _writer.TryComplete();

        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 정리됐다.
        }
    }

    /// <summary>파티션을 멈추고 스레드가 끝나기를 기다린다.</summary>
    /// <remarks>
    /// 여러 번 불러도 안전하다. 여러 파티션을 함께 멈출 때는 전부에게
    /// <see cref="SignalStop"/> 을 먼저 보낸 뒤 각각을 정리한다 — 그러지 않으면
    /// 최악의 종료 시간이 <c>파티션 수 × 제한 시간</c>이 된다.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SignalStop();

        if (_thread.IsAlive && !_thread.Join(_shutdownTimeout))
        {
            // 조인 실패는 작업 하나가 블로킹하고 있다는 뜻이다. 백그라운드 스레드이므로
            // 프로세스 종료를 막지는 않지만, 원인을 알 수 있게 반드시 기록한다.
            LogShutdownTimeout(_shutdownTimeout);
        }

        _stopping.Dispose();
    }

    private void ConsumeLoop()
    {
        while (true)
        {
            if (_reader.TryRead(out object? item))
            {
                Execute(item);
                continue;
            }

            // 큐가 비었다. 여기서 블로킹하는 것은 의도된 설계다 —
            // 이 스레드는 이 파티션 전용이고, 스레드풀과 무관하다.
            if (!WaitForWork())
            {
                return;
            }
        }
    }

    /// <summary>다음 작업이 올 때까지 기다린다.</summary>
    /// <returns>계속 돌아야 하면 <see langword="true"/>, 종료해야 하면 <see langword="false"/>.</returns>
    private bool WaitForWork()
    {
        try
        {
            ValueTask<bool> wait = _reader.WaitToReadAsync(_stopping.Token);

            // 대부분의 경우 이미 완료돼 있다(다른 스레드가 방금 넣었다).
            return wait.IsCompletedSuccessfully
                ? wait.Result
                : wait.AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            // 종료 중에 토큰 원본이 정리됐다. 멈추라는 뜻이다.
            return false;
        }
    }

    private void Execute(object item)
    {
        // 항목별 try/catch. 나쁜 항목 하나가 이 파티션의 모든 커넥션을 죽이지 않게 한다.
        try
        {
            if (item is Task task)
            {
                _scheduler.ExecuteInPartition(task);
                return;
            }

            IPartitionWork work = (IPartitionWork)item;

            try
            {
                work.Execute();
            }
            finally
            {
                // 실행이 실패해도 카운터와 박스는 반드시 되돌린다.
                // 이것을 빠뜨리면 예외 하나가 파티션을 영구 정지시킨다 (CLAUDE.md 9.2).
                Interlocked.Decrement(ref _pendingExternalWork.Value);
                (work as IReturnableWorkBox)?.Return();
            }
        }
#pragma warning disable CA1031 // 소비 루프가 죽으면 이 파티션의 모든 커넥션이 멈춘다.
        catch (Exception exception)
        {
            LogWorkFaulted(exception);
        }
#pragma warning restore CA1031
        finally
        {
            Interlocked.Increment(ref _executedCount);
        }
    }

    private void LogRejected()
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                WorkRejectedEvent,
                (Partition: Index, Capacity: _queueCapacity),
                null,
                static (state, _) =>
                    $"파티션 {state.Partition} 큐가 가득 찼다(용량 {state.Capacity}). 작업을 거부한다.");
        }
    }

    private void LogWorkFaulted(Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(
                LogLevel.Error,
                WorkFaultedEvent,
                Index,
                exception,
                static (index, ex) => $"파티션 {index} 작업이 예외를 던졌다: {ex?.Message}");
        }
    }

    private void LogShutdownTimeout(TimeSpan timeout)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                WorkFaultedEvent,
                (Partition: Index, Timeout: timeout),
                null,
                static (state, _) =>
                    $"파티션 {state.Partition} 스레드가 {state.Timeout} 안에 끝나지 않았다. " +
                    $"작업 하나가 블로킹하고 있을 가능성이 높다.");
        }
    }

    /// <summary>false sharing 을 피하기 위해 캐시 라인 하나를 통째로 차지하는 카운터.</summary>
    /// <remarks>
    /// 일반적인 캐시 라인은 64바이트지만, 일부 x86 프로세서는 인접 라인을 함께
    /// 프리페치한다. 128바이트로 잡아 그 경우까지 막는다.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(64)]
        public int Value;
    }
}

/// <summary>풀로 돌려줄 수 있는 작업 박스.</summary>
internal interface IReturnableWorkBox
{
    void Return();
}

/// <summary>
/// 구조체 작업을 채널에 넣기 위해 감싸는 재사용 가능한 상자.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="IExecutionPartition.TryPost{TWork}"/> 는 박싱을 피하려고
/// <c>struct</c> 로 제약돼 있는데, 채널에 넣으려면 참조 타입이어야 한다. 그대로 넣으면
/// 게시할 때마다 박싱이 생긴다 — 초당 수십만 건이면 그것이 그대로 GC 압력이다.
/// </para>
/// <para>
/// 상자를 풀링하면 <b>정상 상태에서 게시당 할당 0</b>이 된다.
/// </para>
/// </remarks>
internal sealed class WorkBox<TWork> : IPartitionWork, IReturnableWorkBox
    where TWork : struct, IPartitionWork
{
    private TWork _work;

    public void Set(in TWork work) => _work = work;

    public void Execute() => _work.Execute();

    public void Return()
    {
        // 참조를 붙들고 있으면 그것이 곧 누수다.
        _work = default;
        WorkBoxPool<TWork>.Return(this);
    }
}

/// <summary>
/// 작업 타입별 상자 풀.
/// </summary>
/// <remarks>
/// <para>
/// 정적 제네릭 클래스라 타입마다 별도 저장소를 갖는다 — 조회 비용이 없다.
/// </para>
/// <para>
/// <b>파티션 간 공유다.</b> 여러 파티션이 같은 작업 타입을 쓰면 이 큐에서 경합한다.
/// <see cref="IExecutionPartition.TryPost{TWork}"/> 는 <b>보조 경로</b>이고
/// (주 경로는 스케줄러) 게시 빈도가 낮다는 전제 위의 선택이다.
/// 경합이 문제가 되면 파티션별 풀로 바꾼다 — 그때는 측정 결과를 근거로 남긴다.
/// </para>
/// <para>
/// <b>상한을 둔다.</b> 상한 없는 풀은 최대 부하 시점의 메모리를 영원히 붙든다 —
/// 레거시의 무제한 풀이 정확히 그랬다.
/// </para>
/// </remarks>
internal static class WorkBoxPool<TWork> where TWork : struct, IPartitionWork
{
    private const int MaxPooled = 1024;

    private static readonly ConcurrentQueue<WorkBox<TWork>> Pool = new();
    private static int _pooledCount;

    public static WorkBox<TWork> Rent()
    {
        if (Pool.TryDequeue(out WorkBox<TWork>? box))
        {
            Interlocked.Decrement(ref _pooledCount);
            return box;
        }

        return new WorkBox<TWork>();
    }

    public static void Return(WorkBox<TWork> box)
    {
        if (Interlocked.Increment(ref _pooledCount) > MaxPooled)
        {
            // 상한을 넘었다. 반납하지 않고 GC 에 맡긴다.
            Interlocked.Decrement(ref _pooledCount);
            return;
        }

        Pool.Enqueue(box);
    }
}
