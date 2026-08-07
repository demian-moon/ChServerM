using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Resilience;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// **할당 회귀 게이트** — 조립된 디스패치 경로가 프레임당 힙 할당 0 을 유지하는지 (Phase 12).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 측정만 하고 지키지 않으면 성능은 반드시 퇴화한다.</b> 프레임워크의 중심
/// 주장은 "커넥션당·메시지당 힙 할당 0"(CLAUDE.md 2)인데, 그동안 그것은 <b>코덱 수준에서만</b>
/// 고정돼 있었다(<c>FrameCodecAllocationTests</c>). 정작 미들웨어가 쌓이는
/// <b>조립된 파이프라인</b>은 아무도 지키지 않았다 — 미들웨어 하나가 <c>async</c> 상태 머신을
/// 만들거나 클로저를 잡으면 프레임당 할당이 조용히 생긴다.
/// </para>
/// <para>
/// <b>왜 할당 게이트가 첫 회귀 게이트인가.</b> 시간 기반 게이트는 개발 머신에서 본질적으로
/// 노이즈가 크지만(반복·중위값 전략이 필요하고 그래도 흔들린다), <b>할당은 결정적</b>이다 —
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> 는 GC 타이밍과 무관하고 이 스레드의
/// 할당만 세므로 병렬 실행에도 영향받지 않는다. 첫날부터 신뢰할 수 있는 유일한 게이트다.
/// </para>
/// <para>
/// <b>관용구는 <c>FrameCodecAllocationTests</c> 를 따른다</b> — 워밍업으로 JIT 을 끝내고,
/// 측정 루프 안에서는 단언하지 않으며(xUnit <c>Assert</c> 자체가 할당한다), 결과를 싱크에
/// 누적해 JIT 이 루프를 통째로 지우지 못하게 한다.
/// </para>
/// <para>
/// <b>⚠ 여기서 고정하는 것은 "동기 완료 핸들러" 기준이다.</b> 진짜 비동기 핸들러는 상태 머신을
/// 할당한다 — 그것은 설계상 정상이고(<c>FramedConnectionHandler</c> 문서), 이 게이트의 대상이
/// 아니다. 지키려는 것은 <b>프레임워크가 얹는 몫</b>이 0 이라는 것이다.
/// </para>
/// </remarks>
public sealed class DispatchAllocationGateTests
{
    private const int Iterations = 10_000;
    private const int WarmupIterations = 1_000;
    private const ushort EchoId = 1000;
    private const ushort TelemetryId = 1001;

    /// <summary>JIT 이 결과 미사용을 이유로 루프를 지우지 못하게 붙잡는 싱크.</summary>
    private long _sink;

    private sealed class FixedLoad(LoadLevel level) : ILoadLevelSource
    {
        public LoadLevel Current => level;
    }

    private sealed class FrozenTime : TimeProvider
    {
        public override long GetTimestamp() => 0;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private static readonly MessageDelegate Terminal =
        static _ => ValueTask.FromResult(DispatchStatus.Handled);

    /// <summary>파이프라인을 프레임당 반복 호출하고 이 스레드의 할당 증가분을 잰다.</summary>
    private long MeasureAllocation(MessageDelegate pipeline, MessageContext context)
    {
        for (int i = 0; i < WarmupIterations; i++)
        {
            Drain(pipeline(context));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            Drain(pipeline(context));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>동기 완료를 전제로 결과를 꺼내 싱크에 누적한다.</summary>
    private void Drain(ValueTask<DispatchStatus> pending) => _sink += (int)pending.Result;

    [Fact]
    public void Bare_pipeline_allocates_nothing()
    {
        // 기준선 — 미들웨어가 없는 경로. 여기서 이미 할당이 있으면 아래 측정이 무의미하다.
        MessageContext context = NewContext(EchoId);

        long allocated = MeasureAllocation(Terminal, context);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Tracing_middleware_without_listener_allocates_nothing()
    {
        // 추적 fast-path 의 회귀 방어 — 리스너가 없으면 span 도 async 래퍼도 만들지 않는다
        // (ADR-0022). 이 단언이 깨지면 fast-path 가 사라진 것이다.
        TracingMiddleware middleware = new();
        MessageContext context = NewContext(EchoId);
        MessageDelegate pipeline = ctx => middleware.InvokeAsync(ctx, Terminal);

        long allocated = MeasureAllocation(pipeline, context);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Load_shedding_middleware_at_normal_load_allocates_nothing()
    {
        // 열화 fast-path 의 회귀 방어 — 평상시(Normal)에는 규칙 조회조차 없이 통과한다
        // (ADR-0029). "만일을 위한 보험" 이 상시 세금이 되면 안 된다.
        LoadSheddingMiddleware middleware = new(
            new FixedLoad(LoadLevel.Normal),
            new LoadSheddingOptions().ShedAbove(new MessageId(TelemetryId), LoadLevel.Normal));

        MessageContext context = NewContext(EchoId);
        MessageDelegate pipeline = ctx => middleware.InvokeAsync(ctx, Terminal);

        long allocated = MeasureAllocation(pipeline, context);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Load_shedding_rejection_allocates_nothing()
    {
        // 거부 경로도 무할당이어야 한다 — 과부하일수록 이 경로가 뜨거워지는데
        // 거기서 할당하면 열화가 GC 압력을 더한다(정확히 반대 효과).
        LoadSheddingMiddleware middleware = new(
            new FixedLoad(LoadLevel.Critical),
            new LoadSheddingOptions().ShedAbove(new MessageId(TelemetryId), LoadLevel.Normal));

        MessageContext context = NewContext(TelemetryId);
        MessageDelegate pipeline = ctx => middleware.InvokeAsync(ctx, Terminal);

        long allocated = MeasureAllocation(pipeline, context);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Rate_limit_middleware_allocates_only_the_per_connection_bucket()
    {
        // 커넥션당 버킷 하나는 첫 프레임에서 만들어지고 그 뒤 재사용된다
        // (상태를 Connection.Features 에 둔 설계). 워밍업이 그것을 끝내므로
        // 측정 구간에서는 0 이어야 한다 — 프레임당 할당이 없다는 뜻이다.
        RateLimitMiddleware middleware = new(
            new PerConnectionRateLimiter(
                new PerConnectionRateLimitOptions { PermitsPerSecond = 1_000_000, BurstCapacity = 1_000_000 },
                new FrozenTime()));

        MessageContext context = NewContext(EchoId);
        MessageDelegate pipeline = ctx => middleware.InvokeAsync(ctx, Terminal);

        long allocated = MeasureAllocation(pipeline, context);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Composed_pipeline_allocates_nothing()
    {
        // ★ 실제 조립 형태 — 추적 + 열화 + 속도 제한을 함께 쌓아도 프레임당 0.
        // 개별 미들웨어가 0 이어도 합성에서 새는 경우가 있으므로 따로 고정한다.
        TracingMiddleware tracing = new();
        LoadSheddingMiddleware shedding = new(
            new FixedLoad(LoadLevel.Normal),
            new LoadSheddingOptions().ShedAbove(new MessageId(TelemetryId), LoadLevel.Normal));
        RateLimitMiddleware rateLimit = new(
            new PerConnectionRateLimiter(
                new PerConnectionRateLimitOptions { PermitsPerSecond = 1_000_000, BurstCapacity = 1_000_000 },
                new FrozenTime()));

        MessageContext context = NewContext(EchoId);
        MessageDelegate pipeline = ctx =>
            tracing.InvokeAsync(ctx, c1 =>
                shedding.InvokeAsync(c1, c2 =>
                    rateLimit.InvokeAsync(c2, Terminal)));

        long allocated = MeasureAllocation(pipeline, context);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Gate_actually_catches_a_deliberate_regression()
    {
        // ★★ Phase 12 게이트 조건 그 자체 — "회귀 게이트가 의도적 성능 퇴화를 실제로 잡을 때".
        // 게이트가 무엇이든 통과시킨다면 게이트가 아니다. 프레임당 할당하는 미들웨어를 넣어
        // 측정이 0 이 아님을 확인한다.
        AllocatingMiddleware regression = new();
        MessageContext context = NewContext(EchoId);
        MessageDelegate pipeline = ctx => regression.InvokeAsync(ctx, Terminal);

        long allocated = MeasureAllocation(pipeline, context);

        Assert.True(allocated > 0, "고의로 할당하는 미들웨어를 넣었는데 게이트가 0 을 보고했다 — 게이트가 작동하지 않는다.");
    }

    /// <summary>프레임마다 힙 객체를 만드는 미들웨어 — 게이트가 회귀를 잡는지 확인하는 용도.</summary>
    private sealed class AllocatingMiddleware : IServerMiddleware
    {
        public ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
        {
            // 프레임당 힙 할당 — 미들웨어가 흔히 저지르는 실수(상태를 새 객체에 담기).
            _ = new byte[64];
            return next(context);
        }
    }

    private static MessageContext NewContext(ushort messageId)
    {
        MessageContext context = new(new StubConnection());
        context.BeginFrame(
            new MessageEnvelope(new MessageId(messageId), FrameFlags.None, 0),
            default,
            receivedAt: default,
            CancellationToken.None);

        return context;
    }

    /// <summary>측정에 필요한 최소 커넥션 — 기능 저장소만 의미가 있다(속도 제한 버킷).</summary>
    private sealed class StubConnection : IConnection
    {
        private static readonly Pipe DummyPipe = new();

        public ConnectionId Id => new(1, 0);

        public PipeReader Input => DummyPipe.Reader;

        public PipeWriter Output => DummyPipe.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 1);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
