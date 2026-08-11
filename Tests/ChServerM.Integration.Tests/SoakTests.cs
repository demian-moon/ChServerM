using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;
using Xunit.Abstractions;

namespace ChServerM.Integration.Tests;

/// <summary>
/// soak 하네스 — 지속 부하 + 커넥션 처치(connect/disconnect 반복) 아래에서 메모리와
/// 커넥션 슬롯이 <b>평탄</b>한지 확인한다 (Phase 10 게이트 후반부).
/// </summary>
/// <remarks>
/// <para>
/// <b>단발 벤치로는 안 잡히는 것</b>: 커넥션당 상태(버퍼 대여·피처·전송 등록)가 종료 시
/// 정리되지 않으면 <b>처치 반복에서 선형으로 샌다</b>. 이 하네스는 수천 회 connect→메시지
/// →disconnect 를 돌려 그 누수 부류를 드러낸다. 실제 결함(2026-08-04 감사 H1: 죽은 커넥션
/// 항목이 목록에 영구히 남아 상한 판정을 오염)이 정확히 이 부류다.
/// </para>
/// <para>
/// <b>기간은 파라미터다.</b> 기본은 게이트에서 도는 짧은 판(누수를 결정적으로 잡되 몇 초).
/// 환경변수 <c>CHSM_SOAK_SECONDS</c> 로 24시간(86400)까지 늘려 게이트 후반부의 정식 판을
/// 돌린다 — 그 장시간 실행은 CI 스케줄·수동 운영의 몫이다(단발 세션에서 24시간은 못 돈다).
/// </para>
/// <para>
/// <b>InMemory 전송으로 돈다</b> — 소켓 상한·포트 고갈 없이 전 파이프라인(프레이밍·디스패치·
/// 커넥션당 상태)을 결정적으로 태운다. 누수는 전송 종류와 무관한 상위 계층에서 나온다.
/// </para>
/// </remarks>
public sealed class SoakTests
{
    private const ushort EchoId = 900;

    private readonly ITestOutputHelper _output;

    public SoakTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Sustained_churn_keeps_memory_and_connection_slots_flat()
    {
        TimeSpan duration = ResolveDuration();
        const int workers = 8;
        const int framesPerConnection = 16;

        FramingOptions framing = new() { MaxPayloadLength = 4096 };
        FixedHeaderFrameEncoder serverEncoder = new(framing);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"soak-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new();
        InMemoryServerTransport transport = new(hub, endPoint, options);

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

        using CancellationTokenSource harnessTimeout = new(duration + TimeSpan.FromSeconds(60));
        await server.StartAsync(harnessTimeout.Token);

        // 워밍업 — JIT·풀 정착. 이후 메모리를 기준선으로 잡는다.
        long completedCycles = await RunChurnAsync(hub, endPoint, options, framing, workers, framesPerConnection,
            TimeSpan.FromMilliseconds(300), harnessTimeout.Token);
        long baseline = GetStableMemory();

        Stopwatch clock = Stopwatch.StartNew();
        long peakDuringRun = baseline;
        completedCycles += await RunChurnAsync(hub, endPoint, options, framing, workers, framesPerConnection,
            duration, harnessTimeout.Token, onSample: () =>
            {
                long now = GC.GetTotalMemory(forceFullCollection: false);
                if (now > peakDuringRun)
                {
                    peakDuringRun = now;
                }
            });
        clock.Stop();

        // 모든 클라이언트가 닫혔으니 서버 커넥션이 0으로 드레인돼야 한다 — 죽은 항목이 남으면 누수다.
        await WaitForDrainAsync(transport, harnessTimeout.Token);
        long final = GetStableMemory();

        _output.WriteLine(
            $"soak {clock.Elapsed.TotalSeconds:F1}s, cycles={completedCycles}, " +
            $"baseline={baseline / 1024}KB, peak={peakDuringRun / 1024}KB, final={final / 1024}KB, " +
            $"activeConnections={transport.ConnectionCount}");

        // 1. 커넥션 슬롯 누수(결정적) — 처치 후 활성 커넥션 0.
        Assert.Equal(0, transport.ConnectionCount);

        // 2. 메모리 평탄(통계적) — 정착 메모리가 기준선 근처로 돌아온다. 수천 사이클 동안
        //    사이클당 상태가 샜다면 이 배수를 훨씬 넘는다. 짧은 판은 대량 누수를, 24h 판은
        //    미세 추세를 잡는다.
        //
        //    ⚠ 임계 초과 시 바로 실패하지 않고 짧게 재정착을 기다리며 다시 잰다 — 느린
        //    CI 러너에서는 처치 직후 비동기 정리가 끝나기 전에 측정될 수 있다(2026-08-11
        //    CI: 717KB→5854KB 관측 후 다음 실행 통과). 누수라면 기다려도 내려오지 않으므로
        //    판정력은 잃지 않는다.
        long threshold = (baseline * 3 / 2) + (4L * 1024 * 1024);
        for (int settle = 0; settle < 10 && final > threshold; settle++)
        {
            await Task.Delay(200, harnessTimeout.Token);
            final = GetStableMemory();
        }

        Assert.True(final <= threshold,
            $"정착 메모리가 기준선 대비 과도하게 늘었다: 기준선 {baseline / 1024}KB, 최종 {final / 1024}KB. 누수 의심.");
    }

    /// <summary>기간을 정한다 — 환경변수 <c>CHSM_SOAK_SECONDS</c> 우선, 없으면 짧은 게이트 판.</summary>
    private static TimeSpan ResolveDuration()
    {
        string? env = Environment.GetEnvironmentVariable("CHSM_SOAK_SECONDS");
        if (!string.IsNullOrEmpty(env)
            && double.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // 게이트 기본: 대량 누수를 결정적으로 잡되 몇 초 안에 끝난다.
        return TimeSpan.FromSeconds(2);
    }

    /// <summary>여러 워커가 지정 기간 동안 connect→메시지→disconnect 를 반복한다.</summary>
    /// <returns>완료한 커넥션 처치 사이클 수.</returns>
    private static async Task<long> RunChurnAsync(
        InMemoryTransportHub hub,
        InMemoryEndPoint endPoint,
        InMemoryTransportOptions options,
        FramingOptions framing,
        int workers,
        int framesPerConnection,
        TimeSpan duration,
        CancellationToken cancellationToken,
        Action? onSample = null)
    {
        long cycles = 0;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(duration);

        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await OneConnectionCycleAsync(hub, endPoint, options, framing, framesPerConnection, stop.Token)
                            .ConfigureAwait(false);
                        Interlocked.Increment(ref cycles);
                        onSample?.Invoke();
                    }
                    catch (OperationCanceledException)
                    {
                        return; // 기간 종료.
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return Interlocked.Read(ref cycles);
    }

    /// <summary>커넥션 하나를 열어 프레임을 왕복시키고 닫는다 — 처치 사이클의 단위.</summary>
    private static async Task OneConnectionCycleAsync(
        InMemoryTransportHub hub,
        InMemoryEndPoint endPoint,
        InMemoryTransportOptions options,
        FramingOptions framing,
        int framesPerConnection,
        CancellationToken cancellationToken)
    {
        int received = 0;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ChServerMClient client = new ClientBuilder()
            .UseTransport(new InMemoryClientTransport(hub, null, options))
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(EchoId), _ =>
            {
                if (Interlocked.Increment(ref received) >= framesPerConnection)
                {
                    done.TrySetResult();
                }

                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        ClientSession session = await client.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

        byte[] payload = [1, 2, 3, 4];
        for (uint i = 0; i < framesPerConnection; i++)
        {
            await FrameWriter.WriteFrameAsync(
                session.Connection.Output, client.Encoder, new MessageId(EchoId), payload,
                FrameFlags.None, sequence: i, session.Connection.ConnectionClosed).ConfigureAwait(false);
        }

        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>활성 커넥션이 0으로 드레인될 때까지 기다린다.</summary>
    private static async Task WaitForDrainAsync(InMemoryServerTransport transport, CancellationToken cancellationToken)
    {
        while (transport.ConnectionCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>강제 GC 후 정착 메모리를 읽는다 — 회수 가능한 것을 제외한 실사용량.</summary>
    private static long GetStableMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
