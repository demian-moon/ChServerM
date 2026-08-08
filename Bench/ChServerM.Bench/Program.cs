using System;
using System.Reflection;
using BenchmarkDotNet.Running;

namespace ChServerM.Bench;

/// <summary>
/// 벤치마크 진입점.
/// </summary>
/// <remarks>
/// <para>인자 없이 실행하면 대화형 선택 메뉴가 나온다. 특정 항목만 돌리려면 필터를 준다.</para>
/// <code>
///   dotnet run -c Release --project Bench/ChServerM.Bench -- --filter "*PartitionScaling*"
///   dotnet run -c Release --project Bench/ChServerM.Bench -- --filter "*" --join
/// </code>
/// <para>
/// <b>실제 코어 제한 측정은 밖에서 감싼다.</b> .NET 의 <c>ProcessorAffinity</c> 는
/// 리눅스에서 지원되지 않으므로 프로세스 안에서 코어를 줄일 수 없다.
/// </para>
/// <para>
/// <b>⚠ SMT 형제를 물리 코어로 착각하지 않는다 — 마스크가 틀리면 확장성 숫자가 통째로
/// 틀린다.</b> 2026-08-07 실측(ENV-B)에서 <b>SMT 형제는 인접 쌍</b>임을 확인했다:
/// 고-ILP 워크로드에서 CPU 0+1 은 1.08배(형제), CPU 0+16 은 1.93배(별개 코어)였다.
/// 따라서 <b>물리 코어 N 개 = 한 칸씩 건너뛴 비트</b>다 —
/// 1개 <c>0x1</c> / 2개 <c>0x5</c> / 4개 <c>0x55</c> / 8개 <c>0x5555</c> / 16개 <c>0x55555555</c>.
/// (<c>0xF</c> 는 4코어가 아니라 <b>2코어</b>다.) 적용 여부는 벤치 로그의
/// <c>ProcessorCount</c> 로 검증한다 — .NET 은 이 값에 어피니티를 반영한다.
/// </para>
/// <para>
/// 판정 기준선: 같은 마스크에서 순수 ALU 워크로드가 내는 확장성이 이 머신의 천장이다
/// (ENV-B 실측 1→16 코어에서 14.81배·효율 93%). 프레임워크 곡선은 그 아래에서 해석한다.
/// </para>
/// <code>
///   # Linux — 물리 코어 4개로 제한(형제 배치는 lscpu -e 로 확인 후 목록을 준다)
///   taskset -c 0,2,4,6 dotnet run -c Release --project Bench/ChServerM.Bench -- --filter "*Scaling*"
///
///   # Windows — start /affinity (16진 마스크). 빈 창 제목이 필요하다.
///   start "" /affinity 55 /wait /b dotnet run -c Release --project Bench/ChServerM.Bench -- --filter "*Scaling*"
/// </code>
/// </remarks>
internal static class Program
{
    private static void Main(string[] args)
    {
        UseUtf8Console();
        EnvironmentReport.Print();

        // 설정은 각 벤치마크 클래스의 [Config(typeof(BenchConfig))] 가 준다.
        // 여기서 전역 config 를 함께 넘기면 **같은 설정이 두 번 적용되어** BenchmarkDotNet 이
        // "The exporter JsonExporter-full is already present in configuration" 경고를 낸다.
        // 상시 켜진 경고는 사람이 출력을 무시하게 만들므로 중복을 없앤다.
        //
        // ⚠ 그 대가로 새 벤치마크 클래스가 속성을 빠뜨리면 기본 설정(ServerGC·MemoryDiagnoser
        // 없음)으로 조용히 측정된다. 새 클래스를 만들 때 속성을 반드시 붙인다.
        BenchmarkSwitcher
            .FromAssembly(Assembly.GetExecutingAssembly())
            .Run(args);
    }

    /// <summary>콘솔 출력을 UTF-8 로 맞춘다.</summary>
    /// <remarks>
    /// Windows 기본 ANSI 코드 페이지(예: CP949)에서는 한글이 깨진다.
    /// 출력이 리다이렉트된 환경에서는 던질 수 있으므로 삼킨다.
    /// </remarks>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // 콘솔이 없거나 리다이렉트됐다.
        }
        catch (PlatformNotSupportedException)
        {
            // 이 플랫폼은 인코딩 변경을 지원하지 않는다.
        }
    }
}
