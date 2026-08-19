using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 무중단 배포 절차 검증 (ADR-0053) — <b>순서는 이미 있었고 간격이 없었다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>여기서 고정하는 계약.</b> readiness 는 <b>즉시</b> 내려간다 ·
/// <b>⭐ 전파 대기 동안에도 새 접속을 받는다</b>(이것이 이 기능의 존재 이유다) ·
/// 대기가 끝나면 수용이 멈춘다 · 드레인이 상한 안에 끝나면 <c>CompletedWithinTimeout</c>
/// 이 참이고 상한을 치면 거짓이다 · 상한 만료가 <b>취소 예외로 새어 나오지 않는다</b>.
/// </para>
/// <para>
/// <b>⚠ 왜 "전파 대기 동안 수용" 이 계약인가.</b> readiness 를 내리고 <b>즉시</b> 언바인드하면,
/// 로드밸런서가 아직 이 노드로 보내고 있는 접속이 닫힌 수락 소켓을 만나 <b>RST 로 실패</b>한다
/// — 다른 노드로 넘어가는 것이 아니다. 그 창을 닫는 것이 <c>DrainAsync</c> 의 전부이므로,
/// 그것이 깨지면 이 기능은 존재할 이유가 없다.
/// </para>
/// </remarks>
public sealed class DrainOrchestrationTests
{
    private static readonly MessageId Ping = new(700);

    private static ChServerMServer Server(InMemoryTransportHub hub, InMemoryEndPoint endPoint) =>
        new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, new InMemoryTransportOptions()))
            .UseFraming(
                new FixedHeaderFrameDecoder(new FramingOptions { MaxPayloadLength = 1024 }),
                new FixedHeaderFrameEncoder(new FramingOptions { MaxPayloadLength = 1024 }))
            .ConfigureDispatcher(d => d.MapRaw(Ping, _ => ValueTask.FromResult(DispatchStatus.Handled)))
            .Build();

    [Fact]
    public async Task Drain_flipsReadinessImmediately_butKeepsAcceptingDuringPropagation()
    {
        // ⭐ 이 테스트가 DrainAsync 의 존재 이유다.
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"drain-{Guid.NewGuid():N}");
        await using ChServerMServer server = Server(hub, endPoint);
        await server.StartAsync(CancellationToken.None);

        DrainOptions options = new()
        {
            // ⚠ 전파 창은 CI 러너 멈칫(수백 ms~수 초)보다 길어야 한다. 600ms 창 +
            //   Task.Delay(200) 조합은 ubuntu 러너에서 대기가 창을 넘겨 깨어나
            //   "듣고 있는 서버가 없다"로 실패했다(2026-08-12, 실행 31550966690).
            //   이 테스트의 계약은 "창 안에서는 받는다"이지 창의 길이가 아니므로,
            //   창을 늘리는 것은 검증 강도를 깎지 않는다 — 협상·idle 타임아웃 정비
            //   (0da2dbe·136e66d)와 같은 원칙이다.
            ReadinessPropagationDelay = TimeSpan.FromSeconds(4),
            ConnectionDrainTimeout = TimeSpan.FromSeconds(5),
        };

        ValueTask<DrainReport> draining = server.DrainAsync(options, CancellationToken.None);

        // 전파 대기가 도는 동안: 아직 수용한다. 로드밸런서가 아직 이쪽으로 보내고 있다.
        // 대기는 드레인 절차가 굴러가기 시작할 만큼만 — 길게 잘수록 전파 창 여유를 깎는다.
        await Task.Delay(50);

        await using InMemoryClientTransport client = new(hub, null, new InMemoryTransportOptions());
        IConnection accepted = await client.ConnectAsync(endPoint, CancellationToken.None);

        // ⭐ 여기가 계약이다 — readiness 는 이미 내려갔는데 **접속은 아직 받는다**.
        Assert.NotNull(accepted);

        // 그리고 그 늦게 받은 접속도 드레인이 기다려 준다. 스스로 끝내면 깨끗이 끝난다.
        await accepted.DisposeAsync();

        DrainReport report = await draining;
        Assert.True(report.CompletedWithinTimeout);
    }

    [Fact]
    public async Task Drain_stopsAcceptingAfterPropagationDelay()
    {
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"drain-{Guid.NewGuid():N}");
        await using ChServerMServer server = Server(hub, endPoint);
        await server.StartAsync(CancellationToken.None);

        DrainOptions options = new()
        {
            ReadinessPropagationDelay = TimeSpan.FromMilliseconds(100),
            ConnectionDrainTimeout = TimeSpan.FromSeconds(5),
        };

        await server.DrainAsync(options, CancellationToken.None);

        // 절차가 끝난 뒤에는 붙을 수 없다.
        await using InMemoryClientTransport client = new(hub, null, new InMemoryTransportOptions());
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await client.ConnectAsync(endPoint, CancellationToken.None));
    }

    [Fact]
    public async Task Drain_withNoConnections_completesCleanly_andSpendsOnlyThePropagationDelay()
    {
        InMemoryTransportHub hub = new();
        await using ChServerMServer server = Server(hub, new InMemoryEndPoint($"drain-{Guid.NewGuid():N}"));
        await server.StartAsync(CancellationToken.None);

        DrainOptions options = new()
        {
            ReadinessPropagationDelay = TimeSpan.FromMilliseconds(300),
            ConnectionDrainTimeout = TimeSpan.FromSeconds(30),
        };

        long start = Stopwatch.GetTimestamp();
        DrainReport report = await server.DrainAsync(options, CancellationToken.None);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(report.CompletedWithinTimeout);

        // 붙어 있는 커넥션이 없으면 드레인 상한을 소진하지 않는다 — 30초짜리 상한을
        // 두고도 곧바로 끝나야 한다. 여기서 상한만큼 걸리면 배포가 통째로 느려진다.
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"드레인이 너무 오래 걸렸다: {elapsed}");

        // ⚠ 전파 대기를 실제로 기다렸는지 보되 **여유를 둔다** — Task.Delay 와 Stopwatch 는
        //   다른 시계라 경계에서 몇 ms 어긋난다. 여유 없이 못 박으면 가끔 깨지는 테스트가
        //   되고, 가끔 깨지는 테스트는 결국 무시된다. 대기가 통째로 빠졌는지를 보는 것이
        //   목적이므로 이 여유로 충분하다.
        Assert.True(
            report.Elapsed >= options.ReadinessPropagationDelay - TimeSpan.FromMilliseconds(30),
            $"전파 대기를 기다리지 않았다: {report.Elapsed}");
    }

    [Fact]
    public async Task Drain_reportsForcedClose_whenAConnectionOutlivesTheTimeout()
    {
        // ⚠ 상태 유지 프로필의 정상 경로다 — 긴 수명 커넥션은 스스로 끝나지 않으므로
        //   상한을 치고 강제 종료로 끝난다. 그 사실이 **보고에 드러나야** 한다.
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"drain-{Guid.NewGuid():N}");
        await using ChServerMServer server = Server(hub, endPoint);
        await server.StartAsync(CancellationToken.None);

        await using InMemoryClientTransport client = new(hub, null, new InMemoryTransportOptions());
        await using IConnection held = await client.ConnectAsync(endPoint, CancellationToken.None);

        DrainOptions options = new()
        {
            ReadinessPropagationDelay = TimeSpan.Zero,
            ConnectionDrainTimeout = TimeSpan.FromMilliseconds(300),
        };

        DrainReport report = await server.DrainAsync(options, CancellationToken.None);

        // 상한 만료는 **예외가 아니라 보고**로 나온다. 예외로 나오면 배포 스크립트가
        // "실패" 로 읽고, 정상적인 강제 종료와 진짜 오류를 구분할 수 없게 된다.
        Assert.False(report.CompletedWithinTimeout);
    }

    [Fact]
    public async Task Drain_cancellation_isDistinctFromTimeout_andThrows()
    {
        // ⚠ 드레인 상한과 절차 취소는 다른 것이다. 겹치면 상한이 지날 때마다
        //   "배포가 취소됐다" 로 읽힌다.
        InMemoryTransportHub hub = new();
        await using ChServerMServer server = Server(hub, new InMemoryEndPoint($"drain-{Guid.NewGuid():N}"));
        await server.StartAsync(CancellationToken.None);

        DrainOptions options = new()
        {
            ReadinessPropagationDelay = TimeSpan.FromSeconds(30),
            ConnectionDrainTimeout = TimeSpan.FromSeconds(30),
        };

        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await server.DrainAsync(options, cancel.Token));
    }

    [Fact]
    public async Task Drain_measuresElapsedWithInjectedTimeProvider()
    {
        // 감사 2026-08-18 H-5 — 드레인 절차가 UseTimeProvider 를 따르는지. 시각이 멈춘
        // 시간 원본을 주입하면 보고되는 경과도 0 이어야 한다 — 시스템 시계(Stopwatch)를
        // 쓰던 이전 구현이면 실제 경과가 새어 들어와 0 이 될 수 없다.
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"drain-{Guid.NewGuid():N}");
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, new InMemoryTransportOptions()))
            .UseFraming(
                new FixedHeaderFrameDecoder(new FramingOptions { MaxPayloadLength = 1024 }),
                new FixedHeaderFrameEncoder(new FramingOptions { MaxPayloadLength = 1024 }))
            .UseTimeProvider(new FrozenTimeProvider())
            .ConfigureDispatcher(d => d.MapRaw(Ping, _ => ValueTask.FromResult(DispatchStatus.Handled)))
            .Build();
        await server.StartAsync(CancellationToken.None);

        DrainOptions options = new()
        {
            ReadinessPropagationDelay = TimeSpan.Zero,
            ConnectionDrainTimeout = TimeSpan.FromSeconds(30),
        };

        DrainReport report = await server.DrainAsync(options, CancellationToken.None);

        Assert.True(report.CompletedWithinTimeout);
        Assert.Equal(TimeSpan.Zero, report.Elapsed);
    }

    /// <summary>시각이 0 에 멈춘 시간 원본 — 주입 여부만 판별한다. 타이머는 기본(실시간)이다.</summary>
    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(5, -1)]
    public async Task Drain_rejectsNegativeDurations(int propagationSeconds, int drainSeconds)
    {
        InMemoryTransportHub hub = new();
        await using ChServerMServer server = Server(hub, new InMemoryEndPoint($"drain-{Guid.NewGuid():N}"));

        DrainOptions options = new()
        {
            ReadinessPropagationDelay = TimeSpan.FromSeconds(propagationSeconds),
            ConnectionDrainTimeout = TimeSpan.FromSeconds(drainSeconds),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await server.DrainAsync(options, CancellationToken.None));
    }
}
