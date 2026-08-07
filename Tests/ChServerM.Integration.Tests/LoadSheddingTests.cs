using System;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Resilience;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 우아한 열화(ADR-0029)를 검증한다 — 부하가 오를수록 선언된 순서대로 비필수부터 버리고,
/// 등록하지 않은 필수 경로는 어떤 부하에서도 살아남는지.
/// </summary>
/// <remarks>
/// 정책과 측정이 분리돼 있어(<see cref="ILoadLevelSource"/>) 가짜 소스로 <b>결정적으로</b>
/// 검증된다 — ADR-0021 이 메모리 워터마크를 미룰 때 우려한 "GC 시점 의존" 을 계약 분리로 푼 결과다.
/// </remarks>
public sealed class LoadSheddingTests
{
    private const ushort Telemetry = 900;   // 가장 먼저 버린다
    private const ushort Chat = 901;        // 한계에서만 버린다
    private const ushort Auth = 902;        // 절대 버리지 않는다(미등록)

    private sealed class FakeLoad : ILoadLevelSource
    {
        public LoadLevel Current { get; set; } = LoadLevel.Normal;
    }

    private static (LoadSheddingMiddleware Middleware, FakeLoad Load) Create()
    {
        FakeLoad load = new();
        LoadSheddingOptions options = new LoadSheddingOptions()
            .ShedAbove(new MessageId(Telemetry), LoadLevel.Normal)
            .ShedAbove(new MessageId(Chat), LoadLevel.Elevated);

        return (new LoadSheddingMiddleware(load, options), load);
    }

    private static async Task<DispatchStatus> InvokeAsync(LoadSheddingMiddleware middleware, ushort messageId)
    {
        FakeConnection connection = new();
        MessageContext context = new(connection);
        context.BeginFrame(
            new ChServerM.Framing.MessageEnvelope(new MessageId(messageId), ChServerM.Framing.FrameFlags.None, 0),
            default,
            receivedAt: default,
            default);

        return await middleware.InvokeAsync(context, static _ => ValueTask.FromResult(DispatchStatus.Handled));
    }

    [Fact]
    public async Task Normal_load_passes_everything()
    {
        (LoadSheddingMiddleware middleware, FakeLoad load) = Create();
        load.Current = LoadLevel.Normal;

        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Telemetry));
        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Chat));
        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Auth));
    }

    [Fact]
    public async Task Elevated_sheds_only_the_lowest_priority()
    {
        // 선언한 순서가 실제 차단 순서가 된다 — 이것이 "우아한" 열화의 정의다.
        (LoadSheddingMiddleware middleware, FakeLoad load) = Create();
        load.Current = LoadLevel.Elevated;

        Assert.Equal(DispatchStatus.RejectedByLoadShedding, await InvokeAsync(middleware, Telemetry));
        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Chat));
        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Auth));
    }

    [Fact]
    public async Task Critical_sheds_everything_declared_but_never_the_essential()
    {
        (LoadSheddingMiddleware middleware, FakeLoad load) = Create();
        load.Current = LoadLevel.Critical;

        Assert.Equal(DispatchStatus.RejectedByLoadShedding, await InvokeAsync(middleware, Telemetry));
        Assert.Equal(DispatchStatus.RejectedByLoadShedding, await InvokeAsync(middleware, Chat));

        // 미등록 = 필수. 한계에서도 살아남아야 서버가 "살아 있지만 쓸모없는" 상태를 면한다.
        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Auth));
    }

    [Fact]
    public async Task Shedding_recovers_when_load_drops()
    {
        (LoadSheddingMiddleware middleware, FakeLoad load) = Create();

        load.Current = LoadLevel.Critical;
        Assert.Equal(DispatchStatus.RejectedByLoadShedding, await InvokeAsync(middleware, Chat));

        load.Current = LoadLevel.Normal;
        Assert.Equal(DispatchStatus.Handled, await InvokeAsync(middleware, Chat));
    }

    [Fact]
    public void Empty_policy_is_rejected_at_assembly()
    {
        // 규칙 없는 열화 미들웨어는 아무것도 안 하면서 프레임마다 비용만 낸다 — 조립 실수다.
        Assert.Throws<InvalidOperationException>(() =>
            new LoadSheddingMiddleware(new FakeLoad(), new LoadSheddingOptions()));
    }

    [Theory]
    [InlineData(0.0, 0.9)]
    [InlineData(1.0, 0.9)]
    [InlineData(0.9, 0.9)]   // critical 이 elevated 보다 커야 한다
    [InlineData(0.5, 1.0)]
    public void Invalid_memory_thresholds_are_rejected(double elevated, double critical)
    {
        Assert.Throws<InvalidOperationException>(() => new MemoryLoadLevelSource(elevated, critical));
    }

    [Fact]
    public void Memory_source_reports_a_level_immediately()
    {
        // 시작 직후 Normal 로 오인하지 않도록 생성자가 첫 값을 채운다.
        MemoryLoadLevelSource source = new();

        // 실제 값은 환경에 따라 다르므로 유효한 값이라는 것만 고정한다(GC 시점 의존 —
        // 정책 검증은 가짜 소스로 한다).
        Assert.True(Enum.IsDefined(source.Current));
    }

    /// <summary>메시지 ID 만 의미가 있는 최소 커넥션 — 열화 판정은 커넥션을 보지 않는다.</summary>
    private sealed class FakeConnection : ChServerM.Connections.IConnection
    {
        private static readonly System.IO.Pipelines.Pipe DummyPipe = new();

        public ConnectionId Id => new(1, 0);

        public System.IO.Pipelines.PipeReader Input => DummyPipe.Reader;

        public System.IO.Pipelines.PipeWriter Output => DummyPipe.Writer;

        public ChServerM.Features.IFeatureCollection Features { get; } =
            new ChServerM.Features.FeatureCollection(capacity: 0);

        public System.Threading.CancellationToken ConnectionClosed => default;

        public void Abort(in ChServerM.Connections.ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
