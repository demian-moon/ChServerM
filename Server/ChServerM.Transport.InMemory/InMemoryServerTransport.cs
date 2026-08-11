using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Transports;

namespace ChServerM.Transport.InMemory;

/// <summary>
/// 프로세스 안에서 커넥션을 수용하는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="IServerTransport"/>의 두 번째 구현체다. 첫 번째 구현만
/// 있는 추상화는 그 구현을 베낀 것에 불과하다 — 두 번째가 들어와야 계약이 검증된다
/// (CLAUDE.md 3장 "두 번째 구현이 나오기 전까지 추상화는 가설").
/// </para>
/// <para>
/// <b>3단 종료를 실제로 구현한다.</b> <see cref="UnbindAsync"/>는 이름 등록만 해제해
/// 신규 연결을 막고, 기존 커넥션은 계속 산다. <see cref="StopAsync"/>가 그것들을
/// 드레인한다. 이 두 단계 사이가 무중단 배포의 창이다.
/// </para>
/// <para>
/// <b>드레인은 무한정 기다리지 않는다.</b> <see cref="StopAsync"/>의 토큰이 취소되면
/// 남은 커넥션을 <see cref="IConnection.Abort"/>로 끊는다. 상한 없는 대기는
/// 종료를 영원히 막고, 배포 파이프라인을 멈춘다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다. 여러 클라이언트가 동시에 연결해도 된다.
/// </para>
/// </remarks>
public sealed class InMemoryServerTransport : IServerTransport, ITransportBufferLimits
{
    private static readonly EventId ConnectionRejectedEvent = new(1004, "ConnectionRejected");
    private static readonly EventId HandlerFaultedEvent = new(1006, "ConnectionHandlerFaulted");

    private readonly InMemoryTransportHub _hub;
    private readonly InMemoryEndPoint _endPoint;
    private readonly PipeOptions _pipeOptions;
    private readonly int _maxConnections;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IServerLogger _logger;
    private readonly IAdmissionControl? _admissionControl;
    private readonly IMetricsSink? _metricsSink;

    private readonly ConcurrentDictionary<ConnectionId, ActiveConnection> _connections = new();

    private IConnectionHandler? _handler;
    private int _nextSlot;
    private int _bound;

    /// <summary>수용 판정용 활성 커넥션 수. 상한 검사는 이것으로만 한다(엄격 유계).</summary>
    private int _activeCount;

    /// <summary>수용 전송을 만든다.</summary>
    /// <param name="hub">이름 레지스트리. 클라이언트 전송과 같은 것을 써야 한다.</param>
    /// <param name="endPoint">수용할 종단.</param>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public InMemoryServerTransport(
        InMemoryTransportHub hub,
        InMemoryEndPoint endPoint,
        InMemoryTransportOptions? options = null,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(endPoint);

        options ??= new InMemoryTransportOptions();
        options.Validate();

        _hub = hub;
        _endPoint = endPoint;
        _pipeOptions = options.CreatePipeOptions();
        _maxConnections = options.MaxConnections;
        _shutdownTimeout = options.ShutdownTimeout;
        _logger = logger ?? NullServerLogger.Instance;
        _admissionControl = options.AdmissionControl;
        _metricsSink = options.MetricsSink;
        MaxBufferedBytesPerConnection = options.PauseWriterThreshold;
    }

    /// <inheritdoc />
    public long MaxBufferedBytesPerConnection { get; }

    /// <inheritdoc />
    /// <remarks>바인드 전에는 <see langword="null"/>이다.</remarks>
    public EndPoint? LocalEndPoint => Volatile.Read(ref _bound) == 1 ? _endPoint : null;

    /// <summary>현재 열려 있는 커넥션 수.</summary>
    public int ConnectionCount => _connections.Count;

    /// <inheritdoc />
    public ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _bound, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                $"{_endPoint} 에 이미 바인드돼 있다. 전송 인스턴스는 1회용이다 — "
                + "다시 바인드하려면 StopAsync 후 새 인스턴스를 만든다. 재호출이 의도가 아니라면 BindAsync 를 두 곳에서 부르는 조립을 의심한다.");
        }

        _handler = handler;

        if (!_hub.TryRegister(_endPoint.Name, this))
        {
            // 등록에 실패했으면 바인드 상태를 되돌린다. 이걸 빠뜨리면 이 인스턴스는
            // 영원히 "바인드됨"이면서 아무도 못 붙는 좀비가 된다.
            Volatile.Write(ref _bound, 0);
            _handler = null;

            throw new InvalidOperationException(
                $"{_endPoint} 는 이미 다른 전송이 수용 중이다.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 바인드되지 않았어도 조용히 성공한다(IServerTransport 계약).
        if (Interlocked.CompareExchange(ref _bound, 0, 1) == 1)
        {
            _hub.Unregister(_endPoint.Name, this);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // 신규 수용부터 막는다. 이 순서가 반대면 드레인 중에 새 커넥션이 들어와 끝나지 않는다.
        await UnbindAsync(CancellationToken.None).ConfigureAwait(false);

        List<Task> pending = [];
        foreach (ActiveConnection active in _connections.Values)
        {
            pending.Add(active.Completion);
        }

        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 드레인 제한 시간이 끝났다. 남은 것은 끊는다 — 상한 없는 대기는 종료를 영원히 막는다.
            foreach (ActiveConnection active in _connections.Values)
            {
                active.Connection.Abort(ConnectionCloseInfo.ShuttingDown);
            }

#pragma warning disable CA1031 // 종료 경로다. 개별 커넥션의 예외로 전체 정리를 멈추지 않는다.
            try
            {
                // 상한이 있어야 한다 — 취소 토큰을 무시하는 사용자 핸들러가 서버 종료를
                // 볼모로 잡지 않게(2026-08-04 감사 보류분). TCP 쪽과 같은 장치다.
                // CancellationToken.None 명시 — 이 대기는 시간 상한으로만 통제한다.
                await Task.WhenAll(pending)
                    .WaitAsync(_shutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 강제 종료된 커넥션의 예외(이미 기록됨)거나 상한 초과다.
            }
#pragma warning restore CA1031
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // 이미 종료 중이면 즉시 끊는다. Dispose 는 기다려주는 자리가 아니다.
        using CancellationTokenSource immediate = new();
        await immediate.CancelAsync().ConfigureAwait(false);

        await StopAsync(immediate.Token).ConfigureAwait(false);
    }

    /// <summary>클라이언트 연결을 수용하고 클라이언트 쪽 커넥션을 돌려준다.</summary>
    /// <remarks>
    /// 파이프 두 개를 엇갈려 묶어 커넥션 짝을 만들고, 서버 쪽에서 핸들러를 시작한다.
    /// 핸들러는 첫 <c>ReadAsync</c> 에서 곧바로 비동기로 넘어가므로 이 호출을 붙잡지 않는다.
    /// </remarks>
    internal InMemoryConnection Accept(InMemoryEndPoint clientEndPoint)
    {
        IConnectionHandler? handler = _handler;
        if (handler is null || Volatile.Read(ref _bound) != 1)
        {
            throw new InvalidOperationException($"{_endPoint} 는 수용 중이 아니다.");
        }

        // 증가 후 검사-롤백 — Accept 는 여러 클라이언트 스레드에서 동시에 불릴 수 있어
        // Count 검사(check-then-act)로는 상한을 소폭 초과할 수 있다(2026-08-04 감사).
        // 유계는 엄격해야 유계다(CLAUDE.md 9.6). TCP 는 단일 수락 루프라 해당 없다.
        if (Interlocked.Increment(ref _activeCount) > _maxConnections)
        {
            Interlocked.Decrement(ref _activeCount);

            // 거부가 붕괴보다 낫다. 조용히 받아두고 나중에 죽는 것이 최악이다.
            EmitRejected(CloseReasonTags.ConnectionLimit);
            LogRejected(CloseReasonTags.ConnectionLimit);
            throw new InvalidOperationException(
                $"동시 접속 상한({_maxConnections})에 도달했다. ({nameof(ErrorCode.ConnectionLimitReached)})");
        }

        // 동적 수용 제어 — 상한 안의 연결 폭주를 막는다(T-16). 정적 상한 통과 후에만 묻는다.
        // 거부 시 증가시킨 카운터를 되돌려야 한다(정적 거부와 같은 롤백).
        if (_admissionControl is { } admissionControl
            && !admissionControl.TryAdmit(clientEndPoint).IsAdmitted)
        {
            Interlocked.Decrement(ref _activeCount);

            EmitRejected(CloseReasonTags.Admission);
            LogRejected(CloseReasonTags.Admission);
            throw new InvalidOperationException(
                $"수용 제어가 연결을 거부했다. ({nameof(ErrorCode.ConnectionLimitReached)})");
        }

        Pipe clientToServer = new(_pipeOptions);
        Pipe serverToClient = new(_pipeOptions);

        ConnectionId serverId = NextConnectionId();
        ConnectionId clientId = NextConnectionId();

        InMemoryConnection serverSide = new(
            serverId, clientToServer.Reader, serverToClient.Writer, _endPoint, clientEndPoint, _shutdownTimeout);

        InMemoryConnection clientSide = new(
            clientId, serverToClient.Reader, clientToServer.Writer, clientEndPoint, _endPoint, _shutdownTimeout);

        // 등록이 핸들러 기동보다 먼저다. 반대 순서면 즉시 끝난 핸들러의 정리(finally 의
        // TryRemove)가 등록보다 먼저 실행돼 죽은 항목이 영구히 남는다 — TCP 와 같은
        // 결함 부류(2026-08-04 감사 H1). Completion 은 TCS 프록시로 두고 정리 뒤 알린다.
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections[serverId] = new ActiveConnection(serverSide, completion.Task);

        _ = RunHandlerAsync(handler, serverSide, completion);

        return clientSide;
    }

    /// <summary>핸들러를 돌리고, 끝나면 반드시 커넥션을 정리하고 목록에서 뺀다.</summary>
    /// <remarks>
    /// <b><c>finally</c>가 핵심이다.</b> 여기서 목록 제거를 빠뜨리면 종료된 커넥션이
    /// 영원히 남아 <see cref="StopAsync"/>가 끝나지 않는다 — 락-프리 상태를
    /// <c>finally</c>로 복원한다는 규약(CLAUDE.md 9.2)이 그대로 적용되는 자리다.
    /// </remarks>
    private async Task RunHandlerAsync(
        IConnectionHandler handler,
        InMemoryConnection connection,
        TaskCompletionSource completion)
    {
        try
        {
            await handler.RunAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료로 인한 취소. 정상 경로다.
        }
#pragma warning disable CA1031 // 핸들러는 애플리케이션 코드다. 무엇을 던지든 프로세스를 죽이지 않는다.
        catch (Exception exception)
        {
            LogHandlerFaulted(connection.Id, exception);
            connection.Abort(new ConnectionCloseInfo(
                CloseReason.ApplicationError, ErrorCode.HandlerFaulted, exception.Message));
        }
#pragma warning restore CA1031
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            Interlocked.Decrement(ref _activeCount);

            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // StopAsync 가 이 신호로 드레인 완료를 판정한다. 정리가 던져도 반드시 알린다.
                completion.SetResult();
            }
        }
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    private void EmitRejected(string reason)
    {
        if (_metricsSink is { } sink)
        {
            Span<MetricTag> tags = [new MetricTag(TagNames.CloseReason, reason)];
            sink.Count(MetricNames.ConnectionsRejected, 1, tags);
        }
    }

    private void LogRejected(string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                ConnectionRejectedEvent,
                (Limit: _maxConnections, Reason: reason),
                null,
                static (state, _) => $"연결을 거부했다(사유: {state.Reason}, 동시 접속 상한 {state.Limit}).");
        }
    }

    /// <summary>커넥션 거부 메트릭의 저카디널리티 사유 태그 값.</summary>
    private static class CloseReasonTags
    {
        public const string ConnectionLimit = "connection_limit";
        public const string Admission = "admission";
    }

    private void LogHandlerFaulted(ConnectionId id, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(
                LogLevel.Error,
                HandlerFaultedEvent,
                id,
                exception,
                static (connectionId, ex) => $"{connectionId} 핸들러가 예외로 끝났다: {ex?.Message}");
        }
    }

    private readonly record struct ActiveConnection(InMemoryConnection Connection, Task Completion);
}
