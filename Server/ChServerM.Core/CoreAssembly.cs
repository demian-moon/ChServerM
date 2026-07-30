using System.Reflection;

namespace ChServerM;

/// <summary>
/// <c>ChServerM.Core</c> 어셈블리의 타입 앵커.
/// </summary>
/// <remarks>
/// 어셈블리를 이름 문자열이 아니라 타입으로 참조해야 하는 곳에서 쓴다.
/// 문자열 기반 <c>Assembly.Load</c> 는 트리밍·Native AOT 에서 깨지므로 쓰지 않는다.
/// </remarks>
public static class CoreAssembly
{
    /// <summary>
    /// <c>ChServerM.Core</c> 어셈블리.
    /// </summary>
    public static Assembly Instance => typeof(CoreAssembly).Assembly;
}
