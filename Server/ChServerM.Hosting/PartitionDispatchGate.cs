using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using ChServerM.Dispatch;
using ChServerM.Execution;
using ChServerM.Framing;
using ChServerM.Time;

namespace ChServerM.Hosting;

/// <summary>
/// 프레임 하나의 디스패치를 파티션 배타 구간으로 넘기고 완료를 기다리는 게이트.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — ADR-0008 의 주 경로를 무할당으로 잇는 장치다.</b> 읽기 루프는 프레임마다
/// 이 게이트를 파티션에 게시하고(<see cref="IExecutionPartition.TryEnqueueExclusive"/>),
/// 파티션은 디스패치 완료까지 배타 구간을 유지하며, 게이트는
/// <see cref="IValueTaskSource{TResult}"/> 로 읽기 루프를 깨운다. 프레임마다 <c>Task</c> 를
/// 만들면 그 할당이 곧 GC 압력이므로, 커넥션당 1개를 만들어 재사용한다.
/// </para>
/// <para>
/// <b>수명·소유권 규약.</b> 읽기 루프(커넥션)가 소유한다. 커넥션당 프레임은 순차이므로
/// 동시 in-flight 는 항상 1건이다 — 그래서 <see cref="ManualResetValueTaskSourceCore{T}"/>
/// 하나로 충분하다. <see cref="DispatchExclusiveAsync"/> 를 겹쳐 부르면 안 된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="DispatchExclusiveAsync"/> 는 읽기 루프 스레드에서,
/// <see cref="ExecuteAsync"/> 는 파티션 스레드에서 호출된다. 프레임 데이터의 가시성은
/// 파티션 큐(채널)의 게시-소비가 보장한다.
/// </para>
/// <para>
/// <b>막고 있는 레거시 결함.</b> 페이로드 참조 절단(<see cref="MessageContext.EndFrame"/>)을
/// 완료 신호 <b>이전에</b> 반드시 수행한다 — 신호를 받은 읽기 루프가 <c>AdvanceTo</c> 로
/// 버퍼를 반납하기 때문이다. 순서가 바뀌면 해제된 메모리를 가리키는 참조가 남는다
/// (레거시 결함 원인 A: 수명 규약이 주석에만 있었다).
/// </para>
/// </remarks>
internal sealed class PartitionDispatchGate : IPartitionExclusiveWork, IValueTaskSource<DispatchStatus>, IThreadPoolWorkItem
{
    private readonly IMessageDispatcher _dispatcher;
    private readonly MessageContext _context;
    private ManualResetValueTaskSourceCore<DispatchStatus> _source;

    /// <summary>완료 신호로 넘길 결과. 완료 스레드가 쓰고 <see cref="IThreadPoolWorkItem.Execute"/>가 읽는다.</summary>
    /// <remarks>스레드풀 큐의 게시-소비가 가시성을 보장한다.</remarks>
    private DispatchStatus _pendingStatus;
    private Exception? _pendingException;

    /// <summary>커넥션당 하나 만든다.</summary>
    /// <param name="dispatcher">메시지 디스패처.</param>
    /// <param name="context">이 커넥션의 재사용 문맥.</param>
    public PartitionDispatchGate(IMessageDispatcher dispatcher, MessageContext context)
    {
        _dispatcher = dispatcher;
        _context = context;

        // 완료 신호를 받은 읽기 루프의 연속을 파티션 스레드에서 인라인 실행하지 않는다 —
        // 인라인하면 파티션 스레드가 읽기 루프의 다음 구간(AdvanceTo·다음 디코드)까지
        // 떠안아 배타 구간이 늘어난다. 다만 RunContinuationsAsynchronously=true 는
        // 델리게이트+상태를 감싸는 큐 항목을 프레임마다 할당하므로 쓰지 않는다
        // (ADR-0008 후속 실측의 할당 원인 1). 대신 게이트 자신이
        // IThreadPoolWorkItem 으로 큐에 들어가 SetResult 를 스레드풀에서 부른다 —
        // 같은 비동기 인계를 할당 0 으로 얻는다.
        _source.RunContinuationsAsynchronously = false;
    }

    /// <summary>프레임 하나를 파티션 배타 구간에서 디스패치하고 완료를 기다린다.</summary>
    /// <param name="partition">이 커넥션이 배정된 파티션.</param>
    /// <param name="decoded">디코드된 프레임. 완료까지 페이로드가 유효해야 한다.</param>
    /// <param name="timestamp">프레임 수신 시각.</param>
    /// <param name="token">커넥션 종료 토큰.</param>
    /// <returns>디스패치 결과. 파티션이 종료 중이면 <see cref="DispatchStatus.Canceled"/>.</returns>
    public ValueTask<DispatchStatus> DispatchExclusiveAsync(
        IExecutionPartition partition,
        in FrameDecodeResult decoded,
        MonotonicTimestamp timestamp,
        CancellationToken token)
    {
        _source.Reset();
        _context.BeginFrame(decoded.Envelope, decoded.Payload, timestamp, token);

        if (!partition.TryEnqueueExclusive(this))
        {
            // 파티션이 종료 중이다. 참조를 끊고 종료 경로로 보낸다 —
            // 읽기 루프가 Canceled 를 ShuttingDown 종료로 매핑한다.
            _context.EndFrame();
            return new ValueTask<DispatchStatus>(DispatchStatus.Canceled);
        }

        return new ValueTask<DispatchStatus>(this, _source.Version);
    }

    /// <inheritdoc />
    /// <remarks>파티션 스레드에서 호출된다. 예외를 밖으로 던지지 않는다(계약).</remarks>
    public ValueTask ExecuteAsync()
    {
        ValueTask<DispatchStatus> dispatch;
        try
        {
            dispatch = _dispatcher.DispatchAsync(_context);
        }
#pragma warning disable CA1031 // 계약: 오류는 파티션이 아니라 게시자(읽기 루프)에게 전달한다.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            CompleteFrame(exception, default);
            return ValueTask.CompletedTask;
        }

        if (dispatch.IsCompletedSuccessfully)
        {
            // 대부분의 프레임이 이 경로다 — 할당 0, 블로킹 0.
            CompleteFrame(null, dispatch.Result);
            return ValueTask.CompletedTask;
        }

        // 진짜 비동기 핸들러다. 상태 머신 할당은 이 경로에만 생긴다
        // (FramedConnectionHandler 의 "동기적으로 끝나는 핸들러 기준" 예외와 동일).
        return AwaitAndCompleteAsync(dispatch);
    }

    private async ValueTask AwaitAndCompleteAsync(ValueTask<DispatchStatus> dispatch)
    {
        try
        {
            DispatchStatus status = await dispatch.ConfigureAwait(false);
            CompleteFrame(null, status);
        }
#pragma warning disable CA1031 // 계약: 오류는 파티션이 아니라 게시자(읽기 루프)에게 전달한다.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            CompleteFrame(exception, default);
        }
    }

    private void CompleteFrame(Exception? exception, DispatchStatus status)
    {
        // 참조 절단이 신호보다 먼저다 — 신호를 받은 읽기 루프가 곧바로 AdvanceTo 한다.
        _context.EndFrame();

        _pendingStatus = status;
        _pendingException = exception;

        // 게이트 자신이 큐 항목이다 — 프레임당 할당 0 (타입 문서·생성자 주석 참조).
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
    }

    /// <inheritdoc />
    /// <remarks>스레드풀에서 완료를 신호한다. 읽기 루프의 연속이 여기서 인라인으로 이어진다.</remarks>
    void IThreadPoolWorkItem.Execute()
    {
        // 필드를 먼저 지역으로 옮긴다 — SetResult 의 인라인 연속(읽기 루프)이 같은 스택에서
        // 다음 프레임을 진행해 파티션이 필드를 덮어쓸 수 있다.
        DispatchStatus status = _pendingStatus;
        Exception? exception = _pendingException;
        _pendingException = null;

        if (exception is null)
        {
            _source.SetResult(status);
        }
        else
        {
            _source.SetException(exception);
        }
    }

    /// <inheritdoc />
    DispatchStatus IValueTaskSource<DispatchStatus>.GetResult(short token) => _source.GetResult(token);

    /// <inheritdoc />
    ValueTaskSourceStatus IValueTaskSource<DispatchStatus>.GetStatus(short token) => _source.GetStatus(token);

    /// <inheritdoc />
    void IValueTaskSource<DispatchStatus>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags) => _source.OnCompleted(continuation, state, token, flags);
}
