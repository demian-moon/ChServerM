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
    /// 프레임워크 어셈블리로 인정하는 이름. 이 목록에 없으면 서드파티로 본다.
    /// </summary>
    private static readonly HashSet<string> FrameworkAssemblyNames =
        new(StringComparer.Ordinal)
        {
            "netstandard",
            "mscorlib",
            "Microsoft.CSharp",
            "Microsoft.VisualBasic.Core",
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

    private static bool IsFrameworkAssembly(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal)
        || string.Equals(name, "System", StringComparison.Ordinal)
        || FrameworkAssemblyNames.Contains(name);
}
