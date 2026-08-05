using System;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Security;
using ChServerM.Transports;

namespace ChServerM.Hosting;

/// <summary>
/// 축을 골라 클라이언트를 조립한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 클라이언트도 서버와 <b>같은 프레이밍·디스패치·핸들러</b>를 쓴다.
/// 그래서 서버-투-서버 통신이 특별한 경로가 되지 않고, 서버 핸들러를 클라이언트에
/// 그대로 꽂을 수 있다.
/// </para>
/// <para>
/// <b>재접속 정책은 여기에 없다.</b> 백오프·재시도를 프레임워크가 감추면 상위 계층이
/// "연결이 살아 있다"고 오해해 세션 재수립(인증·상태 복원)을 건너뛴다.
/// 재접속은 이 위에서 조립한다.
/// </para>
/// <para><b>스레드 규약.</b> 빌더는 스레드 안전하지 않다.</para>
/// </remarks>
public sealed class ClientBuilder
{
    private readonly MessageDispatcherBuilder _dispatcher = new();
    private readonly FramedConnectionOptions _connectionOptions = new();

    private IClientTransport? _transport;
    private IFrameDecoder? _decoder;
    private IFrameEncoder? _encoder;
    private ITransportSecurity? _transportSecurity;
    private IServerLogger _logger = NullServerLogger.Instance;
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>연결 전송을 지정한다.</summary>
    /// <param name="transport">전송 인스턴스. 클라이언트가 소유권을 가져간다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ClientBuilder UseTransport(IClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        return this;
    }

    /// <summary>프레이밍 축을 지정한다.</summary>
    /// <param name="decoder">프레임 디코더.</param>
    /// <param name="encoder">프레임 인코더.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ClientBuilder UseFraming(IFrameDecoder decoder, IFrameEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(encoder);

        _decoder = decoder;
        _encoder = encoder;
        return this;
    }

    /// <summary>전송 보안 축을 지정한다.</summary>
    /// <param name="security">보안 구현. 지정하지 않으면 평문이다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="security"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 연결 직후·프레이밍 시작 전에 클라이언트 측 핸드셰이크가 수행된다(ADR-0017).
    /// 실패하면 <see cref="ChServerMClient.ConnectAsync"/>가
    /// <see cref="AuthenticationException"/>을 던진다 — 클라이언트의 연결 수립은
    /// 호출자 대면 경로라 상태 반환보다 예외가 자연스럽다(서버 수락 경로와 다른 점).
    /// </remarks>
    public ClientBuilder UseTransportSecurity(ITransportSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        _transportSecurity = security;
        return this;
    }

    /// <summary>진단 로거를 지정한다.</summary>
    public ClientBuilder UseLogger(IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _dispatcher.UseLogger(logger);
        return this;
    }

    /// <summary>시간 원본을 지정한다.</summary>
    public ClientBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        return this;
    }

    /// <summary>읽기 루프의 종료 정책을 설정한다.</summary>
    public ClientBuilder ConfigureConnection(Action<FramedConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_connectionOptions);
        return this;
    }

    /// <summary>서버가 보내는 메시지를 받을 핸들러를 설정한다.</summary>
    /// <remarks>
    /// 클라이언트도 서버 푸시를 받는다. 요청-응답만 쓰는 조립이라면 비워둬도 된다.
    /// </remarks>
    public ClientBuilder ConfigureDispatcher(Action<MessageDispatcherBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_dispatcher);
        return this;
    }

    /// <summary>조립을 끝내고 클라이언트를 만든다.</summary>
    /// <exception cref="InvalidOperationException">필수 축이 지정되지 않았을 때.</exception>
    public ChServerMClient Build()
    {
        IClientTransport transport = _transport
            ?? throw new InvalidOperationException(
                $"전송이 지정되지 않았다. {nameof(UseTransport)} 를 호출한다.");

        IFrameDecoder decoder = _decoder
            ?? throw new InvalidOperationException(
                $"프레이밍이 지정되지 않았다. {nameof(UseFraming)} 를 호출한다.");

        IFrameEncoder encoder = _encoder
            ?? throw new InvalidOperationException(
                $"프레이밍이 지정되지 않았다. {nameof(UseFraming)} 를 호출한다.");

        _connectionOptions.Validate();

        CompositionGuard.EnsureFrameFitsInTransportBuffer(transport, decoder, encoder);

        FramedConnectionHandler handler = new(
            decoder, _dispatcher.Build(), _connectionOptions, _timeProvider, _logger);

        return new ChServerMClient(transport, handler, encoder, _transportSecurity);
    }
}

/// <summary>
/// 조립이 끝난 클라이언트.
/// </summary>
/// <remarks>
/// 연결을 맺고, 그 커넥션의 읽기 루프를 돌린다. 읽기 루프가 없으면
/// 서버가 보낸 프레임을 아무도 처리하지 않는다 — 요청-응답만 쓰더라도
/// 응답을 받으려면 루프가 필요하다.
/// </remarks>
public sealed class ChServerMClient : IAsyncDisposable
{
    private readonly IClientTransport _transport;
    private readonly IConnectionHandler _handler;
    private readonly ITransportSecurity? _security;
    private int _disposed;

    internal ChServerMClient(
        IClientTransport transport,
        IConnectionHandler handler,
        IFrameEncoder encoder,
        ITransportSecurity? security)
    {
        _transport = transport;
        _handler = handler;
        _security = security;
        Encoder = encoder;
    }

    /// <summary>이 클라이언트가 쓰는 프레임 인코더.</summary>
    public IFrameEncoder Encoder { get; }

    /// <summary>연결을 맺고 읽기 루프를 시작한다.</summary>
    /// <param name="endPoint">연결할 주소.</param>
    /// <param name="cancellationToken">연결 시도의 취소 토큰.</param>
    /// <returns>수립된 커넥션과, 읽기 루프가 끝나면 완료되는 작업.</returns>
    /// <remarks>
    /// <para>
    /// 읽기 루프 작업을 <b>돌려준다.</b> 감추면 호출자가 루프의 실패를 관측할 수 없고,
    /// 그러면 "연결은 살아 있는데 아무 응답이 없는" 상태를 진단할 방법이 사라진다.
    /// </para>
    /// <para>
    /// 보안 축이 지정됐으면 연결 직후 핸드셰이크가 수행되고, 반환되는 커넥션의
    /// 바이트 경로는 평문 측 보안 채널이다(ADR-0017).
    /// </para>
    /// </remarks>
    /// <exception cref="AuthenticationException">보안 핸드셰이크가 실패했을 때.</exception>
    public async ValueTask<ClientSession> ConnectAsync(
        EndPoint endPoint,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        IConnection connection = await _transport.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

        if (_security is not null)
        {
            connection = await SecureAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        return new ClientSession(connection, _handler.RunAsync(connection));
    }

    /// <summary>클라이언트 측 핸드셰이크를 수행하고 보안 커넥션으로 감싼다.</summary>
    private async ValueTask<IConnection> SecureAsync(IConnection connection, CancellationToken cancellationToken)
    {
        SecureChannelResult result;
        using (CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed, cancellationToken))
        {
            result = await _security!
                .SecureAsClientAsync(new ConnectionDuplexPipe(connection), linked.Token)
                .ConfigureAwait(false);
        }

        if (!result.IsEstablished)
        {
            // 실패한 원시 커넥션을 반드시 정리한다 — 남겨두면 소켓이 샌다.
            await connection.DisposeAsync().ConfigureAwait(false);

            if (result.Status == SecureChannelStatus.Canceled)
            {
                throw new OperationCanceledException("보안 채널 확립이 취소됐다.");
            }

            throw new AuthenticationException(
                "보안 채널 확립에 실패했다. 서버 인증서 신뢰·프로토콜 버전 설정을 확인한다.");
        }

        return new SecuredConnection(connection, result.Channel!);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>연결 하나와 그 읽기 루프.</summary>
/// <param name="Connection">수립된 커넥션.</param>
/// <param name="Completion">읽기 루프가 끝나면 완료되는 작업.</param>
public readonly record struct ClientSession(IConnection Connection, Task Completion);
