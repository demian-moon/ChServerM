using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Transports;

namespace ChServerM.Transport.InMemory;

/// <summary>
/// 프로세스 안에서 서버 종단에 연결하는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 서버와 클라이언트가 <b>같은 <see cref="IConnection"/></b>을 쓴다는 것을
/// 실제로 보이는 자리다. 이 전송으로 붙인 클라이언트는 TCP 로 붙인 클라이언트와
/// 완전히 같은 상위 계층(프레이밍·디스패치·핸들러)을 쓴다.
/// </para>
/// <para>
/// <b>재접속을 하지 않는다.</b> 연결 실패는 예외로 그대로 올린다. 여기서 재시도를 감추면
/// 상위 계층이 "연결이 살아 있다"고 오해해 세션 재수립(인증·상태 복원)을 건너뛴다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다. 여러 스레드가 동시에 연결해도 된다.
/// </para>
/// <para>
/// <b>수명.</b> 이 전송이 만든 커넥션을 추적하다가 <see cref="DisposeAsync"/>에서 정리한다.
/// 추적하지 않으면 클라이언트를 버려도 서버 쪽 핸들러가 계속 대기해
/// <c>StopAsync</c> 가 끝나지 않는다.
/// </para>
/// </remarks>
public sealed class InMemoryClientTransport : IClientTransport, ITransportBufferLimits
{
    private readonly InMemoryTransportHub _hub;
    private readonly InMemoryEndPoint _localEndPoint;
    private readonly ConcurrentDictionary<InMemoryConnection, byte> _connections = new();
    private readonly long _maxBufferedBytes;

    private int _disposed;

    /// <summary>클라이언트 전송을 만든다.</summary>
    /// <param name="hub">이름 레지스트리. 서버 전송과 같은 것을 써야 한다.</param>
    /// <param name="localEndPoint">
    /// 이 클라이언트의 종단 이름. <see langword="null"/>이면 자동 생성한다.
    /// 서버 쪽에서 <c>RemoteEndPoint</c> 로 보인다.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="hub"/>가 <see langword="null"/>일 때.</exception>
    /// <param name="options">
    /// 버퍼 한계를 알리기 위한 설정. 실제 파이프는 서버 쪽 전송이 만들므로 여기서는
    /// <see cref="ITransportBufferLimits"/> 보고에만 쓰인다. 서버와 같은 값을 넘긴다.
    /// </param>
    public InMemoryClientTransport(
        InMemoryTransportHub hub,
        InMemoryEndPoint? localEndPoint = null,
        InMemoryTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(hub);

        options ??= new InMemoryTransportOptions();
        options.Validate();

        _hub = hub;
        _localEndPoint = localEndPoint ?? new InMemoryEndPoint($"client-{Guid.NewGuid():N}");
        _maxBufferedBytes = options.PauseWriterThreshold;
    }

    /// <summary>이 클라이언트의 종단.</summary>
    public InMemoryEndPoint LocalEndPoint => _localEndPoint;

    /// <inheritdoc />
    public long MaxBufferedBytesPerConnection => _maxBufferedBytes;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="endPoint"/>가 <see cref="InMemoryEndPoint"/>가 아닐 때.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 그 이름을 듣고 있는 서버가 없거나, 서버가 연결을 거부했을 때.
    /// </exception>
    /// <exception cref="ObjectDisposedException">이 전송이 이미 해제됐을 때.</exception>
    public ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (endPoint is not InMemoryEndPoint target)
        {
            throw new ArgumentException(
                $"인메모리 전송은 {nameof(InMemoryEndPoint)} 만 받는다. 받은 타입: {endPoint.GetType().Name}",
                nameof(endPoint));
        }

        if (!_hub.TryGetListener(target.Name, out InMemoryServerTransport listener))
        {
            // 실제 전송이라면 연결 거부(ECONNREFUSED)에 해당한다.
            throw new InvalidOperationException($"{target} 를 듣고 있는 서버가 없다.");
        }

        InMemoryConnection connection = listener.Accept(_localEndPoint);
        _connections[connection] = 0;

        return ValueTask.FromResult<IConnection>(connection);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 이 전송이 만든 커넥션을 모두 정상 종료한다. 서버 쪽 읽기 루프는
    /// 스트림 완료를 관측하고 스스로 끝난다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (InMemoryConnection connection in _connections.Keys)
        {
            _connections.TryRemove(connection, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
