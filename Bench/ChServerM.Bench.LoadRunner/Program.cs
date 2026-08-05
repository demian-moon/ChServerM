using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;
using ChServerM.Transports;

namespace ChServerM.Bench.LoadRunner;

/// <summary>
/// Phase 5 게이트 측정용 부하 러너 (ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 1만 동시 접속 안정 동작과 에코 RPS·p50/p99/p999 기준선은
/// 마이크로 벤치마크(BenchmarkDotNet)로 잴 수 없다 — 종단 부하가 필요하다.
/// 자체 러너를 쓰는 이유와 대안 탈락은 ADR-0009.
/// </para>
/// <para>
/// <b>두 모드.</b> <c>server</c> 는 에코 서버를 프로덕션 조립(ServerBuilder + 파티션
/// 실행 모델)로 세우고, <c>client</c> 는 <c>TcpClientTransport</c> 로 부하를 건다.
/// 서로 다른 프로세스에서 돌려야 생성기의 GC·스케줄링이 서버 측정을 오염시키지 않는다.
/// </para>
/// <para>
/// <b>생성기 병목 방어(ADR-0009 부정 항목).</b> 결과에 생성기 프로세스의 CPU 사용률을
/// 함께 기록한다 — 생성기가 포화 상태면 그 수치는 서버가 아니라 생성기의 상한이다.
/// </para>
/// </remarks>
internal static class Program
{
    private const ushort EchoMessageId = 100;
    private const int MaxPayloadLength = 4096;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        Dictionary<string, string> options = ParseOptions(args);

        return args[0] switch
        {
            "server" => await RunServerAsync(options).ConfigureAwait(false),
            "client" => await RunClientAsync(options).ConfigureAwait(false),
            _ => Fail($"알 수 없는 모드: {args[0]}"),
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  server --port 15000 [--max-connections 20000] [--partitions N] [--seconds 120]");
        Console.WriteLine("         [--transport socket|kestrel (기본 socket, kestrel 은 ADR-0001 벤치 대결용)]");
        Console.WriteLine("         [--vectored true|false (기본 false, 송신 배칭 A/B 측정용)]");
        Console.WriteLine("  client --port 15000 --connections 512 [--payload 128] [--seconds 30]");
        Console.WriteLine("         [--rampup 5] [--active N (기본: 전부)] [--host 127.0.0.1]");
        Console.WriteLine("         [--pipeline P (기본 1=닫힌 루프. P>1 이면 burst P개 송신 후 P개 수신)]");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        for (int i = 1; i < args.Length - 1; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        return map;
    }

    private static int GetInt(Dictionary<string, string> options, string key, int fallback) =>
        options.TryGetValue(key, out string? value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : fallback;

    // ── 서버 모드 ────────────────────────────────────────────────────────────

    private static async Task<int> RunServerAsync(Dictionary<string, string> options)
    {
        int port = GetInt(options, "port", 15000);
        int maxConnections = GetInt(options, "max-connections", 20_000);
        int partitions = GetInt(options, "partitions", Environment.ProcessorCount);
        int seconds = GetInt(options, "seconds", 120);

        FramingOptions framing = new() { MaxPayloadLength = MaxPayloadLength };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        string transportKind = options.TryGetValue("transport", out string? kind) ? kind : "socket";
        IPEndPoint bindPoint = new(IPAddress.Loopback, port);

        // CA2000 억제: 전송·실행 모델의 소유권은 ChServerMServer 가 가져간다(빌더 계약).
#pragma warning disable CA2000
        IServerTransport transport;
        Func<int> connectionCount;

        if (string.Equals(transportKind, "kestrel", StringComparison.OrdinalIgnoreCase))
        {
            // ADR-0001 벤치 대결의 Kestrel 쪽. MaxConnections 상한 등 프로덕션 기능이
            // 없다 — 공정성 주석은 KestrelSocketServerTransport 모듈 문서 참조.
            KestrelSocketServerTransport kestrel = new(bindPoint);
            transport = kestrel;
            connectionCount = () => kestrel.ConnectionCount;
        }
        else
        {
            bool vectored = options.TryGetValue("vectored", out string? v)
                && bool.Parse(v);

            TcpServerTransport socket = new(
                bindPoint,
                new TcpTransportOptions { MaxConnections = maxConnections, UseVectoredSend = vectored });
            transport = socket;
            connectionCount = () => socket.ConnectionCount;
        }

        MessageDelegate echo = async context =>
        {
            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Envelope.MessageId,
                context.Payload,
                FrameFlags.None,
                context.Envelope.Sequence,
                context.CancellationToken).ConfigureAwait(false);

            return DispatchStatus.Handled;
        };

#pragma warning disable CA2007 // await using 선언에는 ConfigureAwait 를 붙일 수 없다. 콘솔 앱 — 컨텍스트 없음.
        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(decoder, encoder)
            .UseExecutionModel(new PartitionedExecutionModel(
                new PartitionedExecutionOptions { PartitionCount = partitions }))
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(EchoMessageId), echo))
            .Build();
#pragma warning restore CA2007
#pragma warning restore CA2000

        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"READY port={port} partitions={partitions} max={maxConnections} transport={transportKind}");

        // 주기적으로 커넥션 수·메모리를 찍는다. "1만 접속에서 안정"의 근거 데이터다.
        using Process self = Process.GetCurrentProcess();
        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            self.Refresh();
            Console.WriteLine(
                $"t={clock.Elapsed.TotalSeconds:F0}s conns={connectionCount()} " +
                $"workingSet={self.WorkingSet64 / (1024 * 1024)}MB " +
                $"gcHeap={GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024)}MB gen2={GC.CollectionCount(2)}");
        }

        Console.WriteLine("서버 종료 중...");
        using CancellationTokenSource drainLimit = new(TimeSpan.FromSeconds(10));
        await server.StopAsync(drainLimit.Token).ConfigureAwait(false);
        Console.WriteLine("DONE");
        return 0;
    }

    // ── 클라이언트 모드 ──────────────────────────────────────────────────────

    private static async Task<int> RunClientAsync(Dictionary<string, string> options)
    {
        int port = GetInt(options, "port", 15000);
        int connections = GetInt(options, "connections", 512);
        int payloadLength = GetInt(options, "payload", 128);
        int seconds = GetInt(options, "seconds", 30);
        int rampUpSeconds = GetInt(options, "rampup", 5);
        int active = GetInt(options, "active", connections);
        int pipeline = GetInt(options, "pipeline", 1);
        string host = options.TryGetValue("host", out string? h) ? h : "127.0.0.1";

        FramingOptions framing = new() { MaxPayloadLength = MaxPayloadLength };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);
        IPEndPoint target = new(IPAddress.Parse(host), port);

#pragma warning disable CA2007 // await using 선언에는 ConfigureAwait 를 붙일 수 없다. 콘솔 앱 — 컨텍스트 없음.
        await using TcpClientTransport clientTransport = new(new TcpTransportOptions());
#pragma warning restore CA2007

        Console.WriteLine($"연결 {connections}개 (램프업 {rampUpSeconds}s, 활성 {active})...");

        // ── 램프업: 초당 connections/rampUpSeconds 로 나눠 붙인다. 한꺼번에 붙이면
        //    accept 백로그·SYN 처리로 실패가 나고, 그것은 서버 측정이 아니다.
        List<IConnection> all = new(capacity: connections);
        Stopwatch rampClock = Stopwatch.StartNew();
        int perSecond = Math.Max(1, connections / Math.Max(1, rampUpSeconds));
        int connectFailures = 0;

        while (all.Count < connections)
        {
            int batch = Math.Min(perSecond, connections - all.Count);
            Task<IConnection>[] connecting = new Task<IConnection>[batch];

            for (int i = 0; i < batch; i++)
            {
                connecting[i] = clientTransport.ConnectAsync(target).AsTask();
            }

            foreach (Task<IConnection> task in connecting)
            {
                try
                {
                    all.Add(await task.ConfigureAwait(false));
                }
#pragma warning disable CA1031 // 실패 수 자체가 측정 결과다.
                catch (Exception)
                {
                    connectFailures++;
                }
#pragma warning restore CA1031
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        Console.WriteLine(
            $"연결 완료: {all.Count}/{connections} (실패 {connectFailures}, {rampClock.Elapsed.TotalSeconds:F1}s)");

        if (all.Count == 0)
        {
            return Fail("연결이 하나도 성립하지 않았다.");
        }

        // ── 부하: active 개 커넥션이 닫힌 루프(요청→응답→다음 요청)로 에코를 반복한다.
        //    닫힌 루프의 지연은 응답 왕복 그 자체다 — coordinated omission 이 없다.
        byte[] payload = new byte[payloadLength];
#pragma warning disable CA5394 // 측정용 페이로드 — 보안 난수가 필요 없다.
        Random.Shared.NextBytes(payload);
#pragma warning restore CA5394

        LatencyHistogram histogram = new();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds));
        long totalRequests = 0;
        long echoErrors = 0;

        using Process self = Process.GetCurrentProcess();
        self.Refresh();
        TimeSpan cpuBefore = self.TotalProcessorTime;
        Stopwatch loadClock = Stopwatch.StartNew();

        Task[] workers = new Task[Math.Min(active, all.Count)];
        for (int i = 0; i < workers.Length; i++)
        {
            IConnection connection = all[i];
            workers[i] = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        long start = Stopwatch.GetTimestamp();

                        // pipeline P — P개를 몰아 보내고 P개를 몰아 받는다. 서버 송신
                        // 파이프에 응답이 쌓여 다중 세그먼트 배치가 생기는 조건을 만든다
                        // (송신 배칭 A/B 측정용). 이때 지연은 burst 왕복 시간이다.
                        for (int p = 0; p < pipeline; p++)
                        {
                            await connection.WriteFrameAsync(
                                encoder, new MessageId(EchoMessageId), payload,
                                FrameFlags.None, sequence: 0).ConfigureAwait(false);
                        }

                        for (int p = 0; p < pipeline; p++)
                        {
                            await ReceiveFrameAsync(connection, decoder, stop.Token).ConfigureAwait(false);
                        }

                        histogram.Record(Stopwatch.GetElapsedTime(start));
                        Interlocked.Add(ref totalRequests, pipeline);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 측정 종료.
                }
#pragma warning disable CA1031 // 오류 수 자체가 측정 결과다.
                catch (Exception)
                {
                    Interlocked.Increment(ref echoErrors);
                }
#pragma warning restore CA1031
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        loadClock.Stop();

        self.Refresh();
        double cpuPercent = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds
            / (loadClock.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;

        // ── 보고. BENCHMARKS.md 에 그대로 옮길 수 있는 형태로 찍는다.
        long requests = Interlocked.Read(ref totalRequests);
        Console.WriteLine();
        Console.WriteLine($"결과 — 커넥션 {all.Count} (활성 {workers.Length}), 페이로드 {payloadLength}B, 파이프라인 {pipeline}, {loadClock.Elapsed.TotalSeconds:F1}s");
        if (pipeline > 1)
        {
            Console.WriteLine($"  (지연 분위수는 burst {pipeline}개 왕복 기준이다 — 단건 왕복과 비교 금지)");
        }
        Console.WriteLine($"  요청 수      : {requests:N0}  (오류 {echoErrors})");
        Console.WriteLine($"  RPS          : {requests / loadClock.Elapsed.TotalSeconds:N0}");
        Console.WriteLine($"  p50 / p99 / p999 : {histogram.Percentile(0.50)} / {histogram.Percentile(0.99)} / {histogram.Percentile(0.999)}");
        Console.WriteLine($"  최대 지연     : {histogram.Max}");
        Console.WriteLine($"  생성기 CPU    : {cpuPercent:F0}% (100%에 가까우면 생성기가 병목이다 — ADR-0009)");

        foreach (IConnection connection in all)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>프레임 하나가 도착할 때까지 읽는다. (통합 테스트와 같은 요령)</summary>
    private static async ValueTask ReceiveFrameAsync(
        IConnection connection,
        FixedHeaderFrameDecoder decoder,
        CancellationToken cancellationToken)
    {
        PipeReader reader = connection.Input;

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            FrameDecodeResult decoded = decoder.Decode(buffer);
            reader.AdvanceTo(decoded.Consumed, decoded.Examined);

            if (decoded.IsDecoded)
            {
                return;
            }

            if (decoded.IsFatal || read.IsCompleted)
            {
                throw new InvalidOperationException("에코 응답을 받기 전에 스트림이 끝났거나 손상됐다.");
            }
        }
    }

    /// <summary>
    /// 로그-선형 버킷 지연 히스토그램. p50/p99/p999 를 1% 안팎 오차로 집계한다.
    /// </summary>
    /// <remarks>
    /// 전 샘플 보관은 분당 수백 MB 라 GC 노이즈가 측정을 오염시킨다. HdrHistogram 을
    /// 들여오는 대신(의존 0 원칙, ADR-0009) 구간별 해상도를 고정한 버킷을 쓴다:
    /// 1ms 까지 1µs / 10ms 까지 10µs / 100ms 까지 100µs / 1s 까지 1ms / 그 위는 오버플로.
    /// 스레드 규약: <see cref="Record"/> 는 여러 워커가 동시에 불러도 안전하다(Interlocked).
    /// </remarks>
    private sealed class LatencyHistogram
    {
        // [0,1000)µs ×1µs + [1,10)ms ×10µs + [10,100)ms ×100µs + [100,1000)ms ×1ms + 오버플로
        private readonly long[] _buckets = new long[1000 + 900 + 900 + 900 + 1];
        private long _maxTicks;

        public void Record(TimeSpan latency)
        {
            double micros = latency.TotalMicroseconds;

            int index = micros switch
            {
                < 1_000 => (int)micros,
                < 10_000 => 1000 + (int)((micros - 1_000) / 10),
                < 100_000 => 1900 + (int)((micros - 10_000) / 100),
                < 1_000_000 => 2800 + (int)((micros - 100_000) / 1_000),
                _ => 3700,
            };

            Interlocked.Increment(ref _buckets[index]);

            long ticks = latency.Ticks;
            long observed;
            while (ticks > (observed = Volatile.Read(ref _maxTicks))
                && Interlocked.CompareExchange(ref _maxTicks, ticks, observed) != observed)
            {
                // 경합 시 재시도.
            }
        }

        public string Max => Format(TimeSpan.FromTicks(Volatile.Read(ref _maxTicks)).TotalMicroseconds);

        public string Percentile(double quantile)
        {
            long total = 0;
            foreach (long count in _buckets)
            {
                total += count;
            }

            if (total == 0)
            {
                return "-";
            }

            long rank = (long)Math.Ceiling(total * quantile);
            long seen = 0;

            for (int i = 0; i < _buckets.Length; i++)
            {
                seen += _buckets[i];
                if (seen >= rank)
                {
                    return Format(BucketLowerBoundMicros(i));
                }
            }

            return Format(BucketLowerBoundMicros(_buckets.Length - 1));
        }

        private static double BucketLowerBoundMicros(int index) => index switch
        {
            < 1000 => index,
            < 1900 => 1_000 + ((index - 1000) * 10),
            < 2800 => 10_000 + ((index - 1900) * 100),
            < 3700 => 100_000 + ((index - 2800) * 1_000),
            _ => 1_000_000,
        };

        private static string Format(double micros) => micros switch
        {
            < 1_000 => $"{micros:F0}µs",
            < 1_000_000 => $"{micros / 1_000:F2}ms",
            _ => $"{micros / 1_000_000:F2}s",
        };
    }
}
