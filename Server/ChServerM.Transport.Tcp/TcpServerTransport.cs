using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Identity;
using ChServerM.Transports;

namespace ChServerM.Transport.Tcp;

/// <summary>
/// raw <see cref="Socket"/> 로 TCP 커넥션을 수용하는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 첫 실동 전송이다. 인메모리 전송과 <b>같은 상위 계층</b>
/// (프레이밍·디스패치·핸들러)을 쓴다는 것이 Phase 1 의 최종 합격 기준이다(ADR-0004).
/// </para>
/// <para>
/// <b>3단 종료를 구현한다.</b> <see cref="UnbindAsync"/>는 수락 소켓만 닫아 신규 연결을
/// 막고, 기존 커넥션은 계속 산다. <see cref="StopAsync"/>가 그것들을 드레인한다.
/// 레거시에는 이 드레인 단계가 없어 종료가 곧 전원 차단이었다.
/// </para>
/// <para>
/// <b>수락 루프의 예외를 구분한다.</b> 개별 연결이 수락 도중 끊기는 것(<c>ConnectionReset</c>)은
/// 흔한 일이고 루프를 멈출 이유가 없다. 반면 수락 소켓 자체가 죽은 것은 계속할 수 없다.
/// 둘을 구분하지 않으면 <b>서버가 조용히 수용을 멈추거나</b>, 반대로
/// <b>죽은 소켓에 대고 무한 루프를 돈다.</b>
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// </remarks>
public sealed class TcpServerTransport : IServerTransport
{
    private static readonly EventId AcceptFaultedEvent = new(1020, "AcceptFaulted");
    private static readonly EventId ConnectionRejectedEvent = new(1004, "ConnectionRejected");
    private static readonly EventId HandlerFaultedEvent = new(1006, "ConnectionHandlerFaulted");

    private readonly EndPoint _bindEndPoint;
    private readonly TcpTransportOptions _options;
    private readonly IServerLogger _logger;
    private readonly ConcurrentDictionary<ConnectionId, ActiveConnection> _connections = new();
    private readonly CancellationTokenSource _stopping = new();

    // CA2213 억제: 이 타입은 IAsyncDisposable 만 구현하고, 수락 소켓은
    // UnbindAsync 와 DisposeAsync 에서 Interlocked.Exchange 로 꺼내 Dispose 한다.
    // 분석기는 동기 Dispose() 메서드만 인식하므로 그 경로를 보지 못한다.
#pragma warning disable CA2213
    private Socket? _listenSocket;
#pragma warning restore CA2213
    private IConnectionHandler? _handler;
    private Task _acceptLoop = Task.CompletedTask;
    private int _nextSlot;
    private int _bound;
    private int _disposed;

    /// <summary>수용 전송을 만든다.</summary>
    /// <param name="endPoint">바인드할 주소. 포트 0 이면 OS 가 배정한다.</param>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endPoint"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public TcpServerTransport(
        EndPoint endPoint,
        TcpTransportOptions? options = null,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        options ??= new TcpTransportOptions();
        options.Validate();

        _bindEndPoint = endPoint;
        _options = options;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 포트 0 으로 바인드했다면 여기서 <b>OS 가 실제로 배정한 포트</b>를 읽는다.
    /// 테스트가 포트를 하드코딩하지 않아도 되는 이유다.
    /// </remarks>
    public EndPoint? LocalEndPoint => _listenSocket?.LocalEndPoint;

    /// <summary>현재 열려 있는 커넥션 수.</summary>
    public int ConnectionCount => _connections.Count;

    /// <inheritdoc />
    public ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _bound, 1, 0) != 0)
        {
            throw new InvalidOperationException($"{_bindEndPoint} 에 이미 바인드돼 있다.");
        }

        Socket listenSocket = new(_bindEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            // IPv6Any 로 바인드하면 IPv4 도 함께 받는다. 이걸 빠뜨리면 IPv4 클라이언트가
            // "연결 거부"를 보게 되고, 원인이 코드 어디에도 드러나지 않는다.
            if (_bindEndPoint is IPEndPoint { Address.IsIPv4MappedToIPv6: false } ip
                && ip.Address.Equals(IPAddress.IPv6Any))
            {
                listenSocket.DualMode = true;
            }

            listenSocket.Bind(_bindEndPoint);
            listenSocket.Listen(_options.Backlog);
        }
        catch
        {
            // 실패한 바인드가 상태를 오염시키면 이 인스턴스는 영원히 좀비가 된다.
            listenSocket.Dispose();
            Volatile.Write(ref _bound, 0);
            throw;
        }

        _listenSocket = listenSocket;
        _handler = handler;
        _acceptLoop = AcceptLoopAsync(listenSocket, handler);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>수락 소켓만 닫는다. 기존 커넥션은 영향받지 않는다.</remarks>
    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 바인드되지 않았어도 조용히 성공한다(IServerTransport 계약).
        if (Interlocked.CompareExchange(ref _bound, 0, 1) != 1)
        {
            return;
        }

        Socket? listenSocket = Interlocked.Exchange(ref _listenSocket, null);
        listenSocket?.Dispose();

        // 수락 루프가 끝나기를 기다린다. 기다리지 않으면 Unbind 가 돌아온 뒤에도
        // 커넥션이 하나 더 들어올 수 있다.
        await _acceptLoop.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // 신규 수용부터 막는다. 순서가 반대면 드레인 중에 새 커넥션이 들어와 끝나지 않는다.
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
            // 드레인 제한 시간이 끝났다. 남은 것은 끊는다.
            foreach (ActiveConnection active in _connections.Values)
            {
                active.Connection.Abort(ConnectionCloseInfo.ShuttingDown);
            }

#pragma warning disable CA1031 // 종료 경로. 개별 커넥션의 예외로 전체 정리를 멈추지 않는다.
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 강제 종료된 커넥션의 예외는 이미 기록됐다.
            }
#pragma warning restore CA1031
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        // Dispose 는 기다려주는 자리가 아니다. 즉시 끊는다.
        using CancellationTokenSource immediate = new();
        await immediate.CancelAsync().ConfigureAwait(false);
        await StopAsync(immediate.Token).ConfigureAwait(false);

        // StopAsync → UnbindAsync 가 이미 정리했지만, 바인드에 실패했거나
        // 바인드하지 않은 경로에서도 핸들이 남지 않도록 여기서 한 번 더 확인한다.
        Interlocked.Exchange(ref _listenSocket, null)?.Dispose();

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listenSocket, IConnectionHandler handler)
    {
        // 첫 await 까지 동기적으로 도는 것을 막는다. BindAsync 가 수락 루프에 붙들리면
        // 서버 시작이 클라이언트 연결을 기다리게 된다.
        await Task.Yield();

        while (!_stopping.IsCancellationRequested)
        {
            Socket accepted;

            try
            {
                accepted = await listenSocket.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // Unbind 가 수락 소켓을 닫았다. 정상 종료 경로다.
                return;
            }
            catch (SocketException exception) when (IsTransientAcceptError(exception))
            {
                // 개별 연결이 수락 직전에 끊겼다. 흔한 일이고 루프를 멈출 이유가 없다.
                continue;
            }
            catch (SocketException exception)
            {
                // 수락 소켓 자체가 죽었다. 계속 돌면 무한 루프가 된다.
                LogAcceptFaulted(exception);
                return;
            }

            StartConnection(accepted, handler);
        }
    }

    private void StartConnection(Socket accepted, IConnectionHandler handler)
    {
        if (_connections.Count >= _options.MaxConnections)
        {
            // 거부가 붕괴보다 낫다. 조용히 받아두고 나중에 죽는 것이 최악이다.
            LogRejected();
            accepted.Dispose();
            return;
        }

        SocketConnection connection;

        try
        {
            _options.Apply(accepted);

            // CA2000 억제: 소유권이 RunHandlerAsync 로 넘어간다. 그쪽 finally 가
            // 반드시 DisposeAsync 를 부른다. 분석기는 비동기 소유권 이전을 추적하지 못한다.
#pragma warning disable CA2000
            connection = new SocketConnection(NextConnectionId(), accepted, _options, _logger);
#pragma warning restore CA2000
        }
        catch (SocketException)
        {
            // 수락 직후 끊긴 소켓이다. 옵션 적용이 던질 수 있다.
            accepted.Dispose();
            return;
        }
        catch (ObjectDisposedException)
        {
            accepted.Dispose();
            return;
        }

        connection.Start();
        _connections[connection.Id] = new ActiveConnection(connection, RunHandlerAsync(handler, connection));
    }

    /// <summary>핸들러를 돌리고, 끝나면 반드시 커넥션을 정리하고 목록에서 뺀다.</summary>
    /// <remarks>
    /// <b><c>finally</c>가 핵심이다.</b> 목록 제거를 빠뜨리면 종료된 커넥션이 영원히 남아
    /// <see cref="StopAsync"/>가 끝나지 않는다 (CLAUDE.md 9.2).
    /// </remarks>
    private async Task RunHandlerAsync(IConnectionHandler handler, SocketConnection connection)
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
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    /// <summary>수락 루프를 계속 돌아도 되는 오류인지 판별한다.</summary>
    private static bool IsTransientAcceptError(SocketException exception) =>
        exception.SocketErrorCode is
            SocketError.ConnectionReset or
            SocketError.ConnectionAborted or
            SocketError.Interrupted or
            SocketError.NetworkReset or
            SocketError.TooManyOpenSockets;

    private void LogAcceptFaulted(SocketException exception)
    {
        if (_logger.IsEnabled(LogLevel.Critical))
        {
            _logger.Log(
                LogLevel.Critical,
                AcceptFaultedEvent,
                _bindEndPoint,
                exception,
                static (endPoint, ex) => $"{endPoint} 수락 루프가 중단됐다. 더는 연결을 받지 않는다: {ex?.Message}");
        }
    }

    private void LogRejected()
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                ConnectionRejectedEvent,
                _options.MaxConnections,
                null,
                static (limit, _) => $"동시 접속 상한({limit})에 도달해 연결을 거부했다.");
        }
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

    private readonly record struct ActiveConnection(SocketConnection Connection, Task Completion);
}
