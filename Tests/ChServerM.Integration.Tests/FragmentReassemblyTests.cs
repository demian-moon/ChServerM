using System;
using System.Buffers;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 조각 재조립(<see cref="FrameFlags.Fragmented"/>/<see cref="FrameFlags.EndOfMessage"/>)의
/// 정상 경로와 계약 위반 경로 검증 (ADR-0015).
/// </summary>
/// <remarks>
/// 계약: 조각은 연속·동일 <c>MessageId</c>, 마지막 조각에만 <c>EndOfMessage</c>,
/// 누적 상한(<see cref="FramedConnectionOptions.MaxAssembledMessageLength"/>) 초과 금지.
/// 위반은 전부 커넥션 종료다 — 조각 상태가 어긋난 채 계속하면 재조립 결과가 조용히
/// 오염되기 때문이다. 종료는 클라이언트 쪽 스트림 종단으로 관측한다.
/// </remarks>
public sealed class FragmentReassemblyTests
{
    private const ushort CollectId = 500;
    private const ushort OtherId = 501;
    private const int MaxPayload = 4096;

    private static readonly FramingOptions Framing = new() { MaxPayloadLength = MaxPayload };

    private sealed record Collected(FrameFlags Flags, uint Sequence, byte[] Payload);

    /// <summary>수신 메시지를 전부 채널로 모으는 서버를 세운다.</summary>
    /// <remarks>유계 채널(9.6) + <c>WriteAsync</c> — 테스트 코드도 교본 규약을 지킨다.</remarks>
    private static (Task<TestHarness> Harness, ChannelReader<Collected> Received, FixedHeaderFrameEncoder Encoder)
        StartCollectorAsync(FramedConnectionOptions? options = null)
    {
        FixedHeaderFrameEncoder encoder = new(Framing);
        Channel<Collected> received = Channel.CreateBounded<Collected>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        Task<TestHarness> harness = TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(CollectId), async context =>
            {
                Collected item = new(
                    context.Envelope.Flags, context.Envelope.Sequence, context.Payload.ToArray());
                await received.Writer.WriteAsync(item, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }),
            connectionOptions: options,
            maxPayloadLength: MaxPayload,
            decoder: new FixedHeaderFrameDecoder(Framing),
            encoder: encoder);

        return (harness, received.Reader, encoder);
    }

    [Fact]
    public async Task LargeMessage_SplitBySender_ArrivesAssembled()
    {
        (Task<TestHarness> starting, ChannelReader<Collected> received, FixedHeaderFrameEncoder encoder) =
            StartCollectorAsync();
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        // 페이로드 상한(4096)의 24배 + 자투리 — 조각 25개.
        byte[] payload = new byte[MaxPayload * 24 + 123];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await FrameWriter.WriteFragmentedFrameAsync(
            connection.Output, encoder, new MessageId(CollectId), payload,
            maxFragmentPayloadLength: MaxPayload, FrameFlags.None, sequence: 7,
            connection.ConnectionClosed);

        Collected collected = await received.ReadAsync(TestTimeout.Token);

        // 핸들러가 보는 것은 조각의 흔적이 없는 완성 메시지다.
        Assert.Equal(FrameFlags.None, collected.Flags);
        Assert.Equal(7u, collected.Sequence);
        Assert.Equal(payload, collected.Payload);
    }

    [Fact]
    public async Task EmptyMessage_SingleFragment_ArrivesAssembled()
    {
        (Task<TestHarness> starting, ChannelReader<Collected> received, FixedHeaderFrameEncoder encoder) =
            StartCollectorAsync();
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        await FrameWriter.WriteFragmentedFrameAsync(
            connection.Output, encoder, new MessageId(CollectId), ReadOnlyMemory<byte>.Empty,
            maxFragmentPayloadLength: MaxPayload, FrameFlags.None, sequence: 1,
            connection.ConnectionClosed);

        Collected collected = await received.ReadAsync(TestTimeout.Token);
        Assert.Empty(collected.Payload);
    }

    [Fact]
    public async Task NormalFrame_BetweenFragments_ClosesConnection()
    {
        (Task<TestHarness> starting, _, FixedHeaderFrameEncoder encoder) = StartCollectorAsync();
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [1, 2], FrameFlags.Fragmented, 0);
        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [3, 4], FrameFlags.None, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task EndOfMessage_WithoutFragmented_ClosesConnection()
    {
        (Task<TestHarness> starting, _, FixedHeaderFrameEncoder encoder) = StartCollectorAsync();
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [1], FrameFlags.EndOfMessage, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task DifferentMessageId_BetweenFragments_ClosesConnection()
    {
        (Task<TestHarness> starting, _, FixedHeaderFrameEncoder encoder) = StartCollectorAsync();
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [1], FrameFlags.Fragmented, 0);
        await connection.WriteFrameAsync(encoder, new MessageId(OtherId), [2], FrameFlags.Fragmented, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task AssembledLength_OverLimit_ClosesConnection()
    {
        // 상한 10KB — 4KB 조각 3개째에서 넘는다. 마지막 조각이 영원히 오지 않아도
        // 이 상한이 메모리를 끊는다는 것이 이 테스트의 요점이다(ADR-0015).
        (Task<TestHarness> starting, _, FixedHeaderFrameEncoder encoder) = StartCollectorAsync(
            new FramedConnectionOptions { MaxAssembledMessageLength = 10_000 });
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        byte[] chunk = new byte[MaxPayload];
        for (int i = 0; i < 3; i++)
        {
            await connection.WriteFrameAsync(encoder, new MessageId(CollectId), chunk, FrameFlags.Fragmented, 0);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task ReassemblyDisabled_FragmentClosesConnection()
    {
        // MaxAssembledMessageLength=0 — 조각을 안 쓰는 프로필은 받는 즉시 끊는다.
        (Task<TestHarness> starting, _, FixedHeaderFrameEncoder encoder) = StartCollectorAsync(
            new FramedConnectionOptions { MaxAssembledMessageLength = 0 });
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [1], FrameFlags.Fragmented, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task ReassemblyState_ResetsBetweenMessages()
    {
        // 완성 → 일반 프레임 → 다시 조각 — 재조립 상태가 메시지마다 깨끗이 돌아가는지.
        (Task<TestHarness> starting, ChannelReader<Collected> received, FixedHeaderFrameEncoder encoder) =
            StartCollectorAsync();
        await using TestHarness harness = await starting;
        await using IConnection connection = await harness.ConnectAsync();

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [10], FrameFlags.Fragmented, 0);
        await connection.WriteFrameAsync(
            encoder, new MessageId(CollectId), [20], FrameFlags.Fragmented | FrameFlags.EndOfMessage, 0);

        Assert.Equal(new byte[] { 10, 20 }, (await received.ReadAsync(TestTimeout.Token)).Payload);

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [30], FrameFlags.None, 0);
        Assert.Equal(new byte[] { 30 }, (await received.ReadAsync(TestTimeout.Token)).Payload);

        await connection.WriteFrameAsync(encoder, new MessageId(CollectId), [40], FrameFlags.Fragmented, 0);
        await connection.WriteFrameAsync(
            encoder, new MessageId(CollectId), [50], FrameFlags.Fragmented | FrameFlags.EndOfMessage, 0);

        Assert.Equal(new byte[] { 40, 50 }, (await received.ReadAsync(TestTimeout.Token)).Payload);
    }
}
