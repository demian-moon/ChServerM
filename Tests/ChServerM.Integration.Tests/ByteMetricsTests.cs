using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 바이트 카운터(ADR-0025)의 종단 검증 — 회선을 건넌 바이트가
/// <see cref="MetricNames.BytesReceived"/>·<see cref="MetricNames.BytesSent"/> 로 관측되는지.
/// </summary>
/// <remarks>
/// <b>TCP 로 검증한다 — 회선이 있는 유일한 전송이기 때문이다.</b> 인메모리 전송은 파이프를
/// 직접 건네므로 "회선을 건넌 바이트"가 존재하지 않고, 그래서 이 메트릭을 내지 않는다
/// (<see cref="MetricNames.BytesReceived"/> 계약). 서버에만 싱크를 주입해 클라이언트 쪽
/// 카운트가 섞이지 않게 한다.
/// </remarks>
public sealed class ByteMetricsTests : IDisposable
{
    private const ushort EchoId = 850;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Fact]
    public async Task Wire_bytes_are_counted_on_both_directions()
    {
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);
        RecordingSink sink = new();

        // 서버에만 싱크를 준다 — 클라이언트 전송은 별도 옵션이라 이 카운터에 섞이지 않는다.
        TcpTransportOptions serverOptions = new() { MetricsSink = sink };
        TcpServerTransport transport = new(new IPEndPoint(IPAddress.Loopback, 0), serverOptions);

        TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);

        EndPoint endPoint = server.LocalEndPoint ?? throw new InvalidOperationException("바인드 주소가 없다.");

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new TcpClientTransport(new TcpTransportOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                echoed.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);

        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(EchoId), payload,
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        // 에코가 돌아왔으면 서버가 프레임을 받고(수신) 되돌려 보냈다(송신).
        await echoed.Task.WaitAsync(_timeout.Token);

        // 수신 카운터는 수신 펌프가 디스패치 전에 올리므로 에코 수신이 곧 증거다.
        long received = sink.Total(MetricNames.BytesReceived);
        Assert.True(received >= payload.Length, $"수신 바이트가 페이로드보다 적다: {received}");

        // ⚠ 송신 카운터는 폴링해야 한다. 송신 펌프는 SendAsync 가 반환한 뒤에 세는데,
        // 클라이언트는 커널이 바이트를 넘기는 즉시 에코를 받을 수 있어 순서가 고정되지
        // 않는다 — 느린 CI 러너에서 0 으로 관측된 실사례(2026-08-11). 상한은 테스트
        // 전역 타임아웃이 진다.
        long sent = sink.Total(MetricNames.BytesSent);
        while (sent < payload.Length)
        {
            await Task.Delay(10, _timeout.Token);
            sent = sink.Total(MetricNames.BytesSent);
        }

        // 최소한 페이로드 + 고정 헤더만큼은 양방향으로 흘렀다.
        Assert.True(sent >= payload.Length, $"송신 바이트가 페이로드보다 적다: {sent}");
    }

    [Fact]
    public async Task Without_sink_transport_still_works()
    {
        // 싱크를 조립하지 않아도(NullMetricsSink 기본) 전송이 정상 동작한다 — 계측이 경로에
        // 필수 조건이 되지 않는 것을 고정한다.
        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        TcpServerTransport transport = new(new IPEndPoint(IPAddress.Loopback, 0), new TcpTransportOptions());
        TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), serverEncoder)
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, serverEncoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            }))
            .Build();

        await server.StartAsync(_timeout.Token);
        EndPoint endPoint = server.LocalEndPoint ?? throw new InvalidOperationException("바인드 주소가 없다.");

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new TcpClientTransport(new TcpTransportOptions()))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), context =>
            {
                echoed.TrySetResult(context.Payload.ToArray());
                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, _timeout.Token);
        await FrameWriter.WriteFrameAsync(
            session.Connection.Output, client.Encoder, new MessageId(EchoId), new byte[] { 9 },
            FrameFlags.None, sequence: 1, session.Connection.ConnectionClosed);

        byte[] result = await echoed.Task.WaitAsync(_timeout.Token);
        Assert.Equal([9], result);
    }

    /// <summary>이름별 누적을 기록하는 테스트용 싱크.</summary>
    private sealed class RecordingSink : IMetricsSink
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

        public long Total(string metric)
        {
            lock (_lock)
            {
                return _counts.GetValueOrDefault(metric);
            }
        }

        public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            lock (_lock)
            {
                _counts[name] = _counts.GetValueOrDefault(name) + delta;
            }
        }

        public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
        {
        }

        public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
        }
    }
}
