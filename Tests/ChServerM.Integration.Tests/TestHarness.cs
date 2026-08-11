using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Transport.Http;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;

namespace ChServerM.Integration.Tests;

/// <summary>어느 전송으로 조립할지.</summary>
/// <remarks>
/// <c>[Theory]</c> 인자로 쓰이므로 <c>public</c> 이어야 한다 — xUnit 이 테스트 메서드
/// 시그니처를 통해 접근한다.
/// </remarks>
#pragma warning disable CA1515 // xUnit 이 [Theory] 인자 타입에 접근해야 하므로 internal 로 낮출 수 없다.
public enum TransportKind
{
    /// <summary>프로세스 내 루프백.</summary>
    InMemory,

    /// <summary>실제 TCP 소켓(루프백 주소).</summary>
    Tcp,

    /// <summary>Kestrel 기반 HTTP/2 스트림(루프백 주소).</summary>
    Http,
}
#pragma warning restore CA1515

/// <summary>
/// 축을 조립해 굴리는 종단 테스트용 하네스.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 클래스의 생김새 자체가 검증 대상이다.</b> 전송 종류에 따라 달라지는 코드가
/// <see cref="CreateTransportsAsync"/> 한 곳뿐이고, 그 아래(프레이밍·디스패치·핸들러·
/// 송수신)는 전부 공통이다. 여기에 전송별 분기가 새로 생긴다면 추상화가 새고 있는 것이다.
/// </para>
/// <para>
/// TCP 는 포트 0 으로 바인드해 OS 가 배정한 포트를 쓴다. 포트를 하드코딩하면
/// 병렬 실행에서 충돌하고, CI 에서 산발적으로 실패한다.
/// </para>
/// </remarks>
internal sealed class TestHarness : IAsyncDisposable
{
    private readonly InMemoryTransportHub? _hub;

    private TestHarness(
        TransportKind kind,
        InMemoryTransportHub? hub,
        EndPoint endPoint,
        IServerTransport server,
        IClientTransport client,
        IFrameEncoder encoder,
        IFrameDecoder decoder)
    {
        Kind = kind;
        _hub = hub;
        EndPoint = endPoint;
        Server = server;
        Client = client;
        Encoder = encoder;
        Decoder = decoder;
    }

    public TransportKind Kind { get; }

    public EndPoint EndPoint { get; }

    public IServerTransport Server { get; }

    public IClientTransport Client { get; }

    public IFrameEncoder Encoder { get; }

    public IFrameDecoder Decoder { get; }

    /// <summary>서버가 들고 있는 커넥션 수.</summary>
    /// <remarks>
    /// 이것만 전송별 분기가 남는다 — <c>IServerTransport</c> 에 올릴 만한 값이 아니기 때문이다.
    /// 커넥션 수는 메트릭(Phase 11)의 몫이지 전송 계약의 일부가 아니다.
    /// </remarks>
    public int ServerConnectionCount => Server switch
    {
        InMemoryServerTransport inMemory => inMemory.ConnectionCount,
        TcpServerTransport tcp => tcp.ConnectionCount,
        HttpServerTransport http => http.ConnectionCount,
        _ => throw new NotSupportedException($"알 수 없는 전송: {Server.GetType().Name}"),
    };

    /// <summary>인메모리 허브가 듣고 있는 종단 수. TCP 에서는 항상 0.</summary>
    public int ListenerCount => _hub?.ListenerCount ?? 0;

    /// <summary>인메모리 클라이언트의 종단. TCP 에서는 <see langword="null"/>.</summary>
    public InMemoryEndPoint? InMemoryClientEndPoint =>
        Client is InMemoryClientTransport inMemory ? inMemory.LocalEndPoint : null;

    /// <summary>디스패처를 구성해 서버를 세우고 바인드까지 마친다.</summary>
    /// <param name="configure">라우팅과 미들웨어 구성.</param>
    /// <param name="kind">조립할 전송.</param>
    /// <param name="connectionOptions">읽기 루프의 종료 정책.</param>
    /// <param name="transportOptions">인메모리 전송 설정.</param>
    /// <param name="tcpOptions">TCP 전송 설정.</param>
    /// <param name="maxPayloadLength">프레임 페이로드 상한.</param>
    /// <param name="decoder">프레임 디코더. <see langword="null"/>이면 고정 헤더.</param>
    /// <param name="encoder">프레임 인코더. <see langword="null"/>이면 고정 헤더. 디코더와 같은 와이어여야 한다.</param>
    /// <param name="executionModel">실행 모델. <see langword="null"/>이면 호출 스레드에서 그대로 디스패치한다. 수명은 호출자가 소유한다.</param>
    public static async Task<TestHarness> StartAsync(
        Action<MessageDispatcherBuilder> configure,
        TransportKind kind = TransportKind.InMemory,
        FramedConnectionOptions? connectionOptions = null,
        InMemoryTransportOptions? transportOptions = null,
        TcpTransportOptions? tcpOptions = null,
        int maxPayloadLength = 4096,
        IFrameDecoder? decoder = null,
        IFrameEncoder? encoder = null,
        ChServerM.Execution.IExecutionModel? executionModel = null)
    {
        ArgumentNullException.ThrowIfNull(configure);

        MessageDispatcherBuilder builder = new();
        configure(builder);

        // 기본은 고정 헤더 쌍. 프레이밍 축 교체 테스트는 varint 쌍을 주입한다.
        FramingOptions framing = new() { MaxPayloadLength = maxPayloadLength };
        decoder ??= new FixedHeaderFrameDecoder(framing);
        encoder ??= new FixedHeaderFrameEncoder(framing);

        FramedConnectionHandler handler = new(
            decoder, builder.Build(), connectionOptions, executionModel: executionModel);

        (InMemoryTransportHub? hub, EndPoint endPoint, IServerTransport server, IClientTransport client) =
            await CreateTransportsAsync(kind, handler, transportOptions, tcpOptions, maxPayloadLength, encoder.MaxHeaderSize)
                .ConfigureAwait(false);

        return new TestHarness(kind, hub, endPoint, server, client, encoder, decoder);
    }

    /// <summary>프레임이 들어갈 수 있는 버퍼 임계값을 계산한다.</summary>
    /// <remarks>
    /// 전송 버퍼가 최대 프레임보다 작으면 그 크기의 프레임에서 <b>조용히 교착한다</b> —
    /// 디코더는 부분 프레임을 소비할 수 없고, 버퍼가 차면 쓰기가 멈추기 때문이다.
    /// 조립 검사가 이것을 예외로 잡아주지만, 테스트는 애초에 성립하는 조합을 써야 한다.
    /// </remarks>
    private static (long Pause, long Resume) BufferThresholdsFor(int maxPayloadLength, int maxHeaderSize)
    {
        long minimum = maxPayloadLength + maxHeaderSize;
        long pause = Math.Max(InMemoryTransportOptions.DefaultPauseWriterThreshold, minimum * 2);

        return (pause, pause / 2);
    }

    /// <summary>전송 종류에 따라 달라지는 유일한 지점.</summary>
    private static async Task<(InMemoryTransportHub? Hub, EndPoint EndPoint, IServerTransport Server, IClientTransport Client)>
        CreateTransportsAsync(
            TransportKind kind,
            IConnectionHandler handler,
            InMemoryTransportOptions? transportOptions,
            TcpTransportOptions? tcpOptions,
            int maxPayloadLength,
            int maxHeaderSize)
    {
        (long pause, long resume) = BufferThresholdsFor(maxPayloadLength, maxHeaderSize);

        if (kind == TransportKind.InMemory)
        {
            transportOptions ??= new InMemoryTransportOptions();
            transportOptions.PauseWriterThreshold = Math.Max(transportOptions.PauseWriterThreshold, pause);
            transportOptions.ResumeWriterThreshold = Math.Max(transportOptions.ResumeWriterThreshold, resume);

            // 허브는 하네스마다 새로 만든다 — xUnit 은 클래스 단위 병렬이라
            // 정적이었다면 테스트끼리 종단 이름이 충돌한다.
            InMemoryTransportHub hub = new();
            InMemoryEndPoint endPoint = new($"test-{Guid.NewGuid():N}");
            InMemoryServerTransport server = new(hub, endPoint, transportOptions);

            await server.BindAsync(handler).ConfigureAwait(false);

            return (hub, endPoint, server, new InMemoryClientTransport(hub, null, transportOptions));
        }

        if (kind == TransportKind.Http)
        {
            // HTTP/2 흐름 제어 윈도가 이 전송의 버퍼 임계값이다. 최대 프레임이 들어가야 한다.
            HttpTransportOptions httpOptions = new()
            {
                StreamReceiveWindowSize = (int)Math.Max(HttpTransportOptions.DefaultStreamReceiveWindowSize, pause),
            };

            HttpServerTransport httpServer = new(new IPEndPoint(IPAddress.Loopback, 0), httpOptions);
            await httpServer.BindAsync(handler).ConfigureAwait(false);

            EndPoint httpActual = httpServer.LocalEndPoint
                ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

            return (null, httpActual, httpServer, new HttpClientTransport(httpOptions));
        }

        tcpOptions ??= new TcpTransportOptions();
        tcpOptions.PauseWriterThreshold = Math.Max(tcpOptions.PauseWriterThreshold, pause);
        tcpOptions.ResumeWriterThreshold = Math.Max(tcpOptions.ResumeWriterThreshold, resume);

        // 포트 0 → OS 가 배정. 바인드 뒤에 실제 포트를 읽는다.
        TcpServerTransport tcpServer = new(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions);
        await tcpServer.BindAsync(handler).ConfigureAwait(false);

        EndPoint actual = tcpServer.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        return (null, actual, tcpServer, new TcpClientTransport(tcpOptions));
    }

    /// <summary>클라이언트 커넥션을 하나 연다.</summary>
    public async Task<IConnection> ConnectAsync() =>
        await Client.ConnectAsync(EndPoint).ConfigureAwait(false);

    /// <summary>프레임 하나를 보내고 내보낸다.</summary>
    public ValueTask<FlushResult> SendAsync(IConnection connection, ushort messageId, ReadOnlySpan<byte> payload) =>
        connection.WriteFrameAsync(Encoder, new MessageId(messageId), payload, FrameFlags.None, sequence: 0);

    /// <summary>프레임 하나가 도착할 때까지 읽는다.</summary>
    /// <returns>헤더와 페이로드 복사본.</returns>
    /// <exception cref="InvalidOperationException">프레임이 오기 전에 스트림이 끝났을 때.</exception>
    /// <remarks>
    /// 페이로드를 <c>ToArray()</c> 로 복사한다. 프로덕션 코드였다면 이것이 결함이지만,
    /// 테스트는 <c>AdvanceTo</c> 이후에도 값을 비교해야 하므로 여기서는 의도적이다.
    /// </remarks>
    public async Task<(MessageEnvelope Envelope, byte[] Payload)> ReceiveAsync(
        IConnection connection,
        CancellationToken cancellationToken = default)
    {
        PipeReader reader = connection.Input;

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            FrameDecodeResult decoded = Decoder.Decode(buffer);

            if (decoded.IsDecoded)
            {
                byte[] payload = decoded.Payload.ToArray();
                reader.AdvanceTo(decoded.Consumed, decoded.Examined);
                return (decoded.Envelope, payload);
            }

            reader.AdvanceTo(decoded.Consumed, decoded.Examined);

            if (decoded.IsFatal)
            {
                throw new InvalidOperationException($"응답 프레임 디코딩 실패: {decoded.Status}");
            }

            if (read.IsCompleted)
            {
                throw new InvalidOperationException("프레임이 도착하기 전에 스트림이 끝났다.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}
