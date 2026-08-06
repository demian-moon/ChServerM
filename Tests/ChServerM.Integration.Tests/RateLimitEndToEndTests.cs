using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 속도 제한의 종단 검증 — 제한 초과 프레임은 버려지되 <b>커넥션은 살아 있고</b>,
/// 리필 후 다시 처리됨을 고정한다(일시적 제한 = 종료 아님, 재접속 폭풍 방지).
/// </summary>
/// <remarks>
/// 드롭 확인에 <c>DispatchFailures{RejectedByRateLimit}</c> 메트릭을 <b>배리어</b>로 쓴다 —
/// 드롭은 응답이 없어 클라이언트가 직접 못 보므로, 서버가 프레임을 처리·거부했음을 메트릭
/// 관측으로 확정한 뒤에 시계를 진행시킨다(비동기 처리와 시계 진행의 경합 제거).
/// </remarks>
public sealed class RateLimitEndToEndTests : IDisposable
{
    private const ushort EchoId = 810;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Fact]
    public async Task Rate_limited_frame_is_dropped_but_connection_survives()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        ManualTimeProvider time = new();
        RateLimitSink sink = new();

        // 버스트 1 — 첫 프레임만 즉시 통과, 다음은 리필 전까지 버려진다.
        PerConnectionRateLimiter limiter = new(
            new PerConnectionRateLimitOptions { PermitsPerSecond = 100, BurstCapacity = 1 }, time);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"ratelimit-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseMetrics(sink) // 드롭을 DispatchFailures 로 관측 — 배리어로 쓴다.
            .ConfigureDispatcher(dispatcher => dispatcher
                .Use(new RateLimitMiddleware(limiter))
                .MapRaw(new MessageId(EchoId), async context =>
                {
                    await FrameWriter.WriteFrameAsync(
                        context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
                        FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                    return DispatchStatus.Handled;
                }))
            .Build();

        await server.StartAsync(_timeout.Token);

        Channel<byte[]> received = Channel.CreateUnbounded<byte[]>();
        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                received.Writer.TryWrite(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        // 1. 첫 프레임 — 버스트 토큰으로 통과, 에코 [1] 도착.
        await Send(session, client, 1);
        Assert.Equal(new byte[] { 1 }, await received.Reader.ReadAsync(_timeout.Token));

        // 2. 두 번째 — 토큰이 없어 버려진다. 드롭이 메트릭에 잡힐 때까지 기다린다(배리어).
        await Send(session, client, 2);
        await WaitForDropAsync(sink, expected: 1);

        // 드롭이 확정된 지금 리필한다 — 시계 진행이 프레임 2 처리와 경합하지 않는다.
        time.Advance(TimeSpan.FromSeconds(1));

        // 3. 세 번째 — 통과. 커넥션이 살아 있어 에코 [3]이 온다(드롭 하나가 커넥션을 안 죽인다).
        await Send(session, client, 3);
        Assert.Equal(new byte[] { 3 }, await received.Reader.ReadAsync(_timeout.Token));

        // 정확히 한 프레임만 버려졌다.
        Assert.Equal(1, sink.RateLimitedCount);
    }

    private async Task WaitForDropAsync(RateLimitSink sink, int expected)
    {
        while (sink.RateLimitedCount < expected)
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, _timeout.Token);
        }
    }

    private static ValueTask Send(ClientSession session, ChServerMClient client, byte value) =>
        new(FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(EchoId), new[] { value },
            FrameFlags.None, sequence: value, session.Connection.ConnectionClosed).AsTask());

    /// <summary>속도 제한 거부(DispatchFailures{RejectedByRateLimit})만 세는 싱크.</summary>
    private sealed class RateLimitSink : IMetricsSink
    {
        private int _rateLimited;

        public int RateLimitedCount => Volatile.Read(ref _rateLimited);

        public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            if (name != MetricNames.DispatchFailures)
            {
                return;
            }

            foreach (MetricTag tag in tags)
            {
                if (tag.Name == TagNames.ErrorCode && tag.Value == nameof(DispatchStatus.RejectedByRateLimit))
                {
                    Interlocked.Add(ref _rateLimited, (int)delta);
                }
            }
        }

        public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
        {
        }

        public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
