using System;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Observability;

namespace ChServerM.Bench.Observability;

/// <summary>
/// 관측 오버헤드 — 메트릭을 켰을 때 디스패치 핫패스에 붙는 프레임당 비용 (Phase 11 게이트).
/// </summary>
/// <remarks>
/// <para>
/// <b>이 벤치가 게이트다.</b> "관측을 켠 상태의 오버헤드가 측정·기록되고 허용 범위 안일 때"가
/// Phase 11 통과 조건이다. 관측이 성능을 먹으면 프로덕션에서 꺼지고, 꺼진 관측은 없는 것과
/// 같다 — 그래서 켠 비용을 수치로 방어한다.
/// </para>
/// <para>4개 변형으로 비용을 분해한다:</para>
/// <list type="bullet">
///   <item><description><b>기준선</b> — 미들웨어 없이 종단 델리게이트만(관측 미조립)</description></item>
///   <item><description><b>Null 싱크</b> — <see cref="MetricsMiddleware"/> 래퍼 비용만
///   (<see cref="NullMetricsSink"/> 호출은 JIT 이 접는다)</description></item>
///   <item><description><b>Meter, 리스너 없음</b> — 메트릭 켜되 익스포터 미부착(가장 흔한
///   프로덕션 상태). BCL <c>Counter.Add</c> 가 구독자 없을 때 거의 무비용인지 확인</description></item>
///   <item><description><b>Meter + 리스너</b> — 익스포터가 붙어 실제로 값을 흘리는 최악 경로</description></item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
public class MetricsOverheadBenchmarks
{
    private MessageContext _context = null!;
    private MessageDelegate _terminal = null!;
    private MessageDelegate _nullSinkPipeline = null!;
    private MessageDelegate _meterNoListenerPipeline = null!;
    private MessageDelegate _meterWithListenerPipeline = null!;
    private Meter _idleMeter = null!;
    private Meter _listenedMeter = null!;
    private MeterListener _listener = null!;

    [GlobalSetup]
    public void Setup()
    {
#pragma warning disable CA2000 // 무동작 커넥션의 수명은 벤치 프로세스 전체다.
        NullConnection connection = new();
#pragma warning restore CA2000
        _context = new MessageContext(connection);
        _context.BeginFrame(
            new MessageEnvelope(new MessageId(100), FrameFlags.None, 0),
            default, receivedAt: default, CancellationToken.None);

        _terminal = static _ => ValueTask.FromResult(DispatchStatus.Handled);

        _nullSinkPipeline = Wrap(new MetricsMiddleware(NullMetricsSink.Instance));

        // 싱크가 소유한 Meter 는 Cleanup 이 폐기한다(Meter.Dispose 는 멱등).
#pragma warning disable CA2000
        _idleMeter = new Meter("ChServerM.Bench.Idle");
        _meterNoListenerPipeline = Wrap(new MetricsMiddleware(new MeterMetricsSink(_idleMeter)));

        _listenedMeter = new Meter("ChServerM.Bench.Listened");
        _meterWithListenerPipeline = Wrap(new MetricsMiddleware(new MeterMetricsSink(_listenedMeter)));
#pragma warning restore CA2000

        // 익스포터를 흉내내는 리스너 — 측정값을 실제로 소비한다.
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, _listenedMeter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        _listener.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener.Dispose();
        _idleMeter.Dispose();
        _listenedMeter.Dispose();
    }

    private MessageDelegate Wrap(MetricsMiddleware middleware) =>
        context => middleware.InvokeAsync(context, _terminal);

    [Benchmark(Baseline = true, Description = "기준선 (관측 미조립)")]
    public async ValueTask<int> Baseline() => (int)await _terminal(_context).ConfigureAwait(false);

    [Benchmark(Description = "Null 싱크 (미들웨어 래퍼만)")]
    public async ValueTask<int> NullSink() => (int)await _nullSinkPipeline(_context).ConfigureAwait(false);

    [Benchmark(Description = "Meter, 리스너 없음")]
    public async ValueTask<int> MeterIdle() => (int)await _meterNoListenerPipeline(_context).ConfigureAwait(false);

    [Benchmark(Description = "Meter + 리스너")]
    public async ValueTask<int> MeterWithListener() => (int)await _meterWithListenerPipeline(_context).ConfigureAwait(false);

    /// <summary>디스패치 측정에는 커넥션이 쓰이지 않는다 — 계약 충족용 무동작 구현.</summary>
    private sealed class NullConnection : IConnection
    {
        private static readonly Pipe DummyPipe = new();

        public ConnectionId Id => new(1, 0);

        public PipeReader Input => DummyPipe.Reader;

        public PipeWriter Output => DummyPipe.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 0);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
