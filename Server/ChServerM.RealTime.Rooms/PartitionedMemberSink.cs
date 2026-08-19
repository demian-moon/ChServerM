using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Execution;
using ChServerM.Identity;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 기본 멤버 싱크 — 브로드캐스트 프레임을 <b>그 커넥션의 파티션 배타 슬롯</b>에서 커넥션에 쓴다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 브로드캐스트의 소유권 문제를 푼다.</b> 커넥션 <c>Output</c>은 단일
/// 라이터 규약이고, 그 규약은 "커넥션당 in-flight 디스패치 1건"이라는 사실이 지켜 왔다.
/// 브로드캐스트는 정의상 <b>남의 커넥션에 쓰는 행위</b>라 이 규약을 정면으로 깬다.
/// 이 싱크는 쓰기를 커넥션이 속한 파티션의 배타 작업으로 옮긴다 — 핸들러 응답과
/// 브로드캐스트 쓰기가 <b>같은 배타 큐에서 직렬화</b>되므로 프레임이 섞일 수 없다(ADR-0064).
/// </para>
/// <para>
/// <b>왜 전용 소비자 태스크(ClusterPeerSet 방식)가 아닌가.</b> 그 방식은 피어 몇 개에는
/// 맞지만 클라이언트 커넥션 1만 개면 태스크 1만 개다. 파티션 배타 슬롯은 이미 있는 실행
/// 구조를 재사용하고, 스레드·태스크를 하나도 더 만들지 않는다.
/// </para>
/// <para>
/// <b>유계·거부 규약.</b> 싱크당 송신 큐는 유계다(<see cref="PartitionedMemberSinkOptions.SendQueueDepth"/>).
/// 포화 시 <see cref="RoomDeliveryStatus.QueueFull"/>로 <b>거부</b>한다 — 느린 수신자가
/// 브로드캐스터(그리고 다른 모든 멤버)를 막는 것보다 그 멤버의 프레임을 버리고 관측하는
/// 편이 낫다(9.6). <c>TryWrite</c>의 <see langword="false"/>를 버리지 않고 상태로 돌려준다 —
/// 레거시가 이 조합으로 패킷을 조용히 유실한 바로 그 지점이다.
/// </para>
/// <para>
/// <b>실패 규약(ADR-0051).</b> <c>FlushResult</c> 의 <c>IsCompleted</c>/<c>IsCanceled</c>를
/// 반드시 검사한다 — 버리면 죽은 커넥션이 살아 있는 척하며 이후 프레임을 전부 삼킨다.
/// 실패한 싱크는 <see cref="IsFaulted"/>가 되어 이후 전달을 <see cref="RoomDeliveryStatus.Closed"/>로
/// 거부하고, 잔여 프레임을 해제한다. 룸에서의 퇴장은 앱의 몫이다
/// (<see cref="PartitionedMemberSinkOptions.OnDeliveryFaulted"/> 콜백이 그 신호다).
/// </para>
/// <para><b>스레드 규약.</b> <see cref="TryDeliver"/>는 아무 스레드에서나 안전하다.</para>
/// </remarks>
public sealed class PartitionedMemberSink : IRoomMemberSink, IPartitionExclusiveWork
{
    private readonly IConnection _connection;
    private readonly IExecutionPartition _partition;
    private readonly IServerLogger _logger;
    private readonly IMetricsSink? _metrics;
    private readonly Action<ConnectionId>? _onDeliveryFaulted;
    private readonly Channel<BroadcastFrame> _queue;

    private int _drainScheduled;
    private volatile bool _faulted;

    /// <summary>싱크를 만든다. 커넥션당 하나를 만들어 커넥션 수명 동안 재사용한다.</summary>
    /// <param name="connection">대상 커넥션.</param>
    /// <param name="partition">
    /// 그 커넥션의 프레임 디스패치가 도는 파티션 —
    /// <c>executionModel.GetPartition(connection.Id.ToPartitionKey())</c>. 다른 파티션을 주면
    /// 배타성이 커넥션 디스패치와 직렬화되지 않아 규약이 깨진다.
    /// </param>
    /// <param name="options">추가 설정. <see langword="null"/>이면 기본값.</param>
    public PartitionedMemberSink(
        IConnection connection,
        IExecutionPartition partition,
        PartitionedMemberSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(partition);
        options?.Validate();

        _connection = connection;
        _partition = partition;
        _logger = options?.Logger ?? NullServerLogger.Instance;
        _metrics = options?.MetricsSink;
        _onDeliveryFaulted = options?.OnDeliveryFaulted;

        _queue = Channel.CreateBounded<BroadcastFrame>(new BoundedChannelOptions(
            options?.SendQueueDepth ?? PartitionedMemberSinkOptions.DefaultSendQueueDepth)
        {
            // 드레인은 배타 슬롯 하나에서만 돌지만, 사망 정리 경로가 다른 스레드에서 잔여를
            // 비울 수 있어 SingleReader 최적화는 걸지 않는다 — 안전이 힌트보다 먼저다.
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public ConnectionId ConnectionId => _connection.Id;

    /// <summary>송신 실패로 사망했는지 여부. 사망한 싱크는 되살아나지 않는다 — 커넥션과 함께 버린다.</summary>
    public bool IsFaulted => _faulted;

    /// <inheritdoc />
    public RoomDeliveryStatus TryDeliver(BroadcastFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_faulted || _connection.ConnectionClosed.IsCancellationRequested)
        {
            return RoomDeliveryStatus.Closed;
        }

        if (!_queue.Writer.TryWrite(frame))
        {
            // 유계 큐 포화 — TryWrite 의 false 를 버리지 않는다(9.6). 소유권은 호출자에 남는다.
            // 이름이 FramesRejected 가 아닌 이유: 거부 집계는 브로드캐스터 한 곳의 책임이다 —
            // 여기서도 같은 이름으로 세면 거부 1건이 2로 집계된다(감사 2026-08-18 R-7).
            _metrics?.Count(RoomMetricNames.SinkQueueFull, 1, default);
            return _faulted ? RoomDeliveryStatus.Closed : RoomDeliveryStatus.QueueFull;
        }

        ScheduleDrain();
        return RoomDeliveryStatus.Accepted;
    }

    /// <summary>파티션 배타 슬롯 진입점. 직접 호출하지 않는다.</summary>
    ValueTask IPartitionExclusiveWork.ExecuteAsync() => DrainAsync();

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
        {
            return; // 이미 예약돼 있다 — 그 드레인이 방금 넣은 항목까지 본다.
        }

        if (!_partition.TryEnqueueExclusive(this))
        {
            // 파티션이 종료 중이다. 이 싱크로는 더 보낼 수 없다.
            MarkFaulted(null);
            Volatile.Write(ref _drainScheduled, 0);
        }
    }

    private async ValueTask DrainAsync()
    {
        try
        {
            int drained = 0;
            while (_queue.Reader.TryRead(out BroadcastFrame? frame))
            {
                if (_faulted)
                {
                    frame.Release();
                    continue;
                }

                try
                {
                    // 배타 슬롯 안이므로 커넥션 Output 의 단일 라이터 규약이 성립한다.
                    _connection.Output.Write(frame.Written.Span);
                    drained++;
                }
#pragma warning disable CA1031 // 닫힌 파이프에의 쓰기는 무엇을 던지든 결론이 "송신 불가" 하나다.
                catch (Exception exception)
                {
                    MarkFaulted(exception);
                }
#pragma warning restore CA1031
                finally
                {
                    frame.Release();
                }
            }

            if (drained > 0 && !_faulted)
            {
                await FlushAsync(drained).ConfigureAwait(false);
            }
        }
        finally
        {
            // 잃어버린 깨움 방지: 0 을 쓴 뒤에 남은 항목이 보이면 다시 예약한다(9.2 — finally 복원).
            Volatile.Write(ref _drainScheduled, 0);
            if (_queue.Reader.TryPeek(out _))
            {
                ScheduleDrain();
            }
        }
    }

    private async ValueTask FlushAsync(int frameCount)
    {
        try
        {
            // 배치당 플러시 1회 — 프레임마다 플러시하면 syscall 이 프레임 수만큼 는다.
            FlushResult result = await _connection.Output
                .FlushAsync(_connection.ConnectionClosed)
                .ConfigureAwait(false);

            if (result.IsCompleted || result.IsCanceled)
            {
                // ADR-0051: 이 결과를 버리면 죽은 커넥션이 살아 있는 척한다.
                MarkFaulted(null);
                return;
            }

            _metrics?.Count(RoomMetricNames.FramesDelivered, frameCount, default);
        }
#pragma warning disable CA1031 // 종료 경로의 예외는 "송신 불가"라는 같은 결론으로 접는다 (ADR-0057 실측 교훈).
        catch (Exception exception)
        {
            MarkFaulted(exception);
        }
#pragma warning restore CA1031
    }

    private void MarkFaulted(Exception? exception)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        _queue.Writer.TryComplete();

        // 잔여 프레임의 참조를 놓는다 — 버려진 큐가 풀 버퍼를 영원히 붙들지 않게.
        while (_queue.Reader.TryRead(out BroadcastFrame? leftover))
        {
            leftover.Release();
        }

        _metrics?.Count(RoomMetricNames.SinkFaults, 1, default);
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                RoomEvents.SinkFaulted,
                _connection.Id,
                exception,
                static (id, ex) => $"{id} 브로드캐스트 싱크가 사망했다: {ex?.Message ?? "상대가 닫혔다"}");
        }

        _onDeliveryFaulted?.Invoke(_connection.Id);
    }
}

/// <summary>
/// <see cref="PartitionedMemberSink"/>의 설정.
/// </summary>
public sealed class PartitionedMemberSinkOptions
{
    /// <summary>기본 송신 큐 깊이. 64.</summary>
    public const int DefaultSendQueueDepth = 64;

    /// <summary>싱크당 송신 큐 깊이. 초과분은 거부된다.</summary>
    /// <remarks>최악 미처리 프레임 참조가 <b>이 값 × 멤버 수</b>다 — 풀 크기 계산의 입력이다(ADR-0051).</remarks>
    public int SendQueueDepth { get; set; } = DefaultSendQueueDepth;

    /// <summary>진단 로거. 기본은 무출력.</summary>
    public IServerLogger Logger { get; set; } = NullServerLogger.Instance;

    /// <summary>메트릭 싱크(Phase 11). <see langword="null"/>이면 기록하지 않는다.</summary>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>송신 실패로 싱크가 사망했을 때의 통지. 룸 퇴장·커넥션 정리는 앱의 몫이다.</summary>
    /// <remarks>파티션 스레드 또는 전달 호출 스레드에서 불린다. 블로킹·예외 금지.</remarks>
    public Action<ConnectionId>? OnDeliveryFaulted { get; set; }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (SendQueueDepth < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(SendQueueDepth)}는 1 이상이어야 한다. 현재 값: {SendQueueDepth}");
        }

        ArgumentNullException.ThrowIfNull(Logger, nameof(Logger));
    }
}
