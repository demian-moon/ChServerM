using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 수용 제어의 종단 검증 — 전송이 과부하 신호에 신규 연결을 거부하고, 거부가
/// <see cref="MetricNames.ConnectionsRejected"/> 로 관측되며, 서버가 생존함을 고정한다.
/// </summary>
/// <remarks>
/// InMemory 전송으로 검증한다 — 소켓 없이 결정적이고, 수용 거부가 <c>Accept</c> 예외로
/// 즉시 드러나 관측이 쉽다. TCP 는 같은 코드 경로(<c>RejectConnection</c>)를 공유한다.
/// </remarks>
public sealed class AdmissionControlEndToEndTests : IDisposable
{
    private const ushort EchoId = 800;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Fact]
    public async Task Admission_rejects_burst_over_limit_and_emits_metric_and_server_survives()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        RecordingSink sink = new();
        ManualTimeProvider time = new();

        // 버스트 1, 초당 10 — 첫 연결만 즉시 수용, 다음은 리필 전까지 거부.
        ConnectionRateAdmissionControl admission = new(
            new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 10, BurstCapacity = 1 }, time);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"admit-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new()
        {
            AdmissionControl = admission,
            MetricsSink = sink,
        };

        TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                echoed.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        await server.StartAsync(_timeout.Token);

        // 1. 첫 연결 — 버스트 토큰으로 수용된다.
        InMemoryClientTransport clientTransport = new(hub, null, options);
        IConnection first = await clientTransport.ConnectAsync(endPoint, _timeout.Token);
        await FrameWriter.WriteFrameAsync(
            first.Output, serverEncoder, new MessageId(EchoId), new byte[] { 1 },
            FrameFlags.None, sequence: 1, first.ConnectionClosed);
        await echoed.Task.WaitAsync(_timeout.Token);

        // 2. 즉시 두 번째 연결 — 토큰이 없어 수용 제어가 거부한다(Accept 예외).
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await clientTransport.ConnectAsync(endPoint, _timeout.Token));

        // 거부가 메트릭으로 남았다 — 사유 태그는 "admission".
        Assert.True(sink.CountOf(MetricNames.ConnectionsRejected, "admission") >= 1,
            "수용 거부가 ConnectionsRejected 메트릭으로 관측되지 않았다.");

        // 3. 리필 후에는 다시 수용된다 — 거부 하나가 서버를 죽이지 않는다(실패 격리).
        //    연결이 성립한다는 것으로 생존을 확인한다(핸들러 응답은 1번에서 이미 검증).
        time.Advance(TimeSpan.FromSeconds(1));
        IConnection third = await clientTransport.ConnectAsync(endPoint, _timeout.Token);
        Assert.NotNull(third);

        await third.DisposeAsync();
        await first.DisposeAsync();
        await clientTransport.DisposeAsync();
    }

    [Fact]
    public async Task Without_admission_control_all_connections_are_admitted()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        RecordingSink sink = new();

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"admit-off-{Guid.NewGuid():N}");
        // AdmissionControl 미설정 — 정적 상한만.
        InMemoryTransportOptions options = new() { MetricsSink = sink };

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
                ValueTask.FromResult(DispatchStatus.Handled)))
            .Build();

        await server.StartAsync(_timeout.Token);

        InMemoryClientTransport clientTransport = new(hub, null, options);
        for (int i = 0; i < 5; i++)
        {
            IConnection c = await clientTransport.ConnectAsync(endPoint, _timeout.Token);
            await c.DisposeAsync();
        }

        Assert.Equal(0, sink.CountOf(MetricNames.ConnectionsRejected, "admission"));
        await clientTransport.DisposeAsync();
    }

    /// <summary>이름·사유 태그별 카운터를 기록하는 테스트용 싱크.</summary>
    private sealed class RecordingSink : IMetricsSink
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

        public long CountOf(string metric, string reason)
        {
            lock (_lock)
            {
                return _counts.GetValueOrDefault(metric + "|" + reason);
            }
        }

        public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            string reason = "";
            foreach (MetricTag tag in tags)
            {
                if (tag.Name == TagNames.CloseReason)
                {
                    reason = tag.Value ?? "";
                }
            }

            lock (_lock)
            {
                string key = name + "|" + reason;
                _counts[key] = _counts.GetValueOrDefault(key) + delta;
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
