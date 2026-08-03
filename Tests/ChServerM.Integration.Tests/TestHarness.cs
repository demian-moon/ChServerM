using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 축을 조립해 굴리는 종단 테스트용 하네스.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 클래스의 생김새 자체가 검증 대상이다.</b> 서버 하나를 세우는 데 필요한 것이
/// (전송, 프레이밍, 디스패처) 셋뿐이고 각각이 인터페이스라면, 축 교체가 실제로 성립한다는 뜻이다.
/// 여기에 전송별 분기가 하나라도 생기면 추상화가 새고 있는 것이다.
/// </para>
/// <para>
/// 허브는 하네스마다 새로 만든다 — xUnit 은 클래스 단위로 병렬 실행하므로,
/// 정적 허브였다면 테스트끼리 종단 이름이 충돌한다.
/// </para>
/// </remarks>
internal sealed class TestHarness : IAsyncDisposable
{
    private readonly InMemoryTransportHub _hub;

    private TestHarness(
        InMemoryTransportHub hub,
        InMemoryEndPoint endPoint,
        InMemoryServerTransport server,
        InMemoryClientTransport client,
        FixedHeaderFrameEncoder encoder,
        FixedHeaderFrameDecoder decoder)
    {
        _hub = hub;
        EndPoint = endPoint;
        Server = server;
        Client = client;
        Encoder = encoder;
        Decoder = decoder;
    }

    public InMemoryEndPoint EndPoint { get; }

    public InMemoryServerTransport Server { get; }

    public InMemoryClientTransport Client { get; }

    public FixedHeaderFrameEncoder Encoder { get; }

    public FixedHeaderFrameDecoder Decoder { get; }

    public int ListenerCount => _hub.ListenerCount;

    /// <summary>디스패처를 구성해 서버를 세우고 바인드까지 마친다.</summary>
    /// <param name="configure">라우팅과 미들웨어 구성.</param>
    /// <param name="connectionOptions">읽기 루프의 종료 정책.</param>
    /// <param name="transportOptions">전송 설정(백프레셔 임계값, 동시 접속 상한).</param>
    /// <param name="maxPayloadLength">프레임 페이로드 상한.</param>
    public static async Task<TestHarness> StartAsync(
        Action<MessageDispatcherBuilder> configure,
        FramedConnectionOptions? connectionOptions = null,
        InMemoryTransportOptions? transportOptions = null,
        int maxPayloadLength = 4096)
    {
        ArgumentNullException.ThrowIfNull(configure);

        MessageDispatcherBuilder builder = new();
        configure(builder);

        FramingOptions framing = new() { MaxPayloadLength = maxPayloadLength };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        FramedConnectionHandler handler = new(decoder, builder.Build(), connectionOptions);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"test-{Guid.NewGuid():N}");
        InMemoryServerTransport server = new(hub, endPoint, transportOptions);

        await server.BindAsync(handler).ConfigureAwait(false);

        return new TestHarness(hub, endPoint, server, new InMemoryClientTransport(hub), encoder, decoder);
    }

    /// <summary>클라이언트 커넥션을 하나 연다.</summary>
    public async Task<IConnection> ConnectAsync() =>
        await Client.ConnectAsync(EndPoint).ConfigureAwait(false);

    /// <summary>프레임 하나를 보내고 내보낸다.</summary>
    public ValueTask<FlushResult> SendAsync(IConnection connection, ushort messageId, ReadOnlySpan<byte> payload) =>
        connection.WriteFrameAsync(Encoder, new MessageId(messageId), payload);

    /// <summary>프레임 하나가 도착할 때까지 읽는다.</summary>
    /// <returns>헤더와 페이로드 복사본.</returns>
    /// <exception cref="InvalidOperationException">프레임이 오기 전에 스트림이 끝났을 때.</exception>
    /// <remarks>
    /// 페이로드를 <c>ToArray()</c> 로 복사한다. 프로덕션 코드였다면 이것이 결함이지만,
    /// 테스트는 <c>AdvanceTo</c> 이후에도 값을 비교해야 하므로 여기서는 의도적이다.
    /// </remarks>
    public async Task<(FrameHeader Header, byte[] Payload)> ReceiveAsync(
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
                return (decoded.Header, payload);
            }

            if (decoded.IsFatal)
            {
                reader.AdvanceTo(decoded.Consumed, decoded.Examined);
                throw new InvalidOperationException($"응답 프레임 디코딩 실패: {decoded.Status}");
            }

            reader.AdvanceTo(decoded.Consumed, decoded.Examined);

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
