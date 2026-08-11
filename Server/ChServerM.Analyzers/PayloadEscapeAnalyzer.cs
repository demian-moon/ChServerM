using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ChServerM.Analyzers;

/// <summary>
/// <c>MessageContext.Payload</c> 의 수명 위반 검출 — 필드·속성으로의 저장 (CHSM3003).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 페이로드 버퍼는 커넥션당 1개를 재사용하고 핸들러가 반환하면
/// <c>EndFrame()</c> 이 참조를 끊는다(Phase 1 의 <c>MessageContext</c> 계약). 시퀀스를
/// 필드에 저장하면 다음 프레임이 같은 버퍼를 덮어써 <b>과거 메시지가 조용히 오염된다</b> —
/// 크래시가 아니라 잘못된 데이터가 도는, 가장 찾기 어려운 형태의 결함이다.
/// 레거시는 이 계약을 주석으로만 적었고 실제로 위반됐다.
/// </para>
/// <para>
/// <b>판정 범위.</b> <c>Payload</c> 를 <b>직접</b> 필드·속성에 대입하는 경우만 잡는다.
/// 지역 변수는 핸들러 수명 안이므로 합법이고, <c>ToArray()</c> 등 복사 결과의 저장도 합법이다.
/// <c>Slice()</c> 결과의 저장(같은 버퍼 공유)은 1차 범위 밖이다 — 휴리스틱을 넓히면
/// 오탐이 생기고, 오탐이 생기면 사용자는 진단을 끈다.
/// </para>
/// <para><b>스레드 규약.</b> 컴파일레이션 시작 시 타입을 한 번 해석해 캡처만 한다 — 동시 실행 안전.</para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PayloadEscapeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UsageDiagnostics.PayloadEscapesHandler);

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
            // ChServerM 을 참조하지 않는 컴파일레이션이면 아무것도 등록하지 않는다 — 비용 0.
            INamedTypeSymbol? messageContext =
                start.Compilation.GetTypeByMetadataName("ChServerM.Dispatch.MessageContext");
            if (messageContext is null)
            {
                return;
            }

            start.RegisterOperationAction(
                operationContext => AnalyzeAssignment(operationContext, messageContext),
                OperationKind.SimpleAssignment);
        });
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, INamedTypeSymbol messageContext)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        // 변환(암시적 캐스트 등)을 벗겨 실제 값의 출처를 본다.
        IOperation value = assignment.Value;
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        if (value is not IPropertyReferenceOperation source
            || source.Property.Name != "Payload"
            || !SymbolEqualityComparer.Default.Equals(source.Property.ContainingType, messageContext))
        {
            return;
        }

        // 지역 변수 대입은 합법 — 핸들러 수명 밖으로 나가는 저장(필드·속성)만 위반이다.
        ISymbol? target = assignment.Target switch
        {
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };

        if (target is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            UsageDiagnostics.PayloadEscapesHandler,
            assignment.Syntax.GetLocation(),
            target.Name));
    }
}
