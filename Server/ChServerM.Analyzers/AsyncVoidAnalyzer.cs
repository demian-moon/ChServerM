using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ChServerM.Analyzers;

/// <summary>
/// <c>async void</c> 검출 — 메서드·로컬 함수·람다 전부 (CHSM3001).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <c>async void</c> 안에서 던져진 예외는 호출자가 아니라 동기화 컨텍스트
/// (서버에서는 스레드풀)로 간다 — 관측할 방법도, 완료를 기다릴 방법도 없다. 프레임워크의
/// 하드 룰("async void 금지")을 소비자 코드에서 컴파일 타임에 강제하는 장치다.
/// </para>
/// <para>
/// <b>예외 하나.</b> UI 이벤트 핸들러 형태(<c>(object, EventArgs)</c>)는 델리게이트 계약이
/// <c>void</c> 를 강제하므로 보고하지 않는다 — 관리 도구 UI 를 만드는 소비자를 막지 않는다.
/// </para>
/// <para><b>스레드 규약.</b> 상태 없는 분석기 — 동시 실행이 안전하다.</para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UsageDiagnostics.AsyncVoid);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // 메서드는 심볼 액션이 가장 싸다. 로컬 함수·람다는 심볼 액션이 방문하지 않으므로
        // 구문 노드 액션으로 따로 잡는다.
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(
            AnalyzeFunctionNode,
            SyntaxKind.LocalFunctionStatement,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.AnonymousMethodExpression);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;

        if (!method.IsAsync || !method.ReturnsVoid || IsEventHandlerShape(method))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            UsageDiagnostics.AsyncVoid, method.Locations[0], method.Name));
    }

    private static void AnalyzeFunctionNode(SyntaxNodeAnalysisContext context)
    {
        // 로컬 함수는 선언 심볼, 람다는 변환된 메서드 심볼로 잡는다.
        IMethodSymbol? function = context.Node is Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax local
            ? context.SemanticModel.GetDeclaredSymbol(local, context.CancellationToken) as IMethodSymbol
            : context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol as IMethodSymbol;

        if (function is null || !function.IsAsync || !function.ReturnsVoid || IsEventHandlerShape(function))
        {
            return;
        }

        string display = function.MethodKind == MethodKind.LocalFunction ? function.Name : "람다";
        context.ReportDiagnostic(Diagnostic.Create(
            UsageDiagnostics.AsyncVoid, context.Node.GetLocation(), display));
    }

    /// <summary>UI 이벤트 핸들러 형태인가 — 두 번째 매개변수가 <see cref="System.EventArgs"/> 파생.</summary>
    private static bool IsEventHandlerShape(IMethodSymbol method)
    {
        if (method.Parameters.Length != 2)
        {
            return false;
        }

        for (ITypeSymbol? type = method.Parameters[1].Type; type is not null; type = type.BaseType)
        {
            if (type.Name == "EventArgs"
                && type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true })
            {
                return true;
            }
        }

        return false;
    }
}
