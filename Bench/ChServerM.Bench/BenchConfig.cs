using System;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters.Json;
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
/// <para>
/// <b>게이트 모드(<c>CHSM_BENCH_GATE=1</c>).</b> CI 회귀 게이트(<c>eng/bench-gate.ps1</c>)가
/// 켜는 모드로, 두 가지가 달라진다: ① 짧은 job(<see cref="Job.ShortRun"/>) — 게이트는 매 PR 에서
/// 돌아야 하므로 정밀도를 시간과 맞바꾼다, ② JSON 내보내기 — 게이트 스크립트가 사람이 읽는
/// 표가 아니라 기계가 읽는 결과를 파싱한다(콘솔 표는 로케일·컬럼 폭에 따라 달라져 파싱
/// 대상으로 쓸 수 없다).
/// </para>
/// <para>
/// <b>⚠ 짧은 job 의 정밀도 손실은 게이트가 <i>비율</i>만 보기 때문에 감당된다.</b> 게이트는
/// 절대 시간을 판정하지 않는다 — 공용 CI 러너에서 절대 시간은 이웃 부하로 20~30% 흔들려
/// 임계를 어떻게 잡아도 무용하거나 플래키다. 같은 실행 안의 두 팔 비율은 노이즈가 분자·분모에
/// 함께 실려 상당 부분 상쇄된다. 절대 수치 기준선은 이 머신(ENV-B)의 전체 job 실행이 담당한다.
/// </para>
/// </remarks>
internal sealed class BenchConfig : ManualConfig
{
    /// <summary>게이트 모드 스위치. <c>eng/bench-gate.ps1</c> 이 설정한다.</summary>
    internal const string GateModeVariable = "CHSM_BENCH_GATE";

    public BenchConfig()
    {
        bool gateMode = Environment.GetEnvironmentVariable(GateModeVariable) == "1";

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);

        // 할당량과 GC 수집 횟수. docs/BENCHMARKS.md 규칙.
        AddDiagnoser(MemoryDiagnoser.Default);

        // 결과를 선언 순서대로 본다. 확장성 곡선은 파티션 수 순서로 읽어야 의미가 있다.
        WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared));

        // 실패한 벤치마크를 조용히 건너뛰지 않는다. 하나라도 실패하면 즉시 멈춘다 —
        // 부분 결과를 근거로 성능 주장을 하는 것이 최악이다.
        WithOptions(ConfigOptions.StopOnFirstError);

        if (gateMode)
        {
            // 게이트 스크립트가 파싱할 기계 판독 결과. 콘솔 표는 파싱 대상이 아니다.
            AddExporter(JsonExporter.Full);
        }

        AddJob((gateMode ? Job.ShortRun : Job.Default)
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
