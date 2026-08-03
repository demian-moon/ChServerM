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
/// <code>
///   # Linux — 물리 코어 4개로 제한
///   taskset -c 0-3 dotnet run -c Release --project Bench/ChServerM.Bench -- --filter "*Scaling*"
///
///   # Windows — start /affinity (16진 마스크)
///   start /affinity F dotnet run -c Release --project Bench/ChServerM.Bench -- --filter "*Scaling*"
/// </code>
/// </remarks>
internal static class Program
{
    private static void Main(string[] args)
    {
        UseUtf8Console();
        EnvironmentReport.Print();

        BenchmarkSwitcher
            .FromAssembly(Assembly.GetExecutingAssembly())
            .Run(args, new BenchConfig());
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
