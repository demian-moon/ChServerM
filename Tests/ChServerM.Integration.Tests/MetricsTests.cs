using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Observability;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 관측 축(Phase 11)의 종단 검증 — <c>UseMetrics</c> 한 번으로 커넥션 생명주기와
/// 디스패치 지연·실패가 실제 메트릭으로 나오는지, 조립하지 않으면 아무것도 안 나오는지
/// <see cref="MeterListener"/> 로 관측해 고정한다.
/// </summary>
public sealed class MetricsTests : IDisposable
{
    private const ushort EchoId = 700;
    private const ushort RejectedId = 701; // 핸들러 없음 → HandlerNotFound

    private readonly Meter _meter = new("ChServerM.MetricsTest." + Guid.NewGuid().ToString("N"));
    private readonly MeterListener _listener = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Value)> _gaugeDeltas = [];
    private readonly List<(string Name, double Value)> _histogram = [];
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public MetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (_lock)
            {
                // UpDownCounter(게이지)와 Counter 를 이름으로 구분해 각각 담는다.
                if (instrument is UpDownCounter<long>)
                {
                    _gaugeDeltas.Add((instrument.Name, value));
                }
                else
                {
                    _counters[instrument.Name] = _counters.GetValueOrDefault(instrument.Name) + value;
                }
            }
        });

        _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            lock (_lock)
            {
                _histogram.Add((instrument.Name, value));
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
        _timeout.Dispose();
    }

    private long Counter(string name)
    {
        lock (_lock)
        {
            return _counters.GetValueOrDefault(name);
        }
    }

    private long GaugeNet(string name)
    {
        lock (_lock)
        {
            long sum = 0;
            foreach ((string n, long v) in _gaugeDeltas)
            {
                if (n == name)
                {
                    sum += v;
                }
            }

            return sum;
        }
    }

    [Fact]
    public async Task Connection_and_dispatch_metrics_are_emitted()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        MeterMetricsSink sink = new(_meter);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"metrics-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseMetrics(sink)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                echoed.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        byte[] payload = [1, 2, 3];
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(EchoId), payload,
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);
        await echoed.Task.WaitAsync(_timeout.Token);

        // 커넥션 수립 카운터 + 활성 게이지가 올라갔고, 프레임·지연이 기록됐다.
        Assert.True(Counter(MetricNames.ConnectionsAccepted) >= 1, "수립 카운터가 오르지 않았다.");
        Assert.True(GaugeNet(MetricNames.ConnectionsActive) >= 1, "활성 게이지가 오르지 않았다.");
        Assert.True(Counter(MetricNames.FramesReceived) >= 1, "프레임 카운터가 오르지 않았다.");
        lock (_lock)
        {
            Assert.Contains(_histogram, h => h.Name == MetricNames.DispatchDuration);
        }
    }

    [Fact]
    public async Task Dispatch_failure_is_counted()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        MeterMetricsSink sink = new(_meter);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"metrics-fail-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        // HandlerNotFound 시 커넥션을 닫도록 해 실패가 확정적으로 관측되게 한다.
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseMetrics(sink)
            .ConfigureConnection(o => o.CloseOnHandlerNotFound = true)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
                ValueTask.FromResult(DispatchStatus.Handled)))
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        // 등록되지 않은 메시지 → HandlerNotFound → 실패 카운터 + 커넥션 종료.
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(RejectedId), ReadOnlySpan<byte>.Empty,
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        // 커넥션이 닫힐 때까지 기다린다 — 그 시점이면 실패 메트릭이 기록됐다.
        await session.Completion.WaitAsync(_timeout.Token);

        Assert.True(Counter(MetricNames.DispatchFailures) >= 1, "디스패치 실패 카운터가 오르지 않았다.");
    }

    [Fact]
    public async Task Without_UseMetrics_nothing_is_recorded()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"metrics-off-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        TaskCompletionSource<byte[]> handled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // UseMetrics 를 호출하지 않는다 — NullMetricsSink 기본값이라 메트릭 0.
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                handled.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        await server.StartAsync(_timeout.Token);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(EchoId), new byte[] { 9 },
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);
        await handled.Task.WaitAsync(_timeout.Token);

        // 이 테스트의 미터로는 아무 계측기도 발행되지 않았다.
        lock (_lock)
        {
            Assert.Empty(_counters);
            Assert.Empty(_gaugeDeltas);
            Assert.Empty(_histogram);
        }
    }
}
