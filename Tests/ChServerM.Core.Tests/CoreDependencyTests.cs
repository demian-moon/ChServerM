using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ChServerM.Core.Tests;

/// <summary>
/// CLAUDE.md 의 최상위 하드 룰을 강제하는 테스트.
/// "Core 는 서드파티 의존이 없다" 를 규약이 아니라 빌드 실패로 만든다.
/// </summary>
public sealed class CoreDependencyTests
{
    /// <summary>
    /// 프레임워크 어셈블리로 인정하는 이름의 <b>닫힌 목록</b>. 여기 없으면 서드파티로 본다.
    /// </summary>
    /// <remarks>
    /// <b>"System.*" 접두사 전체 허용은 구멍이었다</b>(2026-08-04 감사) — `System.IO.Hashing`
    /// 처럼 NuGet 으로만 배포되는 System.* 패키지가 통과한다. 특히 9.1 이 "안정 해시
    /// (XxHash3)"를 지시하고 있어 누군가 그것을 Core 에 넣을 유혹이 구조적으로 존재한다
    /// (ADR-0006 이 별도 패키지임을 확인하고 피보나치 해싱을 택한 이유).
    /// Core 에 새 BCL 참조가 생기면 이 목록에 <b>의식적으로</b> 추가한다 — 그 추가가 곧
    /// "net10.0 공유 프레임워크에 포함됨을 확인했다"는 선언이다.
    /// </remarks>
    private static readonly HashSet<string> FrameworkAssemblyNames =
        new(StringComparer.Ordinal)
        {
            "netstandard",
            "mscorlib",
            "Microsoft.CSharp",
            "Microsoft.VisualBasic.Core",
            "System",
            "System.Runtime",
            "System.Memory",
            "System.IO.Pipelines",
            "System.Net.Primitives",
            "System.Collections",
            "System.Linq",
            "System.Threading",
            "System.Threading.Tasks",
            "System.Runtime.InteropServices",
            "System.Diagnostics.Debug",
            "System.ComponentModel",
        };

    [Fact]
    public void Core_has_no_third_party_dependencies()
    {
        Assembly core = ChServerM.CoreAssembly.Instance;

        string[] violations = core.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? "(unnamed)")
            .Where(static name => !IsFrameworkAssembly(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"ChServerM.Core 에 서드파티 의존이 유입되었다. 벤더 통합은 " +
            $"ChServerM.<축>.<벤더> 어댑터 어셈블리로 분리한다. " +
            $"위반: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// Core 가 어셈블리 이름 문자열로 타입을 찾는 코드를 갖지 않았음을 확인한다.
    /// </summary>
    /// <remarks>
    /// 이 테스트는 Core 가 자기 자신 외의 어셈블리를 이름으로 로드하지 않는지 보는 것이 아니라,
    /// 타입 앵커가 실제로 Core 어셈블리를 가리키는지 확인하는 최소 계약 검증이다.
    /// </remarks>
    [Fact]
    public void CoreAssembly_anchor_points_at_core()
    {
        Assembly core = ChServerM.CoreAssembly.Instance;

        Assert.Equal("ChServerM.Core", core.GetName().Name);
    }

    private static bool IsFrameworkAssembly(string name) => FrameworkAssemblyNames.Contains(name);
}
