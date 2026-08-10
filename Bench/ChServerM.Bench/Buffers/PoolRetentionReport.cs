using System;
using System.Buffers;
using System.Globalization;
using System.Threading;

namespace ChServerM.Bench.Buffers;

/// <summary>
/// <b>전용 <see cref="ArrayPool{T}"/> 이 붙드는 메모리의 상한</b> — ADR-0051 열린 결정의 선결 측정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0051 은 깊은 큐에서 <see cref="ArrayPool{T}.Shared"/> 가
/// 버킷당 보관 한계 때문에 할당을 내고 33% 느려지는 것을 관측했고, 전용 풀이 그것을
/// 되돌리는 것도 확인했다. 그러나 <b>기본값을 전용 풀로 바꾸려면 그 풀이 최악의 경우
/// 얼마를 붙드는지 알아야 한다</b> — 할당 문제를 메모리 폭증과 맞바꾸면 더 나빠진다.
/// 그 수치가 없어서 결정이 열린 채 남았고, 이 리포트가 그것을 닫는다.
/// </para>
/// <para>
/// <b>왜 BenchmarkDotNet 이 아닌가.</b> BDN 이 재는 것은 <b>연산당 할당량</b>이지
/// <b>정상 상태에서 붙들려 있는 양</b>이 아니다. 여기서 알고 싶은 것은 후자이므로
/// <see cref="GC.GetTotalMemory(bool)"/> 로 살아 있는 힙을 직접 본다. 같은 이유로
/// 이것은 테스트도 아니다 — 병렬로 도는 다른 테스트가 힙을 흔들면 숫자가 거짓이 된다.
/// </para>
/// <para>
/// <b>실행.</b> <c>dotnet run -c Release --project Bench/ChServerM.Bench -- retention</c>.
/// 유휴 트리밍 추이까지 보려면 <c>-- retention long</c> (풀당 10분).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 단일 스레드 전용. 전역 GC 상태를 읽으므로 동시에 다른 측정을
/// 돌리면 안 된다.
/// </para>
/// </remarks>
internal static class PoolRetentionReport
{
    /// <summary><see cref="ArrayPool{T}.Create(int, int)"/> 의 최소 버킷 크기.</summary>
    private const int MinimumBucketSize = 16;

    /// <summary>리포트를 출력한다.</summary>
    /// <param name="includeIdleTrend">유휴 트리밍 추이(풀당 10분)를 포함할지.</param>
    internal static void Run(bool includeIdleTrend)
    {
        Console.WriteLine($"GC.Server={System.Runtime.GCSettings.IsServerGC}  ProcCount={Environment.ProcessorCount}");
        Console.WriteLine();

        PrintFullCeiling();
        PrintSingleBucket();
        PrintOverMaxReturn();
        PrintExtrapolation();

        if (includeIdleTrend)
        {
            PrintIdleTrend("dedicated");
            PrintIdleTrend("shared");
        }
    }

    /// <summary>모든 버킷이 정원까지 찼을 때의 보유량 — 최악의 경우.</summary>
    /// <remarks>
    /// 정원을 채우려면 <b>동시에</b> 그만큼 빌려야 한다. 하나씩 빌리고 반납하면
    /// 같은 칸만 오갈 뿐 버킷이 차지 않는다.
    /// </remarks>
    private static void PrintFullCeiling()
    {
        Console.WriteLine("== A. 전 버킷 정원 (최악) ==");
        Console.WriteLine($"{"maxArrayLength",16} {"perBucket",10} {"닫힌 식",16} {"실측",16} {"오차",10}");

        foreach ((int max, int per) in new[]
        {
            (64 * 1024, 8),
            (64 * 1024, 64),
            (64 * 1024, 1024),
            (1024 * 1024, 16),
            (1024 * 1024, 64),
            (1024 * 1024, 256),
            (100_000, 8), // 2 의 거듭제곱이 아닌 상한 — 위로 올림되는지 확인한다
        })
        {
            long predicted = Ceiling(max, per);
            long actual = MeasureFullCeiling(max, per);
            double error = (actual - predicted) * 100.0 / predicted;
            Console.WriteLine(
                $"{max,16:N0} {per,10:N0} {Mib(predicted),16} {Mib(actual),16} {error,9:F2}%");
        }

        Console.WriteLine();
    }

    /// <summary>한 크기만 쓰는 워크로드 — 그 버킷 하나만 찬다.</summary>
    private static void PrintSingleBucket()
    {
        Console.WriteLine("== B. 한 크기만 쓰는 워크로드 (버킷 하나) ==");
        Console.WriteLine($"{"maxArrayLength",16} {"perBucket",10} {"rentSize",10} {"닫힌 식",16} {"실측",16}");

        foreach ((int max, int per, int rent) in new[]
        {
            (1024 * 1024, 1024, 1024),
            (1024 * 1024, 1024, 1024 * 1024),
            (1024 * 1024, 10_000, 1024),
        })
        {
            long predicted = (long)BucketSize(rent) * per;
            long actual = MeasureSingleBucket(max, per, rent);
            Console.WriteLine(
                $"{max,16:N0} {per,10:N0} {rent,10:N0} {Mib(predicted),16} {Mib(actual),16}");
        }

        Console.WriteLine();
    }

    /// <summary>상한을 넘는 대여는 풀에 담기지 않는다 — 그것이 상한을 실제로 상한이게 한다.</summary>
    private static void PrintOverMaxReturn()
    {
        Console.WriteLine("== C. 상한을 넘는 대여는 풀에 담기는가 ==");

        const int Max = 1024 * 1024;
        const int Count = 64;

        long before = Live();
        ArrayPool<byte> pool = ArrayPool<byte>.Create(Max, Count);
        byte[][] held = new byte[Count][];
        for (int n = 0; n < Count; n++)
        {
            held[n] = pool.Rent(2 * Max);
        }

        for (int n = 0; n < Count; n++)
        {
            pool.Return(held[n]);
        }

        held = null!;
        long retained = Live() - before;
        GC.KeepAlive(pool);

        Console.WriteLine(
            $"2 MiB × {Count} 를 1 MiB 풀에 반납 → 보유 {Mib(retained)} (담겼다면 {Mib(2L * Max * Count)})");
        Console.WriteLine();
    }

    /// <summary>실제 기본값 조합의 상한 — 여기부터가 결정의 입력이다.</summary>
    private static void PrintExtrapolation()
    {
        Console.WriteLine("== D. 기본값 조합의 상한 (검증된 닫힌 식) ==");
        Console.WriteLine($"{"MaxPayloadLength",18} {"SendQueueDepth",16} {"전 버킷",18} {"상한 버킷만",18}");

        foreach ((int max, int depth) in new[]
        {
            (64 * 1024, 1024),
            (64 * 1024, 10_000),
            (1024 * 1024, 1024),
            (1024 * 1024, 10_000),
            (1024 * 1024, 20_000),
            (4 * 1024 * 1024, 10_000),
        })
        {
            Console.WriteLine(
                $"{max,18:N0} {depth,16:N0} {Mib(Ceiling(max, depth)),18} {Mib((long)BucketSize(max) * depth),18}");
        }

        Console.WriteLine();
    }

    /// <summary>피크가 지나간 뒤 돌려주는가 — 상한이 일시적인지 영구적인지를 가른다.</summary>
    /// <remarks>
    /// <b>상한이 크다는 것만으로는 결정을 못 한다.</b> 큐가 깊으면 그만큼의 버퍼는
    /// 최고 부하에서 <b>어차피</b> 동시에 살아 있어야 한다 — 풀이 없어도 마찬가지다.
    /// 진짜 질문은 피크가 지나간 뒤 그 메모리가 프로세스로 돌아오는가이고,
    /// <see cref="ArrayPool{T}.Shared"/> 의 트리밍은 <b>미사용 경과 시간</b> 기반이라
    /// 시간을 흘려보내지 않으면 이 질문에 답할 수 없다.
    /// </remarks>
    private static void PrintIdleTrend(string which)
    {
        const int Payload = 1024 * 1024;
        const int Count = 256;
        const int Minutes = 10;

        ArrayPool<byte> pool = which == "shared"
            ? ArrayPool<byte>.Shared
            : ArrayPool<byte>.Create(Payload, Count);

        long before = Live();

        byte[][] held = new byte[Count][];
        for (int n = 0; n < Count; n++)
        {
            held[n] = pool.Rent(Payload);
        }

        for (int n = 0; n < Count; n++)
        {
            pool.Return(held[n]);
        }

        held = null!;

        Console.WriteLine($"== E. {which} · 1 MiB × {Count} 피크 후 유휴 추이 ==");
        Console.WriteLine($"{"분",6} {"보유",14}");
        Console.WriteLine($"{0,6} {Mib(Live() - before),14}");

        for (int minute = 1; minute <= Minutes; minute++)
        {
            // 트리밍은 Gen2 콜백에 걸려 있으므로 GC 가 돌아야 기회가 생긴다.
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(10_000);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            Console.WriteLine($"{minute,6} {Mib(Live() - before),14}");
        }

        GC.KeepAlive(pool);
        Console.WriteLine();
    }

    /// <summary>모든 버킷을 정원까지 채운 뒤 붙들린 바이트를 잰다.</summary>
    private static long MeasureFullCeiling(int maxArrayLength, int maxArraysPerBucket)
    {
        long before = Live();

        ArrayPool<byte> pool = ArrayPool<byte>.Create(maxArrayLength, maxArraysPerBucket);

        for (int size = MinimumBucketSize; size <= BucketSize(maxArrayLength); size <<= 1)
        {
            byte[][] held = new byte[maxArraysPerBucket][];
            for (int n = 0; n < maxArraysPerBucket; n++)
            {
                held[n] = pool.Rent(size);
            }

            for (int n = 0; n < maxArraysPerBucket; n++)
            {
                pool.Return(held[n]);
            }
        }

        long retained = Live() - before;
        GC.KeepAlive(pool);
        return retained;
    }

    /// <summary>한 크기만 빌렸다 반납했을 때 붙들린 바이트를 잰다.</summary>
    private static long MeasureSingleBucket(int maxArrayLength, int maxArraysPerBucket, int rentSize)
    {
        long before = Live();

        ArrayPool<byte> pool = ArrayPool<byte>.Create(maxArrayLength, maxArraysPerBucket);
        byte[][] held = new byte[maxArraysPerBucket][];
        for (int n = 0; n < maxArraysPerBucket; n++)
        {
            held[n] = pool.Rent(rentSize);
        }

        for (int n = 0; n < maxArraysPerBucket; n++)
        {
            pool.Return(held[n]);
        }

        held = null!;

        long retained = Live() - before;
        GC.KeepAlive(pool);
        return retained;
    }

    /// <summary>대여 크기가 담기는 버킷의 실제 크기 — <b>2 의 거듭제곱으로 올림된다</b>.</summary>
    private static int BucketSize(int size)
    {
        int bucket = MinimumBucketSize;
        while (bucket < size)
        {
            bucket <<= 1;
        }

        return bucket;
    }

    /// <summary>전 버킷 정원의 닫힌 식 — 실측과 오차 0.5% 이내임을 A 에서 확인한다.</summary>
    /// <remarks>
    /// 버킷은 16 B 부터 2 배씩 늘며 <paramref name="maxArrayLength"/> 를 덮을 때까지 만들어지므로
    /// 합은 <c>16 × (2^(k+1) − 1)</c> 이고, 이는 <b>최상위 버킷 크기의 약 2 배</b>다.
    /// 따라서 상한 ≈ <c>2 × maxArrayLength × maxArraysPerBucket</c> 로 어림할 수 있다.
    /// </remarks>
    private static long Ceiling(int maxArrayLength, int maxArraysPerBucket)
    {
        long sum = 0;
        for (int size = MinimumBucketSize; size <= BucketSize(maxArrayLength); size <<= 1)
        {
            sum += size;
        }

        return sum * maxArraysPerBucket;
    }

    /// <summary>살아 있는 관리 힙 크기. 여러 번 수거해 부유물을 걷어낸다.</summary>
    private static long Live()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string Mib(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("N2", CultureInfo.InvariantCulture) + " MiB";
}
