using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 분산 추적의 <b>크로스 스레드 부모 전파</b>(ADR-0022)를 종단으로 고정한다.
/// </summary>
/// <remarks>
/// <b>실행 모델을 반드시 낀다.</b> 이 기능의 핵심은 디스패치가 <b>파티션 스레드</b>에서
/// 도는데도 커넥션 span 의 자식이 되는 것이다 — <see cref="Activity.Current"/>(AsyncLocal)는
/// 그 스레드로 흐르지 않으므로, 실행 모델 없이(인라인 디스패치) 테스트하면 정작 풀려던
/// 문제를 건드리지 못한다. 그래서 <see cref="PartitionedExecutionModel"/> 을 조립한다.
/// </remarks>
[Collection("Tracing")]
public sealed class TracingEndToEndTests : IDisposable
{
    private const ushort EchoId = 800;

    private readonly object _lock = new();
    private readonly List<Activity> _started = [];
    private readonly ActivityListener _listener;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public TracingEndToEndTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == DiagnosticNames.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                lock (_lock)
                {
                    _started.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _timeout.Dispose();
    }

    private Activity? FindSpan(string operationName)
    {
        lock (_lock)
        {
            return _started.FirstOrDefault(a => a.OperationName == operationName);
        }
    }

    [Fact]
    public async Task DispatchSpan_IsChildOfConnectionSpan_AcrossPartitionThread()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"trace-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 실행 모델을 껴서 디스패치를 파티션 스레드로 보낸다 — 이 테스트의 요점이다.
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseExecutionModel(new PartitionedExecutionModel(new PartitionedExecutionOptions { PartitionCount = 2 }))
            .UseTracing()
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

        // 에코가 돌아왔으면 커넥션 span 과 디스패치 span 이 둘 다 시작됐다.
        Activity connection = await WaitForSpanAsync(ActivityNames.Connection);
        Activity dispatch = await WaitForSpanAsync(ActivityNames.Dispatch);

        // 크로스 스레드인데도 같은 trace, 디스패치의 부모가 커넥션 span 이다.
        Assert.Equal(connection.TraceId, dispatch.TraceId);
        Assert.Equal(connection.SpanId, dispatch.ParentSpanId);
        Assert.Equal(ActivityKind.Server, connection.Kind);
        Assert.StartsWith("conn:", connection.GetTagItem(TagNames.ConnectionId)?.ToString());
    }

    [Fact]
    public async Task WithoutUseTracing_NoSpansAreCreated()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"trace-off-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();

        TaskCompletionSource<byte[]> handled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // UseTracing 을 부르지 않는다 — 데코레이터·미들웨어가 배선되지 않아 span 이 없다.
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .UseExecutionModel(new PartitionedExecutionModel(new PartitionedExecutionOptions { PartitionCount = 2 }))
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

        lock (_lock)
        {
            Assert.Empty(_started);
        }
    }

    private async Task<Activity> WaitForSpanAsync(string operationName)
    {
        while (true)
        {
            Activity? span = FindSpan(operationName);
            if (span is not null)
            {
                return span;
            }

            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, _timeout.Token);
        }
    }
}
