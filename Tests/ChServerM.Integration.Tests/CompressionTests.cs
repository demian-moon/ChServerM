using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Compression;
using ChServerM.Compression.LZ4;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 압축 축(T-11·T-18)의 종단 검증 — <b>압축이 실제로 실행되고</b>(레거시 무동작의 역),
/// 정책이 지켜지고, 위반(미조립·폭탄)이 커넥션 종료로 드러남을 고정한다.
/// </summary>
/// <remarks>
/// <para>고정하는 것:</para>
/// <list type="bullet">
///   <item><description>큰 압축성 페이로드 = 와이어에서 실제로 줄어들고(계수 코덱으로 검증)
///   핸들러는 평문을 <c>Compressed</c> 플래그 없이 받는다</description></item>
///   <item><description>문턱 미만·제외 목록·비압축성 = 평문 송신(코덱 호출 여부까지 검증)</description></item>
///   <item><description>코덱 미조립 서버에 압축 프레임 = 응답 없이 종료 + 서버 생존</description></item>
///   <item><description>압축 폭탄(선언 길이 &gt; 해제 상한) = 종료 + 서버 생존(T-18)</description></item>
///   <item><description>조각화 + 압축 조합 — 재조립 후 해제 순서</description></item>
/// </list>
/// </remarks>
public sealed class CompressionTests : IDisposable
{
    private const ushort EchoId = 500;
    private const ushort SecretId = 501; // 압축 제외 목록 대상

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    /// <summary>압축이 잘 되는 대표 페이로드.</summary>
    private static byte[] Compressible(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 16);
        }

        return data;
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Large_payload_is_actually_compressed_and_roundtrips(TransportKind kind)
    {
        FramingOptions framing = new() { MaxPayloadLength = 32 * 1024 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        CountingCodec serverCodec = new();
        CountingCodec clientCodec = new();
        PayloadCompressionOptions policy = new();

        (IServerTransport serverTransport, IClientTransport clientTransport, EndPoint? knownEndPoint) =
            CreateTransports(kind, "comp-ok");

        TaskCompletionSource<(FrameFlags Flags, byte[] Payload)> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UsePayloadCodec(serverCodec)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), async context =>
            {
                received.TrySetResult((context.Envelope.Flags, context.Payload.ToArray()));
                // 응답도 압축 경로로 — 코덱·플래그를 모르는 평범한 핸들러 코드다.
                await FrameWriter.WriteCompressedFrameAsync(
                    context.Connection.Output, serverEncoder, serverCodec, policy,
                    context.Envelope.MessageId, context.Payload.ToArray(),
                    context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = knownEndPoint ?? server.LocalEndPoint!;

        TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(clientTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UsePayloadCodec(clientCodec)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                echoed.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(target, _timeout.Token);

        byte[] payload = Compressible(16 * 1024);
        await FrameWriter.WriteCompressedFrameAsync(
            session.Connection.Output, client.Encoder, clientCodec, policy,
            new MessageId(EchoId), payload, sequence: 1, session.Connection.ConnectionClosed);

        (FrameFlags serverSeenFlags, byte[] serverSeenPayload) = await received.Task.WaitAsync(_timeout.Token);

        // 핸들러는 평문을 본다 — 플래그가 남아 있으면 거짓말이다.
        Assert.Equal(FrameFlags.None, serverSeenFlags & FrameFlags.Compressed);
        Assert.Equal(payload, serverSeenPayload);

        // 압축이 실제로 실행됐고(레거시 무동작의 역), 와이어가 실제로 줄었다.
        Assert.Equal(1, clientCodec.EncodeCalls);
        Assert.True(clientCodec.LastEncodedLength < payload.Length,
            $"압축 결과({clientCodec.LastEncodedLength}B)가 원본({payload.Length}B)보다 작지 않다.");
        Assert.Equal(1, serverCodec.DecodeCalls);

        // 왕복 — 서버 응답도 압축 경로를 거쳐 원문으로 돌아온다.
        Assert.Equal(payload, await echoed.Task.WaitAsync(_timeout.Token));
        Assert.Equal(1, clientCodec.DecodeCalls);
    }

    [Fact]
    public async Task Policy_skips_small_excluded_and_incompressible_payloads()
    {
        FramingOptions framing = new() { MaxPayloadLength = 32 * 1024 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        CountingCodec serverCodec = new();
        CountingCodec clientCodec = new();
        PayloadCompressionOptions policy = new PayloadCompressionOptions()
            .DoNotCompress(new MessageId(SecretId)); // T-11: 비밀 문맥은 압축하지 않는다

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"comp-policy-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        TaskCompletionSource<byte[]>[] received =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
        int index = -1;

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UsePayloadCodec(serverCodec)
            .ConfigureDispatcher(dispatcher =>
            {
                MessageDelegate collect = context =>
                {
                    received[Interlocked.Increment(ref index)].TrySetResult(context.Payload.ToArray());
                    return ValueTask.FromResult(DispatchStatus.Handled);
                };
                dispatcher.MapRaw(new MessageId(EchoId), collect).MapRaw(new MessageId(SecretId), collect);
            })
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .UsePayloadCodec(clientCodec)
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        // 1. 문턱(1024B) 미만 — 코덱이 호출조차 되지 않는다.
        byte[] small = Compressible(100);
        await FrameWriter.WriteCompressedFrameAsync(
            session.Connection.Output, client.Encoder, clientCodec, policy,
            new MessageId(EchoId), small, sequence: 1, session.Connection.ConnectionClosed);
        Assert.Equal(small, await received[0].Task.WaitAsync(_timeout.Token));
        Assert.Equal(0, clientCodec.EncodeCalls);

        // 2. 제외 목록(T-11) — 크기가 커도 압축하지 않는다.
        byte[] secret = Compressible(8 * 1024);
        await FrameWriter.WriteCompressedFrameAsync(
            session.Connection.Output, client.Encoder, clientCodec, policy,
            new MessageId(SecretId), secret, sequence: 2, session.Connection.ConnectionClosed);
        Assert.Equal(secret, await received[1].Task.WaitAsync(_timeout.Token));
        Assert.Equal(0, clientCodec.EncodeCalls);

        // 3. 비압축성(랜덤) — 시도는 하되 이득이 없으면 평문으로 나간다.
        byte[] random = new byte[8 * 1024];
        Random.Shared.NextBytes(random);
        await FrameWriter.WriteCompressedFrameAsync(
            session.Connection.Output, client.Encoder, clientCodec, policy,
            new MessageId(EchoId), random, sequence: 3, session.Connection.ConnectionClosed);
        Assert.Equal(random, await received[2].Task.WaitAsync(_timeout.Token));
        Assert.Equal(1, clientCodec.EncodeCalls);

        // 전부 평문으로 도착했다 — 서버 코덱은 한 번도 해제하지 않았다.
        Assert.Equal(0, serverCodec.DecodeCalls);
    }

    [Fact]
    public async Task Compressed_frame_without_codec_closes_connection_and_server_survives()
    {
        FramingOptions framing = new() { MaxPayloadLength = 32 * 1024 };
        FixedHeaderFrameEncoder encoder = new(framing);
        TcpTransportOptions tcpOptions = new();

        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 압축 축을 조립하지 않은 서버.
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                received.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = server.LocalEndPoint!;

        // 1. Compressed 플래그 프레임 — 압축된 바이트가 조용히 핸들러에 가면 안 된다. 종료다.
        Lz4PayloadCodec codec = new();
        byte[] payload = Compressible(4 * 1024);
        byte[] blob = new byte[codec.MaxEncodedLength(payload.Length)];
        int encoded = codec.Encode(payload, blob);

        await using (TcpClientTransport rawTransport = new(tcpOptions))
        {
            IConnection raw = await rawTransport.ConnectAsync(target, _timeout.Token);
            await FrameWriter.WriteFrameAsync(
                raw.Output, encoder, new MessageId(EchoId), blob.AsSpan(0, encoded),
                FrameFlags.Compressed, sequence: 1, raw.ConnectionClosed);

            System.IO.Pipelines.ReadResult read = await raw.Input.ReadAsync(_timeout.Token);
            Assert.True(read.IsCompleted);
            await raw.DisposeAsync();
        }

        Assert.False(received.Task.IsCompleted, "압축 프레임이 핸들러에 전달됐다 — 조용한 실패다.");

        // 2. 실패 이후에도 평문 클라이언트는 정상 처리된다.
        await using TcpClientTransport plainTransport = new(tcpOptions);
        IConnection plain = await plainTransport.ConnectAsync(target, _timeout.Token);
        byte[] plainPayload = [1, 2, 3];
        await FrameWriter.WriteFrameAsync(
            plain.Output, encoder, new MessageId(EchoId), plainPayload,
            FrameFlags.None, sequence: 2, plain.ConnectionClosed);

        Assert.Equal(plainPayload, await received.Task.WaitAsync(_timeout.Token));
        await plain.DisposeAsync();
    }

    [Fact]
    public async Task Compression_bomb_is_rejected_and_server_survives()
    {
        FramingOptions framing = new() { MaxPayloadLength = 32 * 1024 };
        FixedHeaderFrameEncoder encoder = new(framing);
        TcpTransportOptions tcpOptions = new();

        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .UsePayloadCodec(new Lz4PayloadCodec())
            .ConfigureConnection(options => options.MaxDecompressedMessageLength = 8 * 1024)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                received.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint target = server.LocalEndPoint!;

        // 1. 폭탄 — 몇 바이트짜리 프레임이 1GiB 해제를 선언한다(T-18). 할당 없이 종료돼야 한다.
        byte[] bomb = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bomb, 1024u * 1024 * 1024);

        await using (TcpClientTransport bombTransport = new(tcpOptions))
        {
            IConnection raw = await bombTransport.ConnectAsync(target, _timeout.Token);
            await FrameWriter.WriteFrameAsync(
                raw.Output, encoder, new MessageId(EchoId), bomb,
                FrameFlags.Compressed, sequence: 1, raw.ConnectionClosed);

            System.IO.Pipelines.ReadResult read = await raw.Input.ReadAsync(_timeout.Token);
            Assert.True(read.IsCompleted);
            await raw.DisposeAsync();
        }

        // 2. 폭탄 이후에도 정상 압축 클라이언트는 처리된다.
        Lz4PayloadCodec codec = new();
        byte[] payload = Compressible(4 * 1024);
        byte[] blob = new byte[codec.MaxEncodedLength(payload.Length)];
        int encoded = codec.Encode(payload, blob);

        await using TcpClientTransport goodTransport = new(tcpOptions);
        IConnection good = await goodTransport.ConnectAsync(target, _timeout.Token);
        await FrameWriter.WriteFrameAsync(
            good.Output, encoder, new MessageId(EchoId), blob.AsSpan(0, encoded),
            FrameFlags.Compressed, sequence: 2, good.ConnectionClosed);

        Assert.Equal(payload, await received.Task.WaitAsync(_timeout.Token));
        await good.DisposeAsync();
    }

    [Fact]
    public async Task Fragmented_compressed_message_reassembles_then_decompresses()
    {
        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameEncoder encoder = new(framing);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"comp-frag-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
            .UsePayloadCodec(new Lz4PayloadCodec())
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                received.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        // 압축(전체 메시지) → 조각화 순서 — 각 조각이 Compressed 플래그를 지니고,
        // 수신은 재조립 → 해제 순서다(FragmentAssembler 가 플래그를 보존한다).
        // 페이로드는 비압축성(랜덤) — 압축이 잘 되는 데이터는 블롭이 프레임 상한보다
        // 작아져 조각화 자체가 일어나지 않는다.
        Lz4PayloadCodec codec = new();
        byte[] payload = new byte[16 * 1024];
        Random.Shared.NextBytes(payload);
        byte[] blob = new byte[codec.MaxEncodedLength(payload.Length)];
        int encoded = codec.Encode(payload, blob);
        Assert.True(encoded > framing.MaxPayloadLength, "조각화가 일어나도록 블롭이 프레임 상한보다 커야 한다.");

        await FrameWriter.WriteFragmentedFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(EchoId),
            blob.AsMemory(0, encoded), maxFragmentPayloadLength: framing.MaxPayloadLength,
            FrameFlags.Compressed, sequence: 1, session.Connection.ConnectionClosed);

        Assert.Equal(payload, await received.Task.WaitAsync(_timeout.Token));
    }

    /// <summary>전송 종류에 따라 달라지는 유일한 지점 — TestHarness 와 같은 규약.</summary>
    private static (IServerTransport Server, IClientTransport Client, EndPoint? KnownEndPoint) CreateTransports(
        TransportKind kind, string name)
    {
        if (kind == TransportKind.InMemory)
        {
            InMemoryTransportOptions options = new();
            InMemoryTransportHub hub = new();
            InMemoryEndPoint endPoint = new($"{name}-{Guid.NewGuid():N}");
            return (new InMemoryServerTransport(hub, endPoint, options), new InMemoryClientTransport(hub, null, options), endPoint);
        }

        TcpTransportOptions tcpOptions = new();
        return (new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 0), tcpOptions), new TcpClientTransport(tcpOptions), null);
    }

    /// <summary>호출·크기를 계수하는 코덱 데코레이터 — "실제로 압축됐는가"의 관측 지점.</summary>
    private sealed class CountingCodec : IPayloadCodec
    {
        private readonly Lz4PayloadCodec _inner = new();
        private int _encodeCalls;
        private int _decodeCalls;

        public int EncodeCalls => Volatile.Read(ref _encodeCalls);

        public int DecodeCalls => Volatile.Read(ref _decodeCalls);

        public int LastEncodedLength { get; private set; }

        public int MaxEncodedLength(int sourceLength) => _inner.MaxEncodedLength(sourceLength);

        public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            Interlocked.Increment(ref _encodeCalls);
            int written = _inner.Encode(source, destination);
            LastEncodedLength = written;
            return written;
        }

        public bool TryDecode(
            in ReadOnlySequence<byte> source,
            IBufferWriter<byte> destination,
            int maxDecodedLength,
            out int decodedLength)
        {
            Interlocked.Increment(ref _decodeCalls);
            return _inner.TryDecode(source, destination, maxDecodedLength, out decodedLength);
        }
    }
}
