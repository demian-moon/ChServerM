using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Security;
using ChServerM.Security.Tls;
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
            "spike" => await RunSpikeAsync(options).ConfigureAwait(false),
            _ => Fail($"알 수 없는 모드: {args[0]}"),
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  server --port 15000 [--max-connections 20000] [--partitions N] [--seconds 120]");
        Console.WriteLine("         [--transport socket|kestrel (기본 socket, kestrel 은 ADR-0001 벤치 대결용)]");
        Console.WriteLine("         [--vectored true|false (기본 false, 송신 배칭 A/B 측정용)]");
        Console.WriteLine("         [--tls true|false (기본 false. 자가서명 인증서로 TLS 1.3, ADR-0017 A/B 측정용)]");
        Console.WriteLine("         [--defenses true|false (기본 false. 수용 제어를 켠다 — 스파이크 시나리오용)]");
        Console.WriteLine("         [--accept-rate N] [--accept-burst N (defenses 켰을 때 초당 수용 한도)]");
        Console.WriteLine("  client --port 15000 --connections 512 [--payload 128] [--seconds 30]");
        Console.WriteLine("         [--rampup 5] [--active N (기본: 전부)] [--host 127.0.0.1]");
        Console.WriteLine("         [--pipeline P (기본 1=닫힌 루프. P>1 이면 burst P개 송신 후 P개 수신)]");
        Console.WriteLine("         [--tls true|false (기본 false. 서버와 같게 맞춘다)]");
        Console.WriteLine("  spike  --port 15000 [--baseline 256] [--surge 2000] [--payload 128]");
        Console.WriteLine("         [--baseline-seconds 10] [--surge-seconds 10] [--recovery-seconds 10]");
        Console.WriteLine("         [--warmup-seconds 5 (측정에서 제외 — 티어드 JIT 승격 오염 방지)]");
        Console.WriteLine("         [--host 127.0.0.1]");
        Console.WriteLine("         스파이크 시나리오 — 기준 부하 위에 신규 접속 폭주를 얹고 구간별로 잰다.");
        Console.WriteLine("         판정은 '빨랐는가' 가 아니라 '이미 붙은 손님이 살아남았는가' 다.");
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
        bool tls = options.TryGetValue("tls", out string? tlsValue) && bool.Parse(tlsValue);

        FramingOptions framing = new() { MaxPayloadLength = MaxPayloadLength };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        string transportKind = options.TryGetValue("transport", out string? kind) ? kind : "socket";
        IPEndPoint bindPoint = new(IPAddress.Loopback, port);

        // CA2000 억제: 전송·실행 모델의 소유권은 ChServerMServer 가 가져간다(빌더 계약).
#pragma warning disable CA2000
        IServerTransport transport;
        Func<int> connectionCount;

        if (tls && string.Equals(transportKind, "kestrel", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("kestrel 프로토타입은 TLS 조합을 지원하지 않는다 — ADR-0001 재현 전용이다.");
        }

        // ⚠ 방어 장치는 기본 꺼짐이다 — 처리량 기준선 측정에 수용 제어가 끼면 재는 것이
        // 서버가 아니라 토큰 버킷이 된다. 스파이크 시나리오에서만 켠다.
        //
        // **주소별 제한(ADR-0026)은 여기서 켜지 않는다.** 루프백 부하는 전부 127.0.0.1 한
        // 주소에서 오므로 주소별 예산이 즉시 소진돼 아무것도 안 붙는다 — 방어가 동작하는
        // 것이지만 시나리오가 퇴화한다. 주소별 제한의 실부하 검증은 다중 호스트 생성기가
        // 필요하며 Phase 12 미완 항목이다.
        bool defenses = options.TryGetValue("defenses", out string? defensesValue)
            && bool.Parse(defensesValue);
        IAdmissionControl? admission = defenses
            ? new ConnectionRateAdmissionControl(new ConnectionRateAdmissionControlOptions
            {
                PermitsPerSecond = GetInt(options, "accept-rate", 500),
                BurstCapacity = GetInt(options, "accept-burst", 500),
            })
            : null;

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
                new TcpTransportOptions
                {
                    MaxConnections = maxConnections,
                    UseVectoredSend = vectored,
                    AdmissionControl = admission,
                });
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

        ServerBuilder builder = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(decoder, encoder)
            .UseExecutionModel(new PartitionedExecutionModel(
                new PartitionedExecutionOptions { PartitionCount = partitions }))
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(EchoMessageId), echo));

        // TLS A/B — 실행마다 새 자가서명 인증서. 재는 것은 암호 경로 비용이다(ADR-0017).
        using X509Certificate2? certificate = tls ? CreateSelfSignedCertificate() : null;
        if (certificate is not null)
        {
            builder.UseTransportSecurity(new TlsTransportSecurity(new TlsSecurityOptions
            {
                ServerCertificate = certificate,
            }));
        }

#pragma warning disable CA2007 // await using 선언에는 ConfigureAwait 를 붙일 수 없다. 콘솔 앱 — 컨텍스트 없음.
        await using ChServerMServer server = builder.Build();
#pragma warning restore CA2007
#pragma warning restore CA2000

        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"READY port={port} partitions={partitions} max={maxConnections} " +
            $"transport={transportKind} tls={(tls ? "on" : "off")}");

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
        bool tls = options.TryGetValue("tls", out string? tlsValue) && bool.Parse(tlsValue);

        // 벤치 전용 신뢰 정책 — 서버 인증서가 실행마다 새로 생기는 자가서명이라 핀 고정이
        // 불가능하다. 여기서 재는 것은 암호 경로 비용이지 신뢰 검증이 아니다.
        // 무조건 true 콜백은 프로덕션 금지 패턴이다(TlsSecurityOptions 문서).
        TlsTransportSecurity? tlsSecurity = tls
            ? new TlsTransportSecurity(new TlsSecurityOptions
            {
                TargetHost = host,
#pragma warning disable CA5359 // 벤치 전용 — 위 주석 참조. 실행마다 새 자가서명 인증서라 검증할 신뢰 체계가 없다.
                RemoteCertificateValidation = static (_, _, _, _) => true,
#pragma warning restore CA5359
            })
            : null;

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

        // ⚠ 진전 없는 라운드가 연속되면 중단한다.
        //
        // 이 조건이 없으면 대상 서버가 없거나 도중에 죽었을 때 all.Count 가 영원히 늘지 않아
        // **무한 루프**가 된다 — 1초마다 실패만 세며 프로세스가 살아남아 고아가 된다
        // (2026-08-05 발견). 루프 뒤의 "연결이 하나도 성립하지 않았다" 검사는 그 경우
        // **도달조차 하지 못했다.**
        //
        // 판정 기준은 "실패 수" 가 아니라 **진전 여부**다. 부하 시험에서는 일부 실패가 정상
        // 결과이므로(그 수 자체가 측정값이다), 실패를 세어 끊으면 멀쩡한 시험을 중단시킨다.
        // 반면 <b>한 라운드에서 단 하나도 붙지 못하는 상태가 연속</b>되면 그것은 부하가 아니라
        // 대상이 없다는 뜻이다.
        const int MaxConsecutiveFailedRounds = 3;
        int consecutiveFailedRounds = 0;

        while (all.Count < connections)
        {
            int countBeforeRound = all.Count;
            int batch = Math.Min(perSecond, connections - all.Count);
            Task<IConnection>[] connecting = new Task<IConnection>[batch];

            for (int i = 0; i < batch; i++)
            {
                connecting[i] = ConnectOneAsync(clientTransport, target, tlsSecurity);
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

            if (all.Count > countBeforeRound)
            {
                // 하나라도 붙었으면 진전이다 — 부분 실패는 정상 결과이므로 계수를 되돌린다.
                consecutiveFailedRounds = 0;
            }
            else if (++consecutiveFailedRounds >= MaxConsecutiveFailedRounds)
            {
                Console.WriteLine(
                    $"램프업 중단: {MaxConsecutiveFailedRounds}회 연속으로 한 개도 연결하지 못했다. " +
                    $"대상({target})이 살아 있는지 확인한다.");
                break;
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
        Console.WriteLine($"결과 — 커넥션 {all.Count} (활성 {workers.Length}), 페이로드 {payloadLength}B, 파이프라인 {pipeline}, tls={(tls ? "on" : "off")}, {loadClock.Elapsed.TotalSeconds:F1}s");
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

    /// <summary>커넥션 하나를 연결하고, TLS 가 켜져 있으면 핸드셰이크까지 마친다.</summary>
    /// <remarks>핸드셰이크 실패는 예외로 던져 램프업 루프의 연결 실패 계수에 잡히게 한다.</remarks>
    // ── 스파이크 모드 ────────────────────────────────────────────────────────

    /// <summary>
    /// 기준 부하 위에 <b>신규 접속 폭주</b>를 얹고 구간별(기준·스파이크·회복)로 측정한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>존재 이유 — Phase 9/10 방어의 유일한 종단 검증이다.</b> 수용 제어(ADR-0021)·속도
    /// 제한·단계적 열화(ADR-0029)·부하 기반 수용 거부는 전부 "과부하가 왔을 때" 를 위한
    /// 장치인데, 램프업·지속 시나리오는 <b>과부하를 만들지 않는다</b>. 통합 테스트가
    /// 인메모리로 폭주를 흉내내고는 있지만(2026-08-07), 실제 소켓·accept 백로그·커널 경로가
    /// 실린 폭주는 여기서만 볼 수 있다.
    /// </para>
    /// <para>
    /// <b>⚠ 두 무리를 분리하는 것이 이 시나리오의 설계 핵심이다.</b> 하나로 합치면 아무것도
    /// 판정할 수 없다 — 폭주 커넥션의 실패가 기준 커넥션의 실패와 섞이기 때문이다.
    /// </para>
    /// <list type="bullet">
    ///   <item><b>기준 무리</b>: 시작부터 끝까지 유지되는 "이미 붙은 손님". 이들이 <b>살아남는
    ///   것</b>이 방어의 목적이므로 구간별 지연·오류를 따로 잰다</item>
    ///   <item><b>폭주 무리</b>: 스파이크 구간에만 한꺼번에 붙는 신규 접속. 이들의
    ///   <b>거부는 실패가 아니라 성공 신호</b>다 — "거부가 붕괴보다 낫다"(CLAUDE.md 9.6)</item>
    /// </list>
    /// <para>
    /// <b>따라서 판정 기준은 "빨랐는가" 가 아니다.</b> ① 기준 무리의 오류가 0 인가(붕괴하지
    /// 않았는가) ② 스파이크 구간의 지연 악화가 회복 구간에 되돌아오는가(항구적 열화가
    /// 아닌가). 폭주 무리가 많이 거부됐다는 것은 그 자체로 나쁜 결과가 아니다.
    /// </para>
    /// <para>
    /// <b>램프업을 쓰지 않는다.</b> 폭주 무리는 의도적으로 한꺼번에 연결을 시도한다 —
    /// 완만하게 붙이면 수용 제어의 토큰이 계속 차서 과부하가 재현되지 않는다.
    /// </para>
    /// </remarks>
    private static async Task<int> RunSpikeAsync(Dictionary<string, string> options)
    {
        int port = GetInt(options, "port", 15000);
        int baselineCount = GetInt(options, "baseline", 256);
        int surgeCount = GetInt(options, "surge", 2000);
        int payloadLength = GetInt(options, "payload", 128);
        int baselineSeconds = GetInt(options, "baseline-seconds", 10);
        int surgeSeconds = GetInt(options, "surge-seconds", 10);
        int recoverySeconds = GetInt(options, "recovery-seconds", 10);
        int warmupSeconds = GetInt(options, "warmup-seconds", 5);
        string host = options.TryGetValue("host", out string? h) ? h : "127.0.0.1";

        FramingOptions framing = new() { MaxPayloadLength = MaxPayloadLength };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);
        IPEndPoint target = new(IPAddress.Parse(host), port);

#pragma warning disable CA2007 // await using 선언에는 ConfigureAwait 를 붙일 수 없다. 콘솔 앱 — 컨텍스트 없음.
        await using TcpClientTransport clientTransport = new(new TcpTransportOptions());
#pragma warning restore CA2007

        byte[] payload = new byte[payloadLength];
#pragma warning disable CA5394 // 측정용 페이로드 — 보안 난수가 필요 없다.
        Random.Shared.NextBytes(payload);
#pragma warning restore CA5394

        // 구간 0=기준, 1=스파이크, 2=회복. 기준 워커가 매 왕복마다 현재 구간을 읽어
        // 해당 히스토그램에 기록한다 — 구간을 나누지 않으면 스파이크의 영향이 평균에
        // 묻혀 "아무 일도 없었다" 로 보인다.
        string[] phaseNames = ["기준", "스파이크", "회복"];
        LatencyHistogram[] histograms = [new(), new(), new()];
        long[] phaseRequests = new long[3];
        double[] phaseElapsed = new double[3];
        int currentPhase = 0;
        long baselineErrors = 0;

        Console.WriteLine($"기준 커넥션 {baselineCount}개 연결 중...");
        List<IConnection> baselineConnections = new(capacity: baselineCount);
        for (int i = 0; i < baselineCount; i++)
        {
            try
            {
                baselineConnections.Add(await ConnectOneAsync(clientTransport, target, null).ConfigureAwait(false));
            }
#pragma warning disable CA1031 // 기준 무리는 측정 전제다 — 못 붙으면 아래에서 중단한다.
            catch (Exception)
            {
                // 아래 개수 검사로 처리.
            }
#pragma warning restore CA1031
        }

        if (baselineConnections.Count < baselineCount)
        {
            Console.WriteLine($"⚠ 기준 커넥션이 {baselineConnections.Count}/{baselineCount} 만 붙었다.");
        }

        if (baselineConnections.Count == 0)
        {
            return Fail("기준 커넥션이 하나도 성립하지 않았다 — 대상 서버를 확인한다.");
        }

        using CancellationTokenSource stop = new();
        Task[] baselineWorkers = new Task[baselineConnections.Count];
        for (int i = 0; i < baselineWorkers.Length; i++)
        {
            IConnection connection = baselineConnections[i];
            baselineWorkers[i] = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        long start = Stopwatch.GetTimestamp();
                        await connection.WriteFrameAsync(
                            encoder, new MessageId(EchoMessageId), payload,
                            FrameFlags.None, sequence: 0).ConfigureAwait(false);
                        await ReceiveFrameAsync(connection, decoder, stop.Token).ConfigureAwait(false);

                        // 구간 경계에서 왕복이 걸쳐 있으면 어느 쪽에 넣어도 되지만, 시작이
                        // 아니라 **완료 시점**의 구간에 넣는다 — 지연이 관측된 시점이 그때다.
                        int phase = Volatile.Read(ref currentPhase);
                        histograms[phase].Record(Stopwatch.GetElapsedTime(start));
                        Interlocked.Increment(ref phaseRequests[phase]);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 측정 종료.
                }
#pragma warning disable CA1031 // 오류 수 자체가 측정 결과다 — 기준 무리의 오류는 '붕괴' 신호다.
                catch (Exception)
                {
                    Interlocked.Increment(ref baselineErrors);
                }
#pragma warning restore CA1031
            });
        }

        List<IConnection> surgeConnections = [];
        int surgeAccepted = 0;
        int surgeRejected = 0;
        double surgeConnectSeconds = 0;
        long surgeAcceptedCount = 0;
        long surgeRejectedByProbe = 0;
        Task[] surgeWorkers = [];

        using CancellationTokenSource surgeStop = new();

        try
        {
            // ── 워밍업 (측정에서 버린다) ─────────────────────────────────────
            //
            // ⚠ 이 구간이 없으면 기준 구간이 티어드 JIT 승격 중에 측정되어 **부당하게
            // 느리게** 나온다. 실제로 그렇게 재보니 스파이크 구간의 처리량이 기준보다
            // 30% *높게* 나왔다 — 스파이크가 서버를 빠르게 만들 리 없으므로 그것은
            // 스파이크의 효과가 아니라 기준 구간의 워밍업 오염이었다. 기준이 오염되면
            // 스파이크의 실제 열화가 그만큼 상쇄되어 보이지 않는다.
            if (warmupSeconds > 0)
            {
                Console.WriteLine($"[워밍업] {warmupSeconds}s (측정에서 제외)...");
                await Task.Delay(TimeSpan.FromSeconds(warmupSeconds)).ConfigureAwait(false);
                histograms[0] = new LatencyHistogram();
                Interlocked.Exchange(ref phaseRequests[0], 0);
            }

            // ── 구간 0: 기준 ─────────────────────────────────────────────────
            Console.WriteLine($"[기준] {baselineSeconds}s 관측...");
            Stopwatch phaseClock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(baselineSeconds)).ConfigureAwait(false);
            phaseElapsed[0] = phaseClock.Elapsed.TotalSeconds;

            // ── 구간 1: 스파이크 ─────────────────────────────────────────────
            Volatile.Write(ref currentPhase, 1);
            phaseClock.Restart();
            Console.WriteLine($"[스파이크] 신규 접속 {surgeCount}개 동시 시도 + 부하 생성...");

            // ⚠ 램프업 없이 한꺼번에 — 완만하면 수용 제어 토큰이 계속 차서 과부하가 안 난다.
            Stopwatch connectClock = Stopwatch.StartNew();
            Task<IConnection>[] surging = new Task<IConnection>[surgeCount];
            for (int i = 0; i < surgeCount; i++)
            {
                surging[i] = ConnectOneAsync(clientTransport, target, null);
            }

            List<IConnection> connected = new(capacity: surgeCount);
            foreach (Task<IConnection> task in surging)
            {
                try
                {
                    connected.Add(await task.ConfigureAwait(false));
                }
#pragma warning disable CA1031 // 연결 실패 수가 측정 결과다.
                catch (Exception)
                {
                    surgeRejected++;
                }
#pragma warning restore CA1031
            }

            surgeConnectSeconds = connectClock.Elapsed.TotalSeconds;

            // ⚠⚠ **TCP 연결 성공은 수용을 뜻하지 않는다.**
            //
            // 수용 제어는 accept 이후에 판정하므로, 거부된 커넥션도 커널이 3-way 핸드셰이크를
            // 이미 끝내 클라이언트의 ConnectAsync 는 **성공한다**. 서버는 그 뒤 조용히 닫는다
            // (RejectionNotice 는 기본값이 비어 있어 통지도 없다). 실제로 이것 때문에 첫
            // 측정에서 수용 제어를 켜고도 "거부 0" 이 나왔다 — 클라이언트가 볼 수 없었을 뿐이다.
            //
            // 따라서 **왕복 한 번을 성공해야 수용된 것**으로 센다. 그 왕복이 곧 부하의 시작이다.
            surgeWorkers = new Task[connected.Count];
            for (int i = 0; i < connected.Count; i++)
            {
                IConnection connection = connected[i];
                surgeWorkers[i] = Task.Run(async () =>
                {
                    try
                    {
                        await connection.WriteFrameAsync(
                            encoder, new MessageId(EchoMessageId), payload,
                            FrameFlags.None, sequence: 0).ConfigureAwait(false);
                        await ReceiveFrameAsync(connection, decoder, surgeStop.Token).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // 왕복 실패 = 거부. 그 수가 측정 결과다.
                    catch (Exception)
                    {
                        Interlocked.Increment(ref surgeRejectedByProbe);
                        return;
                    }
#pragma warning restore CA1031

                    Interlocked.Increment(ref surgeAcceptedCount);

                    // 수용된 커넥션은 스파이크 구간 내내 부하를 만든다 — 커넥션 수만 늘리고
                    // 놀고 있으면 그것은 스파이크가 아니라 그냥 접속 수 증가다.
                    try
                    {
                        while (!surgeStop.IsCancellationRequested)
                        {
                            await connection.WriteFrameAsync(
                                encoder, new MessageId(EchoMessageId), payload,
                                FrameFlags.None, sequence: 0).ConfigureAwait(false);
                            await ReceiveFrameAsync(connection, decoder, surgeStop.Token).ConfigureAwait(false);
                        }
                    }
#pragma warning disable CA1031 // 스파이크 무리의 중도 실패는 정상 결과다(서버가 끊을 수 있다).
                    catch (Exception)
                    {
                        // 측정 종료 또는 서버 측 종료.
                    }
#pragma warning restore CA1031
                });
            }

            surgeConnections.AddRange(connected);

            await Task.Delay(TimeSpan.FromSeconds(surgeSeconds)).ConfigureAwait(false);
            phaseElapsed[1] = phaseClock.Elapsed.TotalSeconds;

            await surgeStop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(surgeWorkers).ConfigureAwait(false);

            surgeAccepted = (int)Interlocked.Read(ref surgeAcceptedCount);
            surgeRejected += (int)Interlocked.Read(ref surgeRejectedByProbe);
            Console.WriteLine(
                $"  수용 {surgeAccepted} / 거부 {surgeRejected} ({surgeConnectSeconds:F1}s 안에 시도) " +
                $"— 거부는 방어가 동작한 신호다");

            // ── 구간 2: 회복 ─────────────────────────────────────────────────
            // 폭주 무리를 걷어내고 기준 무리가 원래 성능으로 돌아오는지 본다.
            // 돌아오지 않으면 스파이크가 **항구적 열화**를 남긴 것이다(슬롯 누수·상태 오염).
            Console.WriteLine($"[회복] 폭주 커넥션 {surgeConnections.Count}개 해제 후 {recoverySeconds}s 관측...");
            foreach (IConnection connection in surgeConnections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            surgeConnections.Clear();
            Volatile.Write(ref currentPhase, 2);
            phaseClock.Restart();
            await Task.Delay(TimeSpan.FromSeconds(recoverySeconds)).ConfigureAwait(false);
            phaseElapsed[2] = phaseClock.Elapsed.TotalSeconds;
        }
        finally
        {
            // 락-프리 상태와 같은 규율 — 정리를 finally 에 둔다(CLAUDE.md 9.2).
            // 예외로 중도 이탈해도 폭주 워커가 살아남아 프로세스를 붙잡지 않게 한다.
            await surgeStop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(surgeWorkers).ConfigureAwait(false);

            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(baselineWorkers).ConfigureAwait(false);

            foreach (IConnection connection in surgeConnections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            foreach (IConnection connection in baselineConnections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        // ── 보고 ────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine(
            $"결과 — 기준 {baselineConnections.Count} 커넥션 유지, 폭주 {surgeCount} 시도, 페이로드 {payloadLength}B");
        Console.WriteLine();
        Console.WriteLine("  구간별 (기준 무리 = 이미 붙어 있던 커넥션)");
        Console.WriteLine("  구간       | 요청 수    | RPS      | p50      | p99      | p999");
        Console.WriteLine("  -----------|-----------|----------|----------|----------|----------");

        for (int phase = 0; phase < 3; phase++)
        {
            long requests = Interlocked.Read(ref phaseRequests[phase]);
            double rps = phaseElapsed[phase] > 0 ? requests / phaseElapsed[phase] : 0;
            Console.WriteLine(
                $"  {phaseNames[phase],-10} | {requests,9:N0} | {rps,8:N0} | " +
                $"{histograms[phase].Percentile(0.50),8} | {histograms[phase].Percentile(0.99),8} | " +
                $"{histograms[phase].Percentile(0.999),8}");
        }

        Console.WriteLine();
        Console.WriteLine($"  폭주 무리     : 수용 {surgeAccepted} / 거부 {surgeRejected} ({surgeConnectSeconds:F1}s 안에 시도)");
        Console.WriteLine($"  기준 무리 오류 : {Interlocked.Read(ref baselineErrors)}");
        Console.WriteLine();
        Console.WriteLine("  판정 기준 — '빨랐는가' 가 아니다:");
        Console.WriteLine("    ① 기준 무리 오류 0  = 폭주가 이미 붙은 손님을 죽이지 않았다");
        Console.WriteLine("    ② 회복 p99 ≈ 기준 p99 = 스파이크가 항구적 열화를 남기지 않았다");
        Console.WriteLine("    ③ 폭주 거부는 실패가 아니다 — 거부가 붕괴보다 낫다 (CLAUDE.md 9.6)");

        return 0;
    }

    private static async Task<IConnection> ConnectOneAsync(
        TcpClientTransport transport, IPEndPoint target, TlsTransportSecurity? tlsSecurity)
    {
        IConnection connection = await transport.ConnectAsync(target).ConfigureAwait(false);

        if (tlsSecurity is null)
        {
            return connection;
        }

        SecureChannelResult result = await tlsSecurity
            .SecureAsClientAsync(new BenchDuplexPipe(connection), connection.ConnectionClosed)
            .ConfigureAwait(false);

        if (!result.IsEstablished)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"TLS 핸드셰이크 실패: {result.Status}");
        }

        return new TlsClientConnection(connection, result.Channel!);
    }

    /// <summary>테스트 전용 자가서명 인증서. Schannel 이 ephemeral 키를 못 쓰므로 PFX 왕복으로 로드한다.</summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>벤치 전용 보안 커넥션 래퍼 — Hosting 의 SecuredConnection 은 internal 이라 최소 사본을 둔다.</summary>
    private sealed class TlsClientConnection(IConnection inner, ISecureChannel channel) : IConnection
    {
        public ConnectionId Id => inner.Id;

        public PipeReader Input => channel.Input;

        public PipeWriter Output => channel.Output;

        public IFeatureCollection Features => inner.Features;

        public CancellationToken ConnectionClosed => inner.ConnectionClosed;

        public void Abort(in ConnectionCloseInfo info) => inner.Abort(info);

        public async ValueTask DisposeAsync()
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary><see cref="IConnection"/> 바이트 경로의 <see cref="IDuplexPipe"/> 어댑터.</summary>
    private sealed class BenchDuplexPipe(IConnection connection) : IDuplexPipe
    {
        public PipeReader Input => connection.Input;

        public PipeWriter Output => connection.Output;
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
