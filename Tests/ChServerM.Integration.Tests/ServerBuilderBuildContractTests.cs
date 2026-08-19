using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transports;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// <see cref="ServerBuilder.Build"/> 의 1회 계약(감사 2026-08-18 H-3)과
/// <see cref="ChServerMServer.DisposeAsync"/> 의 정리 보장(H-6)을 고정한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>H-3.</b> Build 는 공유 디스패처에 관측 미들웨어·세션 라우팅을 추가하므로, 두 번째
/// 호출을 허용하면 메트릭/추적만 켠 조립에서 프레임 수·지연이 <b>조용히 2배로 계수</b>된다.
/// 시끄러운 예외가 조용한 오계측보다 낫다. 단, 필수 축 누락 같은 순수 검증 실패는 빌더를
/// 오염시키지 않으므로 계약을 소모하지 않아야 한다 — 축을 채워 다시 부를 수 있다.
/// </para>
/// <para>
/// <b>H-6.</b> <c>StopAsync</c>(실행 모델 정리 포함)가 던져도 전송은 반드시 정리돼야 한다.
/// 건너뛰면 수락 소켓·포트가 산 채로 남는데 <c>_disposed</c> 가드가 재시도까지 막아
/// 누수가 고착된다.
/// </para>
/// </remarks>
public sealed class ServerBuilderBuildContractTests
{
    private static readonly FramingOptions Framing = new() { MaxPayloadLength = 1024 };

    private static ServerBuilder Builder(IServerTransport transport) =>
        new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(Framing), new FixedHeaderFrameEncoder(Framing))
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(1), _ => ValueTask.FromResult(DispatchStatus.Handled)));

    [Fact]
    public async Task Build_secondCall_throwsInsteadOfDoubleWiring()
    {
        // 메트릭·추적을 켜서 "두 번째 Build 가 조용히 이중 배선하던" 바로 그 조립을 만든다.
        ServerBuilder builder = Builder(new FakeTransport())
            .UseMetrics(new CountingSink())
            .UseTracing();

        await using ChServerMServer first = builder.Build();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("Build", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_pureValidationFailure_doesNotConsumeTheOneShotContract()
    {
        // 전송 누락은 공유 상태를 건드리기 전의 순수 검증 실패다 — 축을 채우면 다시 지을 수 있다.
        ServerBuilder builder = new ServerBuilder()
            .UseFraming(new FixedHeaderFrameDecoder(Framing), new FixedHeaderFrameEncoder(Framing));

        Assert.Throws<InvalidOperationException>(() => builder.Build());

        builder.UseTransport(new FakeTransport());
        await using ChServerMServer server = builder.Build();
        Assert.NotNull(server);
    }

    [Fact]
    public async Task DisposeAsync_disposesTransport_evenWhenStopThrows()
    {
        // ★ H-6 의 핵심 — StopAsync 예외가 전송 정리를 건너뛰게 두면 포트가 산 채로 남는다.
        FakeTransport transport = new() { ThrowOnStop = true };
        ChServerMServer server = Builder(transport).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.DisposeAsync());

        Assert.True(transport.Disposed, "StopAsync 가 던졌는데 전송이 정리되지 않았다.");
    }

    [Fact]
    public async Task DisposeAsync_happyPath_stillDisposesTransport()
    {
        // try/finally 도입이 정상 경로를 바꾸지 않았는지 — 회귀 방지 짝 테스트.
        FakeTransport transport = new();
        ChServerMServer server = Builder(transport).Build();

        await server.DisposeAsync();

        Assert.True(transport.Disposed);
    }

    /// <summary>정리 여부만 기록하는 최소 전송. <c>ThrowOnStop</c> 으로 H-6 경로를 재현한다.</summary>
    private sealed class FakeTransport : IServerTransport
    {
        public bool ThrowOnStop { get; init; }

        public bool Disposed { get; private set; }

        public EndPoint? LocalEndPoint => null;

        public ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default) =>
            default;

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => default;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ThrowOnStop
                ? ValueTask.FromException(new InvalidOperationException("의도된 정지 실패 — H-6 재현용."))
                : default;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    /// <summary>아무것도 하지 않는 싱크 — UseMetrics 배선만 필요할 때 쓴다.</summary>
    private sealed class CountingSink : ChServerM.Diagnostics.IMetricsSink
    {
        public void Count(string name, long delta, ReadOnlySpan<ChServerM.Diagnostics.MetricTag> tags)
        {
        }

        public void Record(string name, double value, ReadOnlySpan<ChServerM.Diagnostics.MetricTag> tags)
        {
        }

        public void AdjustGauge(string name, long delta, ReadOnlySpan<ChServerM.Diagnostics.MetricTag> tags)
        {
        }
    }
}
