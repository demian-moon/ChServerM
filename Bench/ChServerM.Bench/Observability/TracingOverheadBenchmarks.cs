using System;
using System.Diagnostics;
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

namespace ChServerM.Bench.Observability;

/// <summary>
/// 추적 오버헤드 — 트레이싱을 켰을 때 디스패치 핫패스에 붙는 프레임당 비용 (Phase 11 게이트, ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>이 벤치가 fast-path 를 방어한다.</b> <see cref="TracingMiddleware"/> 는 구독자가 없으면
/// <c>next</c> 를 async 래퍼 없이 그대로 통과시킨다. 그 최적화가 실제로 near-zero 인지를
/// "리스너 없음"과 "기준선"의 차이로 확인한다 — 관측이 성능을 먹으면 프로덕션에서 꺼지고,
/// 꺼진 관측은 없는 것과 같다(측정 없는 최적화 금지, CLAUDE.md 2).
/// </para>
/// <para>3개 변형으로 비용을 분해한다:</para>
/// <list type="bullet">
///   <item><description><b>기준선</b> — 미들웨어 없이 종단 델리게이트만(추적 미조립)</description></item>
///   <item><description><b>추적, 리스너 없음</b> — <see cref="TracingMiddleware"/> 삽입하되 익스포터
///   미부착(가장 흔한 프로덕션 상태). fast-path 로 기준선에 근접해야 한다</description></item>
///   <item><description><b>추적 + 리스너</b> — <see cref="ActivityListener"/> 가 붙어 실제로 span 을
///   만드는 최악 경로(<see cref="Activity"/> 할당이 여기서 생긴다)</description></item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
public class TracingOverheadBenchmarks
{
    private MessageContext _context = null!;
    private MessageDelegate _terminal = null!;
    private MessageDelegate _tracingPipeline = null!;
    private ActivityListener? _listener;

    // 공통 셋업. 리스너는 붙이지 않는다 — BDN 은 벤치마크마다 별도 프로세스라, 이 셋업이
    // 도는 프로세스는 리스너가 없는 상태다(기준선·fast-path 변형).
    [GlobalSetup]
    public void Setup() => BuildPipeline();

    // 리스너 변형 전용 셋업. Target 을 지정하면 이 벤치마크 프로세스에서만 리스너가 붙는다 —
    // "리스너 없음"과 "리스너 있음"을 하나의 정적 ActivitySource 로 프로세스 격리해 잰다.
    [GlobalSetup(Target = nameof(TracingWithListener))]
    public void SetupWithListener()
    {
        BuildPipeline();

        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == DiagnosticNames.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    private void BuildPipeline()
    {
#pragma warning disable CA2000 // 무동작 커넥션의 수명은 벤치 프로세스 전체다.
        NullConnection connection = new();
#pragma warning restore CA2000
        _context = new MessageContext(connection);
        _context.BeginFrame(
            new MessageEnvelope(new MessageId(100), FrameFlags.None, 0),
            default, receivedAt: default, CancellationToken.None);

        _terminal = static _ => ValueTask.FromResult(DispatchStatus.Handled);

        TracingMiddleware middleware = new();
        _tracingPipeline = context => middleware.InvokeAsync(context, _terminal);
    }

    [GlobalCleanup]
    public void Cleanup() => _listener?.Dispose();

    [Benchmark(Baseline = true, Description = "기준선 (추적 미조립)")]
    public async ValueTask<int> Baseline() => (int)await _terminal(_context).ConfigureAwait(false);

    [Benchmark(Description = "추적, 리스너 없음 (fast-path)")]
    public async ValueTask<int> TracingNoListener() => (int)await _tracingPipeline(_context).ConfigureAwait(false);

    [Benchmark(Description = "추적 + 리스너 (span 생성)")]
    public async ValueTask<int> TracingWithListener() => (int)await _tracingPipeline(_context).ConfigureAwait(false);

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
