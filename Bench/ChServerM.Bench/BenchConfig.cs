using System;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;

namespace ChServerM.Bench;

/// <summary>
/// 모든 벤치마크가 공유하는 측정 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> `docs/BENCHMARKS.md`의 규칙(Release, ServerGC, 할당량 함께 기록)을
/// 사람이 기억하는 대신 <b>설정으로 고정</b>한다. 벤치마크마다 다른 조건으로 측정하면
/// 수치를 서로 비교할 수 없고, 비교할 수 없는 수치는 성능 주장의 근거가 되지 못한다.
/// </para>
/// <para>
/// <b>할당량을 항상 함께 잰다.</b> 처리량만 보면 "빨라졌지만 GC 압력이 늘어난" 변경을
/// 놓친다. 이 프로젝트는 무할당을 목표로 하므로 할당량이 1급 지표다.
/// </para>
/// </remarks>
internal sealed class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);

        // 할당량과 GC 수집 횟수. docs/BENCHMARKS.md 규칙.
        AddDiagnoser(MemoryDiagnoser.Default);

        // 결과를 선언 순서대로 본다. 확장성 곡선은 파티션 수 순서로 읽어야 의미가 있다.
        WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared));

        // 실패한 벤치마크를 조용히 건너뛰지 않는다. 하나라도 실패하면 즉시 멈춘다 —
        // 부분 결과를 근거로 성능 주장을 하는 것이 최악이다.
        WithOptions(ConfigOptions.StopOnFirstError);

        AddJob(Job.Default
            .WithRuntime(CoreRuntime.Core10_0)
            .WithGcServer(true)
            .WithGcConcurrent(true));
    }
}

/// <summary>
/// 측정 환경을 사람이 읽을 수 있게 출력한다.
/// </summary>
/// <remarks>
/// BenchmarkDotNet 도 환경 요약을 찍지만, <b>물리 코어와 논리 코어를 구분해</b> 보여주지는
/// 않는다. 확장성 판정에서 이 구분이 결정적이다 — SMT 구간에서는 코어를 늘려도
/// 처리량이 선형으로 늘지 않는 것이 <b>정상</b>이므로, 그 구간을 판정에 넣으면
/// 멀쩡한 설계를 실패로 오판한다.
/// </remarks>
internal static class EnvironmentReport
{
    public static void Print()
    {
        Console.WriteLine("=== 측정 환경 ===");
        Console.WriteLine($"  OS               : {Environment.OSVersion}");
        Console.WriteLine($"  .NET             : {Environment.Version}");
        Console.WriteLine($"  ProcessorCount   : {Environment.ProcessorCount} (논리)");
        Console.WriteLine($"  ServerGC         : {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"  64bit            : {Environment.Is64BitProcess}");
        Console.WriteLine();
        Console.WriteLine("  물리 코어 수는 OS 도구로 확인해 docs/BENCHMARKS.md 의 환경 프로필에 기록한다.");
        Console.WriteLine("  확장성 판정은 물리 코어 수까지만 본다 — SMT 구간의 비선형성은 정상이다.");
        Console.WriteLine();
    }
}
