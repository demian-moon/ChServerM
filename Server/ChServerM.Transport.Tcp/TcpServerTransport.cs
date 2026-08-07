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
using ChServerM.Resilience;
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
public sealed class TcpServerTransport : IServerTransport, ITransportBufferLimits, IHealthCheck
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

    /// <summary>수락 루프를 멈춘 예외. <see langword="null"/> 이면 정상(수락 중이거나 정상 종료).</summary>
    /// <remarks>
    /// <para>
    /// <b>존재 이유 — 조용한 죽음을 없앤다.</b> 수락 루프가 예상 밖 예외로 죽으면 서버는
    /// <b>살아 있지만 신규 연결을 하나도 받지 못하는</b> 상태가 된다. 그런데 <c>_acceptLoop</c>
    /// 태스크는 <c>UnbindAsync</c> 에서야 <c>await</c> 되므로 <b>종료 시점까지 아무도 모른다</b> —
    /// 헬스는 여전히 "수용 중" 을 보고하고, 오케스트레이터는 멀쩡한 줄 알고 트래픽을 계속 보낸다.
    /// 이 필드가 그 상태를 <see cref="CheckAsync"/> 로 드러낸다.
    /// </para>
    /// <para>
    /// <b>정상 종료는 기록하지 않는다.</b> <c>Unbind</c>·취소로 루프가 끝나는 것은 고장이 아니다.
    /// </para>
    /// <para><b>스레드 규약(9.3).</b> 수락 루프가 쓰고 헬스 프로브가 다른 스레드에서 읽는다 —
    /// 양쪽 모두 <see cref="Volatile"/> 로 접근한다.</para>
    /// </remarks>
    private Exception? _acceptFault;
    private int _nextSlot;
    private int _bound;
    private int _disposed;

    // idle 스윕 — 전송당 타이머 하나다. 커넥션당 타이머를 만들지 않는다(CLAUDE.md 9.5).
    // CA2213 억제 사유는 _listenSocket 과 같다(비동기 정리 경로).
#pragma warning disable CA2213
    private Timer? _idleSweepTimer;
#pragma warning restore CA2213

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

        // 스냅샷 보관 — 라이브 참조면 Build() 이후의 옵션 변경이 조립 검사(ADR-0007)를
        // 사후 무효화한다. 상세는 TcpTransportOptions.Snapshot 문서.
        _options = options.Snapshot();
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
    public long MaxBufferedBytesPerConnection => _options.PauseWriterThreshold;

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

            if (_options.ReuseAddress)
            {
                // 재시작 시 TIME_WAIT 포트 재바인드용. 기본 끔 — 근거는 옵션 문서.
                listenSocket.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
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

        // CA2025 억제 — 지적 자체는 맞지만 여기서는 의도된 설계다.
        //
        // 분석기가 보는 것: 수락 루프가 listenSocket 을 쓰는데
        // UnbindAsync 가 그것을 Dispose 한 뒤에 루프를 await 한다.
        //
        // 그것이 정확히 취소 수단이다. AcceptAsync 로 대기 중인 소켓을 깨우는 방법은
        // Dispose 뿐이다 — 토큰 취소는 플랫폼마다 대기 중 작업에 실제로 먹는지가
        // 다르고(SocketConnection 문서 참조), 안 먹으면 UnbindAsync 가 영구히 매달린다.
        // Kestrel 도 같은 방식을 쓴다.
        //
        // 안전한 이유: 루프가 ObjectDisposedException 을 잡고 즉시 반환한다
        // (IsTransientAcceptError 가 아니라 별도 catch 절). 해제된 소켓을 계속 쓰는
        // 경로가 없고, UnbindAsync 는 그 반환을 await 해 확인한다.
        //
        // await 먼저 → Dispose 나중으로 순서를 바꿀 수 없다. 루프는 소켓이 죽어야
        // 끝나므로 그 순서는 교착이다.
#pragma warning disable CA2025
        _acceptLoop = AcceptLoopAsync(listenSocket, handler);
#pragma warning restore CA2025

        if (_options.IdleTimeout > TimeSpan.Zero)
        {
            // 스윕 주기 = 타임아웃/4 (최소 1초). 판정 해상도와 스윕 비용의 절충이다 —
            // 상세는 TcpTransportOptions.IdleTimeout 문서.
            TimeSpan period = TimeSpan.FromMilliseconds(
                Math.Max(1000, _options.IdleTimeout.TotalMilliseconds / 4));
            _idleSweepTimer = new Timer(static state => ((TcpServerTransport)state!).SweepIdleConnections(),
                this, period, period);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>idle 타임아웃을 넘긴 커넥션을 끊는다. 스윕 타이머 콜백.</summary>
    /// <remarks>
    /// O(커넥션 수) 순회다 — 1만 커넥션에 티스탬프 비교 1만 번은 스윕당 수십 µs 로,
    /// 주기(≥1초) 대비 무시할 수준이다. 타이머 콜백에서 예외가 새면 프로세스가 죽으므로
    /// 전체를 삼킨다(개별 Abort 는 멱등·예외 없음 설계).
    /// </remarks>
    private void SweepIdleConnections()
    {
        long cutoff = Environment.TickCount64 - (long)_options.IdleTimeout.TotalMilliseconds;

#pragma warning disable CA1031 // 타이머 콜백의 미처리 예외는 프로세스를 죽인다.
        try
        {
            foreach (ActiveConnection active in _connections.Values)
            {
                if (active.Connection.LastActivityTicks < cutoff)
                {
                    active.Connection.Abort(new ConnectionCloseInfo(
                        CloseReason.Timeout, ErrorCode.TransportTimeout,
                        $"수신·송신이 {_options.IdleTimeout} 동안 없었다."));
                }
            }
        }
        catch (Exception)
        {
            // 다음 스윕에서 다시 시도한다.
        }
#pragma warning restore CA1031
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

        // idle 스윕은 기존 커넥션이 사는 동안(드레인 중) 계속 필요하므로 여기서 멈추지
        // 않는다 — DisposeAsync 가 정리한다.
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
                // 상한이 있어야 한다 — Abort 뒤에도 취소 토큰을 무시하는 사용자 핸들러는
                // 안 끝날 수 있고, 그때 무한 대기면 서버 종료가 핸들러 하나에 볼모로
                // 잡힌다(2026-08-04 감사 보류분). 커넥션 정리는 핸들러와 독립적으로
                // RunHandlerAsync 의 finally 가 보장한다.
                // CancellationToken.None 명시 — 이 대기는 시간 상한으로만 통제한다.
                await Task.WhenAll(pending)
                    .WaitAsync(_options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
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

        if (Interlocked.Exchange(ref _idleSweepTimer, null) is { } sweep)
        {
            await sweep.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listenSocket, IConnectionHandler handler)
    {
        // 첫 await 까지 동기적으로 도는 것을 막는다. BindAsync 가 수락 루프에 붙들리면
        // 서버 시작이 클라이언트 연결을 기다리게 된다.
        await Task.Yield();

        // 최종 방어선. StartConnection 은 사용자 공급 컴포넌트(IAdmissionControl)를 부르므로
        // 소켓 예외가 아닌 무엇이든 던질 수 있다. 그것이 이 루프를 뚫고 나가면 수락 루프가
        // 조용히 죽고(태스크는 Unbind 때까지 관측되지 않는다) 서버는 "살아 있지만 아무도
        // 못 받는" 상태가 된다 — 그 조용한 죽음을 여기서 끊는다(_acceptFault 문서).
        try
        {
            await AcceptUntilStoppedAsync(listenSocket, handler).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 수락 루프는 어떤 예외로도 조용히 죽으면 안 된다 — 기록하고 헬스로 드러낸다.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            MarkAcceptFaulted(exception);
        }
    }

    private async Task AcceptUntilStoppedAsync(Socket listenSocket, IConnectionHandler handler)
    {
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
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TooManyOpenSockets)
            {
                // FD/핸들 고갈. 이 상태에서 즉시 재시도하면 AcceptAsync 가 곧바로 다시
                // 실패해 수락 루프가 예외를 만들며 코어 하나를 태운다(2026-08-04 감사).
                // 재시도 자체는 맞다 — 고갈은 대개 일시적이다 — 다만 물러났다 한다.
                LogAcceptBackoff(exception);

                try
                {
                    await Task.Delay(ExhaustionRetryDelay, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }
            catch (SocketException exception) when (IsTransientAcceptError(exception))
            {
                // 개별 연결이 수락 직전에 끊겼다. 흔한 일이고 루프를 멈출 이유가 없다.
                continue;
            }
            catch (SocketException exception)
            {
                // 수락 소켓 자체가 죽었다. 계속 돌면 무한 루프가 된다.
                // 이것도 고장이다 — 로그만 남기고 끝내면 헬스가 계속 "수용 중" 을 보고한다.
                MarkAcceptFaulted(exception);
                return;
            }

            StartConnection(accepted, handler);
        }
    }

    private void StartConnection(Socket accepted, IConnectionHandler handler)
    {
        // 정적 하드 상한 — 정상 상태의 최대 커넥션.
        if (_connections.Count >= _options.MaxConnections)
        {
            RejectConnection(accepted, CloseReasonTags.ConnectionLimit);
            return;
        }

        // 동적 수용 제어 — 상한 안의 연결 폭주(SYN 폭주·재접속 스톰)를 막는다(T-16).
        // 정적 상한을 통과한 뒤에만 물어본다: 이미 꽉 찼으면 속도를 따질 필요가 없다.
        if (_options.AdmissionControl is { } admissionControl
            && !admissionControl.TryAdmit(SafeRemoteEndPoint(accepted)).IsAdmitted)
        {
            RejectConnection(accepted, CloseReasonTags.Admission);
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

        // 등록이 핸들러 기동보다 먼저다. 반대 순서면 즉시 끊긴 커넥션(포트 스캐너 등)의
        // 정리(finally 의 TryRemove)가 등록보다 먼저 실행될 수 있고, 그 죽은 항목은 영구히
        // 남는다 — ConnectionCount 가 부풀어 상한 판정이 살아 있는 연결을 거부하게 된다
        // (2026-08-04 감사 H1). Completion 은 TCS 프록시로 두고 핸들러 정리가 끝난 뒤 알린다.
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections[connection.Id] = new ActiveConnection(connection, completion.Task);

        _ = RunHandlerAsync(handler, connection, completion);
    }

    /// <summary>핸들러를 돌리고, 끝나면 반드시 커넥션을 정리하고 목록에서 뺀다.</summary>
    /// <remarks>
    /// <b><c>finally</c>가 핵심이다.</b> 목록 제거를 빠뜨리면 종료된 커넥션이 영원히 남아
    /// <see cref="StopAsync"/>가 끝나지 않는다 (CLAUDE.md 9.2).
    /// </remarks>
    private async Task RunHandlerAsync(
        IConnectionHandler handler,
        SocketConnection connection,
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

    /// <summary>FD 고갈 시 재시도 전에 물러나는 시간.</summary>
    private static readonly TimeSpan ExhaustionRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>수락 루프를 즉시 계속 돌아도 되는 오류인지 판별한다.</summary>
    /// <remarks>
    /// <see cref="SocketError.TooManyOpenSockets"/> 는 여기 없다 — 그것은 즉시 재시도가
    /// 아니라 백오프 후 재시도다(수락 루프의 전용 catch 절).
    /// </remarks>
    private static bool IsTransientAcceptError(SocketException exception) =>
        exception.SocketErrorCode is
            SocketError.ConnectionReset or
            SocketError.ConnectionAborted or
            SocketError.Interrupted or
            SocketError.NetworkReset;

    private void LogAcceptBackoff(SocketException exception)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                AcceptFaultedEvent,
                ExhaustionRetryDelay,
                exception,
                static (delay, ex) =>
                    $"소켓 핸들이 고갈됐다({ex?.Message}). {delay.TotalMilliseconds:F0}ms 뒤 수락을 재시도한다.");
        }
    }

    /// <summary>수락 루프가 고장으로 끝났음을 기록하고 헬스에 드러낸다.</summary>
    /// <remarks>
    /// 로그는 즉시(운영자가 원인을 본다), 상태는 <see cref="CheckAsync"/> 로(오케스트레이터가
    /// 트래픽을 뺀다). 둘 다 필요하다 — 로그만 남기면 자동화된 대응이 없고, 상태만 두면
    /// 원인을 알 수 없다.
    /// </remarks>
    private void MarkAcceptFaulted(Exception exception)
    {
        Volatile.Write(ref _acceptFault, exception);
        LogAcceptFaulted(exception);
    }

    /// <summary>수락 능력에 대한 헬스 판정 (Phase 10 크래시 처리, ADR-0028).</summary>
    /// <param name="cancellationToken">쓰이지 않는다 — 로컬 상태를 읽는 즉시 완료다.</param>
    /// <returns>
    /// 수락 루프가 고장으로 끝났으면 <see cref="HealthStatus.Unhealthy"/>(사유 포함),
    /// 그 외에는 <see cref="HealthStatus.Healthy"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>readiness 신호다.</b> 수락 루프가 죽은 서버는 <b>신규 트래픽을 받을 수 없으므로</b>
    /// 로드밸런서에서 빠져야 한다. 기존 커넥션은 여전히 처리되므로 즉시 재시작(liveness 실패)
    /// 대상으로 두지 않는다 — 진행 중인 작업을 끊는 것이 더 큰 피해일 수 있다.
    /// </para>
    /// <para>
    /// <b>다만 이 고장은 회복되지 않는다.</b> 루프는 다시 돌지 않으므로 not-ready 가 지속되면
    /// 운영·오케스트레이터가 재시작으로 escalate 해야 한다(수락 재시작을 자동화하지 않는 이유:
    /// 원인이 지속적이면 무한 예외 루프로 코어를 태운다, ADR-0028).
    /// </para>
    /// <para>
    /// 호스팅은 전송이 <see cref="IHealthCheck"/> 를 구현하면 프로브에 자동 등록한다 —
    /// Core 전송 계약(<see cref="IServerTransport"/>)에 진단 멤버를 얹지 않는 접점이다
    /// (실행 모델 liveness 와 같은 규율, ADR-0023).
    /// </para>
    /// </remarks>
    public ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        Exception? fault = Volatile.Read(ref _acceptFault);

        HealthCheckResult result = fault is null
            ? HealthCheckResult.Healthy("수락 루프 정상")
            : HealthCheckResult.Unhealthy($"수락 루프가 중단됐다 — 신규 연결을 받지 못한다: {fault.Message}");

        return ValueTask.FromResult(result);
    }

    private void LogAcceptFaulted(Exception exception)
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

    /// <summary>커넥션을 거부한다 — 관측·로그·최선 노력 통지·소켓 정리를 한곳에서.</summary>
    /// <remarks>
    /// 정적 상한 거부와 동적 수용 거부가 같은 경로를 쓴다 — 통지·정리 로직이 두 벌이 되면
    /// 한쪽만 고치는 사고가 난다. <paramref name="reason"/> 는 메트릭의 저카디널리티 태그다.
    /// </remarks>
    private void RejectConnection(Socket accepted, string reason)
    {
        // 거부가 붕괴보다 낫다. 조용히 받아두고 나중에 죽는 것이 최악이다(관측되지 않는 유실도 금지).
        EmitRejected(reason);
        LogRejected(reason);

        // 거부 이유 통지(최선 노력). 그냥 닫으면 클라이언트는 RST 하나만 보고
        // "서버가 꽉 찼다"와 "네트워크가 끊겼다"를 구분할 수 없다 — 옵션 문서 참조.
        // 동기 Send 인 이유: 새 소켓의 송신 버퍼는 비어 있어 실질 논블로킹이고,
        // 거부 경로에서 비동기 대기를 만들면 그것이 곧 자원 소모 공격 표면이다.
        if (!_options.RejectionNotice.IsEmpty)
        {
            try
            {
                accepted.Send(_options.RejectionNotice.Span);
            }
            catch (SocketException)
            {
                // 상대가 이미 끊었다. 통지는 최선 노력이다.
            }
            catch (ObjectDisposedException)
            {
                // 이미 버려진 소켓이다.
            }
        }

        accepted.Dispose();
    }

    /// <summary>원격 주소를 안전하게 읽는다 — 수락 직후 끊긴 소켓은 던진다.</summary>
    private static EndPoint? SafeRemoteEndPoint(Socket accepted)
    {
        try
        {
            return accepted.RemoteEndPoint;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void EmitRejected(string reason)
    {
        if (_options.MetricsSink is { } sink)
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
                (Limit: _options.MaxConnections, Reason: reason),
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

    private readonly record struct ActiveConnection(SocketConnection Connection, Task Completion);
}
