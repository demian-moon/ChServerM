using System;
using System.Net;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Resilience;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 부하 기반 수용 제어(Phase 10 리소스 상한)를 검증한다 — 한계에서 신규 연결을 끊어
/// 기존 커넥션을 보호하고, <b>열화가 먼저·수용 거부가 나중</b>이라는 순서를 고정한다.
/// </summary>
public sealed class LoadLevelAdmissionControlTests
{
    private const ushort Telemetry = 990;

    private sealed class FakeLoad : ILoadLevelSource
    {
        public LoadLevel Current { get; set; } = LoadLevel.Normal;
    }

    private static IPEndPoint Any => new(IPAddress.Loopback, 1234);

    [Fact]
    public void Normal_load_admits()
    {
        FakeLoad load = new() { Current = LoadLevel.Normal };
        LoadLevelAdmissionControl control = new(load);

        Assert.True(control.TryAdmit(Any).IsAdmitted);
    }

    [Fact]
    public void Elevated_still_admits_by_default()
    {
        // 기본 임계가 Critical 인 것이 설계의 핵심 — 조금 밀린다고 문을 닫으면
        // 신규 사용자의 재시도가 accept 부하를 더한다.
        FakeLoad load = new() { Current = LoadLevel.Elevated };
        LoadLevelAdmissionControl control = new(load);

        Assert.True(control.TryAdmit(Any).IsAdmitted);
    }

    [Fact]
    public void Critical_rejects_with_reason()
    {
        FakeLoad load = new() { Current = LoadLevel.Critical };
        LoadLevelAdmissionControl control = new(load);

        AdmissionDecision decision = control.TryAdmit(Any);

        Assert.False(decision.IsAdmitted);
        Assert.Equal("load level Critical", decision.RejectionReason);
    }

    [Fact]
    public void Threshold_is_configurable()
    {
        FakeLoad load = new() { Current = LoadLevel.Elevated };
        LoadLevelAdmissionControl strict = new(load, LoadLevel.Elevated);

        Assert.False(strict.TryAdmit(Any).IsAdmitted);
    }

    [Fact]
    public void Recovers_when_load_drops()
    {
        FakeLoad load = new() { Current = LoadLevel.Critical };
        LoadLevelAdmissionControl control = new(load);

        Assert.False(control.TryAdmit(Any).IsAdmitted);

        load.Current = LoadLevel.Normal;
        Assert.True(control.TryAdmit(Any).IsAdmitted);
    }

    [Fact]
    public void Normal_threshold_is_rejected_at_assembly()
    {
        // Normal 에서 거부하면 평상시에도 전부 막힌다 — 조립 실수를 시작 시점에 잡는다.
        Assert.Throws<InvalidOperationException>(() =>
            new LoadLevelAdmissionControl(new FakeLoad(), LoadLevel.Normal));
    }

    [Fact]
    public void Null_source_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new LoadLevelAdmissionControl(null!));
    }

    [Fact]
    public async System.Threading.Tasks.Task Graduated_response_degrades_before_refusing()
    {
        // ★ 두 방어가 같은 신호를 공유하며 단계적으로 반응하는지 — 이 순서가 설계의 요점이다.
        // 열화는 공개 표면(미들웨어)으로 검증한다.
        FakeLoad load = new();
        LoadLevelAdmissionControl admission = new(load);
        LoadSheddingMiddleware shedding = new(
            load,
            new LoadSheddingOptions().ShedAbove(new MessageId(Telemetry), LoadLevel.Normal));

        // 평상시: 둘 다 통과.
        load.Current = LoadLevel.Normal;
        Assert.True(admission.TryAdmit(Any).IsAdmitted);
        Assert.Equal(ChServerM.Dispatch.DispatchStatus.Handled, await ShedAsync(shedding));

        // 1단계(Elevated): 비필수는 버리되 문은 열려 있다.
        load.Current = LoadLevel.Elevated;
        Assert.True(admission.TryAdmit(Any).IsAdmitted);
        Assert.Equal(ChServerM.Dispatch.DispatchStatus.RejectedByLoadShedding, await ShedAsync(shedding));

        // 2단계(Critical): 그래도 부족하면 신규 수용을 끊는다.
        load.Current = LoadLevel.Critical;
        Assert.False(admission.TryAdmit(Any).IsAdmitted);
        Assert.Equal(ChServerM.Dispatch.DispatchStatus.RejectedByLoadShedding, await ShedAsync(shedding));
    }

    /// <summary>텔레메트리 메시지 하나를 열화 미들웨어에 통과시킨다.</summary>
    private static async System.Threading.Tasks.ValueTask<ChServerM.Dispatch.DispatchStatus> ShedAsync(
        LoadSheddingMiddleware middleware)
    {
        FakeConnection connection = new();
        ChServerM.Dispatch.MessageContext context = new(connection);
        context.BeginFrame(
            new ChServerM.Framing.MessageEnvelope(new MessageId(Telemetry), ChServerM.Framing.FrameFlags.None, 0),
            default,
            receivedAt: default,
            default);

        return await middleware.InvokeAsync(
            context,
            static _ => System.Threading.Tasks.ValueTask.FromResult(ChServerM.Dispatch.DispatchStatus.Handled));
    }

    /// <summary>메시지 ID 만 의미가 있는 최소 커넥션.</summary>
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

        public System.Threading.Tasks.ValueTask DisposeAsync() => default;
    }

    [Fact]
    public void Composes_with_rate_limits_via_composite()
    {
        // 의도된 조립 — 서버 상태(부하)와 클라이언트 행위(속도)를 AND 로 건다.
        FakeLoad load = new() { Current = LoadLevel.Critical };
        ManualTime time = new();

        CompositeAdmissionControl composite = new(
            new ConnectionRateAdmissionControl(
                new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 1000, BurstCapacity = 1000 }, time),
            new LoadLevelAdmissionControl(load));

        // 속도 예산은 남았지만 부하가 한계라 거부된다.
        Assert.False(composite.TryAdmit(Any).IsAdmitted);

        load.Current = LoadLevel.Normal;
        Assert.True(composite.TryAdmit(Any).IsAdmitted);
    }

    private sealed class ManualTime : TimeProvider
    {
        public override long GetTimestamp() => 0;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
