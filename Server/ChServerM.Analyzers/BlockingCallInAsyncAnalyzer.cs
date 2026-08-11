using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ChServerM.Analyzers;

/// <summary>
/// <c>async</c> 메서드 안의 블로킹 호출 검출 — <c>.Result</c>·<c>.Wait()</c>·
/// <c>GetAwaiter().GetResult()</c>·<c>Thread.Sleep</c> (CHSM3002).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> async 경로에서 블로킹하면 대기 중인 연속(continuation)이 돌아올 스레드를
/// 그 블로킹이 점유한다. 부하가 오르면 스레드풀 고갈로 서버 전체가 멈춘다 — 레거시는
/// <c>Dispose</c> 의 <c>Thread.Sleep(1000)</c> 하나로 이 상태를 만들 수 있었다(CLAUDE.md 9.5).
/// </para>
/// <para>
/// <b>판정 범위.</b> <c>async</c> 로 선언된 메서드·로컬 함수·람다 <b>안</b>만 본다.
/// 동기 메서드의 sync-over-async 도 위험하지만 정당한 경우(콘솔 Main 등)와 구분할 수 없어
/// 1차에서는 오탐 없는 범위만 잡는다 — 확대는 실수요가 생기면 한다.
/// </para>
/// <para><b>스레드 규약.</b> 컴파일레이션 시작 시 타입을 한 번 해석해 캡처만 한다 — 동시 실행 안전.</para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingCallInAsyncAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UsageDiagnostics.BlockingCallInAsync);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            Compilation compilation = start.Compilation;
            var targets = new BlockingTargets(
                task: compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"),
                taskOfT: compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1"),
                valueTaskOfT: compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"),
                thread: compilation.GetTypeByMetadataName("System.Threading.Thread"),
                awaiters: ImmutableArray.CreateRange(
                    new[]
                    {
                        compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.TaskAwaiter"),
                        compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.TaskAwaiter`1"),
                        compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ValueTaskAwaiter"),
                        compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ValueTaskAwaiter`1"),
                    }));

            start.RegisterOperationAction(
                operationContext => AnalyzePropertyReference(operationContext, targets),
                OperationKind.PropertyReference);
            start.RegisterOperationAction(
                operationContext => AnalyzeInvocation(operationContext, targets),
                OperationKind.Invocation);
        });
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context, BlockingTargets targets)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;

        if (reference.Property.Name != "Result" || !IsInsideAsyncFunction(reference, context.ContainingSymbol))
        {
            return;
        }

        INamedTypeSymbol? owner = reference.Property.ContainingType?.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(owner, targets.TaskOfT)
            || SymbolEqualityComparer.Default.Equals(owner, targets.ValueTaskOfT))
        {
            Report(context, reference, "'.Result'");
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, BlockingTargets targets)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        INamedTypeSymbol? owner = method.ContainingType?.OriginalDefinition;

        string? offender = method.Name switch
        {
            "Wait" when SymbolEqualityComparer.Default.Equals(owner, targets.Task) => "'Task.Wait()'",
            "Sleep" when SymbolEqualityComparer.Default.Equals(owner, targets.Thread) => "'Thread.Sleep'",
            "GetResult" when IsAwaiter(owner, targets.Awaiters) => "'GetAwaiter().GetResult()'",
            _ => null,
        };

        if (offender is not null && IsInsideAsyncFunction(invocation, context.ContainingSymbol))
        {
            Report(context, invocation, offender);
        }
    }

    private static bool IsAwaiter(INamedTypeSymbol? owner, ImmutableArray<INamedTypeSymbol?> awaiters)
    {
        foreach (INamedTypeSymbol? awaiter in awaiters)
        {
            if (awaiter is not null && SymbolEqualityComparer.Default.Equals(owner, awaiter))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>이 연산을 직접 감싸는 함수(람다·로컬 함수·메서드)가 <c>async</c> 로 선언됐는가.</summary>
    /// <remarks>
    /// <see cref="OperationAnalysisContext.ContainingSymbol"/> 은 람다 안의 연산에서도
    /// 바깥 메서드를 준다 — 그대로 쓰면 "async 메서드 안의 동기 람다"가 오탐이 된다.
    /// 그래서 연산 트리를 거슬러 올라 가장 가까운 함수 경계를 먼저 찾는다.
    /// </remarks>
    private static bool IsInsideAsyncFunction(IOperation operation, ISymbol containingSymbol)
    {
        for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation anonymous)
            {
                return anonymous.Symbol.IsAsync;
            }

            if (current is ILocalFunctionOperation localFunction)
            {
                return localFunction.Symbol.IsAsync;
            }
        }

        return containingSymbol is IMethodSymbol { IsAsync: true };
    }

    private static void Report(OperationAnalysisContext context, IOperation operation, string offender) =>
        context.ReportDiagnostic(Diagnostic.Create(
            UsageDiagnostics.BlockingCallInAsync, operation.Syntax.GetLocation(), offender));

    /// <summary>컴파일레이션당 한 번 해석한 블로킹 후보 타입 묶음.</summary>
    private sealed class BlockingTargets(
        INamedTypeSymbol? task,
        INamedTypeSymbol? taskOfT,
        INamedTypeSymbol? valueTaskOfT,
        INamedTypeSymbol? thread,
        ImmutableArray<INamedTypeSymbol?> awaiters)
    {
        public INamedTypeSymbol? Task { get; } = task;

        public INamedTypeSymbol? TaskOfT { get; } = taskOfT;

        public INamedTypeSymbol? ValueTaskOfT { get; } = valueTaskOfT;

        public INamedTypeSymbol? Thread { get; } = thread;

        public ImmutableArray<INamedTypeSymbol?> Awaiters { get; } = awaiters;
    }
}
