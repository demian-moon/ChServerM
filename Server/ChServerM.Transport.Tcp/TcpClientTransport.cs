using System;
using System.Collections.Concurrent;
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
/// raw <see cref="Socket"/> 으로 TCP 연결을 맺는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 서버와 클라이언트가 <b>같은 <see cref="IConnection"/></b>을 쓴다는 것을
/// 실제로 보이는 자리다. 서버-투-서버 통신이 특별한 경로가 되지 않는다.
/// </para>
/// <para>
/// <b>재접속을 하지 않는다.</b> 연결 실패는 예외로 그대로 올린다. 여기서 재시도를 감추면
/// 상위 계층이 "연결이 살아 있다"고 오해해 세션 재수립(인증·상태 복원)을 건너뛴다.
/// 백오프 정책은 조립하는 쪽의 몫이다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// </remarks>
public sealed class TcpClientTransport : IClientTransport, ITransportBufferLimits
{
    private readonly TcpTransportOptions _options;
    private readonly IServerLogger _logger;
    private readonly ConcurrentDictionary<ConnectionId, SocketConnection> _connections = new();

    private int _nextSlot;
    private int _disposed;

    /// <summary>클라이언트 전송을 만든다.</summary>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public TcpClientTransport(TcpTransportOptions? options = null, IServerLogger? logger = null)
    {
        options ??= new TcpTransportOptions();
        options.Validate();

        _options = options;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    public long MaxBufferedBytesPerConnection => _options.PauseWriterThreshold;

    /// <inheritdoc />
    /// <exception cref="SocketException">연결에 실패했을 때.</exception>
    /// <exception cref="ObjectDisposedException">이 전송이 이미 해제됐을 때.</exception>
    public async ValueTask<IConnection> ConnectAsync(
        EndPoint endPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        Socket socket = new(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            _options.Apply(socket);
        }
        catch
        {
            // 실패한 소켓을 흘리면 그것이 곧 핸들 누수다.
            socket.Dispose();
            throw;
        }

        SocketConnection connection = new(NextConnectionId(), socket, _options, _logger);
        connection.Start();
        _connections[connection.Id] = connection;

        return connection;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 이 전송이 만든 커넥션을 모두 정상 종료한다. 추적하지 않으면
    /// 클라이언트를 버려도 서버 쪽 핸들러가 계속 대기해 <c>StopAsync</c> 가 끝나지 않는다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (SocketConnection connection in _connections.Values)
        {
            _connections.TryRemove(connection.Id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);
}
