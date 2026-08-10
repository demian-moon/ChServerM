using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ChServerM.SourceGen;

/// <summary>
/// <c>[MessageHandler]</c> 가 붙은 핸들러를 찾아 컴파일 타임 검증(CHSM1xxx)과
/// 등록 코드(<c>MapGeneratedHandlers</c>)를 생성한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 수동 <c>Map&lt;T&gt;</c> 등록의 실패(중복 ID, 계약 미구현, 센티넬 ID)는
/// 런타임 조립 예외로만 드러난다. 이 제너레이터가 그 실패를 전부 <b>빌드 실패</b>로
/// 당긴다 — "리플렉션 대신 소스 제너레이터" 하드 룰의 디스패치 축 적용(ADR-0014).
/// </para>
/// <para>
/// <b>증분 계약.</b> <c>ForAttributeWithMetadataName</c> 기반이라 어트리뷰트가 없는
/// 타입은 파이프라인에 들어오지도 않는다. 모델은 값 동등성 record 로만 구성해
/// 편집당 재실행 범위를 최소화한다 — 대규모 프로젝트에서 IDE 를 멈추지 않기 위한
/// 전제 조건이다(ROADMAP Phase 7).
/// </para>
/// <para>
/// <b>생성 형태.</b> 핸들러 <b>인스턴스를 매개변수로 받는</b> 빌더 확장 메서드를 만든다.
/// 자동 <c>new</c> 는 하지 않는다 — 핸들러의 의존(인코더 등) 주입 방식을 프레임워크가
/// 강요하게 되기 때문이다. 대안 비교는 ADR-0014.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class MessageHandlerGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "ChServerM.Dispatch.MessageHandlerAttribute";
    private const string HandlerInterfaceDisplay = "ChServerM.Dispatch.IMessageHandler<TMessage>";
    private const string BuilderMetadataName = "ChServerM.Hosting.Dispatch.MessageDispatcherBuilder";

    /// <summary>MessageId.FrameworkRangeStart 와 같은 값. 생성기는 Core 를 참조하지 않으므로 복제한다.</summary>
    private const ushort FrameworkRangeStart = 40001;

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<HandlerModel>> handlers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => Create(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        // Hosting 미참조 어셈블리(핸들러만 담는 라이브러리)에서도 검증 진단은 살아야 한다.
        // 그래서 생성 가능 여부를 별도 신호로 결합한다.
        IncrementalValueProvider<bool> hostingAvailable = context.CompilationProvider
            .Select(static (compilation, _) => compilation.GetTypeByMetadataName(BuilderMetadataName) is not null);

        context.RegisterSourceOutput(
            handlers.Combine(hostingAvailable),
            static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    private static HandlerModel? Create(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || context.Attributes.Length == 0)
        {
            return null;
        }

        AttributeData attribute = context.Attributes[0];

        if (attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not ushort messageId)
        {
            // 인자가 깨진 상태 — 컴파일러가 이미 오류를 냈다. 여기서 겹쳐 알리지 않는다.
            return null;
        }

        INamedTypeSymbol[] handlerInterfaces = symbol.AllInterfaces
            .Where(static i => i.IsGenericType
                && i.OriginalDefinition.ToDisplayString() == HandlerInterfaceDisplay)
            .ToArray();

        string? messageType = handlerInterfaces.Length == 1
            ? handlerInterfaces[0].TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;

        return new HandlerModel(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol.Name,
            messageType,
            messageId,
            handlerInterfaces.Length,
            symbol.IsAbstract,
            symbol.IsGenericType,
            LocationModel.From(context.TargetNode.GetLocation()));
    }

    private static void Emit(
        SourceProductionContext context, ImmutableArray<HandlerModel> models, bool hostingAvailable)
    {
        if (models.IsDefaultOrEmpty)
        {
            return;
        }

        List<HandlerModel> valid = [];

        foreach (HandlerModel model in models)
        {
            bool usable = true;

            if (model.HandlerInterfaceCount == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DispatchDiagnostics.NotAHandler, model.Location.ToLocation(), model.HandlerTypeFqn));
                usable = false;
            }
            else if (model.HandlerInterfaceCount > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DispatchDiagnostics.AmbiguousMessageType, model.Location.ToLocation(),
                    model.HandlerTypeFqn, model.HandlerInterfaceCount));
                usable = false;
            }

            if (model.IsAbstract || model.IsGenericDefinition)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DispatchDiagnostics.NotInstantiable, model.Location.ToLocation(), model.HandlerTypeFqn));
                usable = false;
            }

            if (model.MessageId == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DispatchDiagnostics.SentinelMessageId, model.Location.ToLocation(), model.HandlerTypeFqn));
                usable = false;
            }
            else if (model.MessageId >= FrameworkRangeStart)
            {
                // 경고만 — 프레임워크 자신이 이 대역의 핸들러를 등록할 수 있어야 한다.
                context.ReportDiagnostic(Diagnostic.Create(
                    DispatchDiagnostics.FrameworkReservedRange, model.Location.ToLocation(), model.MessageId));
            }

            if (usable)
            {
                valid.Add(model);
            }
        }

        // 중복 ID — 어느 쪽이 정본인지 알 수 없으므로 전원 탈락시키고 각자 위치에 알린다.
        foreach (IGrouping<ushort, HandlerModel> group in valid.GroupBy(static m => m.MessageId).Where(static g => g.Count() > 1))
        {
            string names = string.Join(", ", group.Select(static m => m.HandlerTypeFqn));

            foreach (HandlerModel duplicate in group)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DispatchDiagnostics.DuplicateMessageId, duplicate.Location.ToLocation(),
                    duplicate.MessageId, names));
            }

            valid.RemoveAll(m => m.MessageId == group.Key);
        }

        if (valid.Count == 0)
        {
            return;
        }

        if (!hostingAvailable)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DispatchDiagnostics.HostingNotReferenced, valid[0].Location.ToLocation()));
            return;
        }

        valid.Sort(static (a, b) => a.MessageId.CompareTo(b.MessageId));
        context.AddSource("GeneratedMessageHandlerMap.g.cs", SourceText.From(Render(valid), Encoding.UTF8));
    }

    private static string Render(List<HandlerModel> handlers)
    {
        StringBuilder source = new();

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("// ChServerM.SourceGen 이 [MessageHandler] 선언에서 생성했다. 직접 수정하지 않는다.");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine("namespace ChServerM.Dispatch.Generated");
        source.AppendLine("{");
        source.AppendLine("    /// <summary>[MessageHandler] 핸들러의 컴파일 타임 등록 맵.</summary>");
        source.AppendLine("    internal static class GeneratedMessageHandlerMap");
        source.AppendLine("    {");
        source.AppendLine("        /// <summary>이 어셈블리의 모든 [MessageHandler] 핸들러를 등록한다.</summary>");
        source.AppendLine("        /// <remarks>중복 ID·계약 위반은 이미 컴파일 타임에 걸러졌다(CHSM1xxx).</remarks>");
        source.AppendLine("        public static global::ChServerM.Hosting.Dispatch.MessageDispatcherBuilder MapGeneratedHandlers(");
        source.AppendLine("            this global::ChServerM.Hosting.Dispatch.MessageDispatcherBuilder builder,");
        source.Append("            global::ChServerM.Serialization.IMessageSerializerProvider serializers");

        foreach (HandlerModel handler in handlers)
        {
            source.AppendLine(",");
            source.Append($"            {handler.HandlerTypeFqn} handler{handler.MessageId}");
        }

        source.AppendLine(")");
        source.AppendLine("        {");
        source.AppendLine("            if (builder is null) { throw new global::System.ArgumentNullException(nameof(builder)); }");
        source.AppendLine("            if (serializers is null) { throw new global::System.ArgumentNullException(nameof(serializers)); }");
        source.AppendLine();

        foreach (HandlerModel handler in handlers)
        {
            source.AppendLine("            builder.Map(");
            source.AppendLine($"                new global::ChServerM.Identity.MessageId({handler.MessageId}),");
            source.AppendLine($"                Require<{handler.MessageTypeFqn}>(serializers),");
            source.AppendLine($"                handler{handler.MessageId});");
        }

        source.AppendLine();
        source.AppendLine("            return builder;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::ChServerM.Serialization.IMessageSerializer<TMessage> Require<TMessage>(");
        source.AppendLine("            global::ChServerM.Serialization.IMessageSerializerProvider serializers)");
        source.AppendLine("            => serializers.Find<TMessage>()");
        source.AppendLine("               ?? throw new global::System.InvalidOperationException(");
        source.AppendLine("                   $\"{typeof(TMessage)} 의 직렬화기가 제공자에 없다. 조립 오류다 — 제공자 등록을 확인하라.\");");
        source.AppendLine("    }");
        source.AppendLine("}");

        return source.ToString();
    }

    /// <summary>핸들러 하나의 발견 결과. 값 동등성이 증분 캐시의 전제라 record 다.</summary>
    private sealed record HandlerModel(
        string HandlerTypeFqn,
        string HandlerTypeName,
        string? MessageTypeFqn,
        ushort MessageId,
        int HandlerInterfaceCount,
        bool IsAbstract,
        bool IsGenericDefinition,
        LocationModel Location);
}
