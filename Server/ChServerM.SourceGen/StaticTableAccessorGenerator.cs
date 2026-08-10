using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ChServerM.SourceGen;

/// <summary>
/// <c>[StaticTableRow]</c> 선언에서 <b>스키마와 강타입 접근자를 함께</b> 생성한다
/// (진단 대역 <c>CHSM2xxx</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 서수를 사람이 관리하지 않게 하는 것.</b> 데이터 테이블 축은 조회 비용과
/// 컬처 의존을 없애려고 <b>문자열 키 조회를 서수 조회로</b> 바꿨다(ADR-0041 결정 4).
/// 그런데 서수를 손으로 적으면 새 위험이 생긴다: 열을 <b>가운데에 하나 끼워 넣는 순간</b>
/// 뒤따르는 모든 <c>GetInt32(row, 3)</c> 이 조용히 다른 열을 읽는다. 컴파일도 되고 예외도
/// 나지 않으며 <b>밸런스 값만 틀린다</b> — 레거시의 문제점 4를 고치다 만든 함정이다.
/// </para>
/// <para>
/// 그래서 이 제너레이터의 1차 산출물은 진단이 아니라 <b>스키마와 접근자를 같은 선언에서
/// 함께 만드는 것</b>이다. 스키마의 열 순서와 접근자의 서수가 <b>같은 입력에서 나오므로</b>
/// 둘이 어긋날 수 있는 경로가 존재하지 않는다. 진단(CHSM2xxx)은 그 선언 자체가 앞뒤가
/// 맞는지를 지키는 2차 방어선이며, 전부 <b>런타임이었다면 기동 시점 스키마 조립 예외</b>였을
/// 것들을 빌드 실패로 당긴다.
/// </para>
///
/// <para>
/// <b>생성물</b> — 행 타입 <c>ItemRow</c> 하나당:
/// </para>
/// <list type="bullet">
///   <item><c>ItemRow.Schema</c> — 선언에서 만든 <c>StaticTableSchema</c>. 로딩에 이것을 쓴다</item>
///   <item><c>partial</c> 속성 구현 — 각각 <b>컴파일 타임 상수 서수</b>로 읽는다</item>
///   <item><c>ItemRow.Table</c> — 표 하나의 강타입 뷰(인덱서·<c>TryGetRow</c>·무할당 열거)</item>
/// </list>
///
/// <para>
/// <b>⚠ 뷰는 묶을 때 스키마 동일성을 확인한다.</b> <c>ItemRow.Table</c> 생성자는 대상
/// <c>StaticTable</c> 이 <b>바로 이 <c>Schema</c> 인스턴스</b>로 로딩됐는지 참조 비교로
/// 확인하고, 아니면 던진다. 구조만 같은 다른 스키마로 로딩된 표를 받아들이면 서수가 같다는
/// 보장이 사라지고, 그 순간 이 제너레이터가 없앤 바로 그 위험이 되돌아온다.
/// </para>
///
/// <para>
/// <b>중복 테이블 이름은 여기서 보지 않는다.</b> 두 행 타입이 같은 표 이름을 주장하는 것은
/// 오류지만, 그것을 컴파일 타임에 잡으려면 모든 행 타입을 <c>Collect</c> 해야 하고 그러면
/// <b>행 타입 하나를 편집할 때 전부 다시 생성</b>된다. 런타임 <c>StaticTableSetBuilder</c> 가
/// 기동 시점에 같은 판정을 하므로(묶음에 같은 이름을 두 번 넣으면 실패) 증분성을 택했다.
/// </para>
///
/// <para>
/// <b>증분 계약.</b> <c>ForAttributeWithMetadataName</c> 기반이라 어트리뷰트가 없는 타입은
/// 파이프라인에 들어오지도 않는다. 모델은 값 동등성 record 와
/// <see cref="EquatableArray{T}"/> 로만 구성한다 — <c>ImmutableArray</c> 를 그대로 담으면
/// 참조 비교라 캐시가 매번 깨진다.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StaticTableAccessorGenerator : IIncrementalGenerator
{
    private const string RowAttributeMetadataName = "ChServerM.DataTable.StaticTableRowAttribute";
    private const string ColumnAttributeMetadataName = "ChServerM.DataTable.StaticTableColumnAttribute";

    private const string Ns = "global::ChServerM.DataTable";

    /// <summary>생성 멤버가 차지하는 이름. 열 속성이 이 이름을 쓰면 충돌한다.</summary>
    private static readonly string[] ReservedMemberNames =
        ["Schema", "Table", "TableName", "RowIndex", "_table", "_row", "SchemaHolder", "Equals", "GetHashCode"];

    /// <summary>
    /// 선언한 형식을 그대로 재현하는 표시 형식.
    /// </summary>
    /// <remarks>
    /// <b>널 허용 표기를 반드시 포함해야 한다.</b> <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>
    /// 은 <c>?</c> 를 빼고 출력한다 — 그대로 쓰면 <c>string?</c> 로 선언한 속성의 구현부가
    /// <c>string</c> 이 되어 <b>부분 속성 시그니처가 어긋나고</b>, 선택 열의 null 이 널 비허용
    /// 계약을 타고 호출자에게 흘러간다.
    /// </remarks>
    private static readonly SymbolDisplayFormat DeclaredTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 어트리뷰트가 ChServerM.DataTable 에 있으므로, 어트리뷰트를 찾았다는 것은
        // 그 어셈블리가 이미 참조돼 있다는 뜻이다 — 디스패치 제너레이터의 CHSM1007 같은
        // "참조 없음" 분기가 여기서는 성립하지 않는다.
        IncrementalValuesProvider<RowModel> rows = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RowAttributeMetadataName,
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => Create(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(rows, static (spc, model) => Emit(spc, model));
    }

    // ── 선언 읽기 ────────────────────────────────────────────────────

    private static RowModel? Create(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || context.Attributes.Length == 0)
        {
            return null;
        }

        AttributeData attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not string tableName)
        {
            // 인자가 깨진 상태 — 컴파일러가 이미 오류를 냈다. 여기서 겹쳐 알리지 않는다.
            return null;
        }

        ImmutableArray<ColumnModel>.Builder columns = ImmutableArray.CreateBuilder<ColumnModel>();

        foreach (ISymbol member in symbol.GetMembers())
        {
            // 열은 "getter 만 있는 partial 인스턴스 속성" 이다. 그 밖의 멤버(도우미 메서드,
            // 계산 속성)는 사용자의 것이므로 건드리지 않는다.
            if (member is not IPropertySymbol property
                || property.IsStatic
                || !property.IsPartialDefinition
                || property.GetMethod is null)
            {
                continue;
            }

            columns.Add(ReadColumn(property));
        }

        return new RowModel(
            symbol.Name,
            symbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : symbol.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>(ContainingTypeChain(symbol)),
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            tableName,
            symbol.IsReadOnly,
            AllPartial(symbol),
            new EquatableArray<string>(
                ImmutableArray.CreateRange(symbol.GetMembers().Select(static m => m.Name).Distinct())),
            new EquatableArray<ColumnModel>(columns.ToImmutable()),
            LocationModel.From(context.TargetNode.GetLocation()));
    }

    private static ColumnModel ReadColumn(IPropertySymbol property)
    {
        AttributeData? column = property.GetAttributes().FirstOrDefault(
            static a => a.AttributeClass?.ToDisplayString() == ColumnAttributeMetadataName);

        string? name = null;
        bool key = false;
        bool optional = false;
        string? referencesTable = null;
        bool referenceTargetInvalid = false;
        long? minimumInteger = null;
        long? maximumInteger = null;
        double? minimumReal = null;
        double? maximumReal = null;

        if (column is not null)
        {
            foreach (KeyValuePair<string, TypedConstant> argument in column.NamedArguments)
            {
                switch (argument.Key)
                {
                    case "Name" when argument.Value.Value is string value:
                        name = value;
                        break;

                    case "Key" when argument.Value.Value is bool value:
                        key = value;
                        break;

                    case "Optional" when argument.Value.Value is bool value:
                        optional = value;
                        break;

                    case "References":
                        // 대상 표 이름은 대상 타입의 [StaticTableRow] 에서 읽는다 —
                        // 같은 이름을 두 군데 적지 않게 하는 것이 typeof 를 받는 이유다.
                        if (argument.Value.Value is INamedTypeSymbol target
                            && TableNameOf(target) is { } targetTable)
                        {
                            referencesTable = targetTable;
                        }
                        else
                        {
                            referenceTargetInvalid = true;
                        }

                        break;

                    // ⚠ 명명 인자의 "존재 여부" 가 곧 제약의 유무다. 어트리뷰트 인자는
                    // long? 이 될 수 없으므로 센티넬 값 대신 이 방법을 쓴다.
                    case "MinimumInteger" when argument.Value.Value is long value:
                        minimumInteger = value;
                        break;

                    case "MaximumInteger" when argument.Value.Value is long value:
                        maximumInteger = value;
                        break;

                    case "MinimumReal" when argument.Value.Value is double value:
                        minimumReal = value;
                        break;

                    case "MaximumReal" when argument.Value.Value is double value:
                        maximumReal = value;
                        break;

                    default:
                        break;
                }
            }
        }

        return new ColumnModel(
            property.Name,
            name ?? property.Name,
            MapType(property.Type),
            property.Type.ToDisplayString(DeclaredTypeFormat),
            property.Type.NullableAnnotation == NullableAnnotation.Annotated,
            property.Type.NullableAnnotation != NullableAnnotation.None,
            key,
            !optional,
            referencesTable,
            referenceTargetInvalid,
            minimumInteger,
            maximumInteger,
            minimumReal,
            maximumReal,
            LocationModel.From(property.Locations.Length > 0 ? property.Locations[0] : Location.None));
    }

    private static string? TableNameOf(INamedTypeSymbol type)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == RowAttributeMetadataName
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>지원하는 열 형식으로 대응시킨다. 대응이 없으면 <see langword="null"/>.</summary>
    private static string? MapType(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => "String",
        SpecialType.System_Int32 => "Int32",
        SpecialType.System_Int64 => "Int64",
        SpecialType.System_Double => "Double",
        SpecialType.System_Boolean => "Boolean",
        _ => null,
    };

    /// <summary>바깥 타입의 선언 머리를 바깥→안 순서로 모은다(예: <c>partial class Outer</c>).</summary>
    /// <remarks>
    /// <b>종류를 함께 담는 이유.</b> 행 타입은 struct 지만 그것을 감싸는 타입은 class·record 일
    /// 수 있다. 전부 <c>struct</c> 로 다시 열면 <b>원래 선언과 종류가 달라 컴파일이 깨진다</b>.
    /// </remarks>
    private static ImmutableArray<string> ContainingTypeChain(INamedTypeSymbol symbol)
    {
        List<string> chain = [];

        for (INamedTypeSymbol? outer = symbol.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            string kind = outer.TypeKind switch
            {
                TypeKind.Struct => outer.IsRecord ? "record struct" : "struct",
                TypeKind.Interface => "interface",
                _ => outer.IsRecord ? "record" : "class",
            };

            chain.Insert(0, $"partial {kind} {outer.Name}");
        }

        return ImmutableArray.CreateRange(chain);
    }

    /// <summary>행 타입과 모든 바깥 타입이 partial 인가.</summary>
    private static bool AllPartial(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            bool partial = false;

            foreach (SyntaxReference reference in current.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    partial = true;
                    break;
                }
            }

            if (!partial)
            {
                return false;
            }
        }

        return true;
    }

    // ── 검증과 생성 ──────────────────────────────────────────────────

    private static void Emit(SourceProductionContext context, RowModel model)
    {
        if (!Validate(context, model))
        {
            return;
        }

        context.AddSource($"{model.HintName}.StaticTableRow.g.cs", SourceText.From(Render(model), Encoding.UTF8));
    }

    private static bool Validate(SourceProductionContext context, RowModel model)
    {
        bool valid = true;

        if (!model.AllPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.NotPartial, model.Location.ToLocation(), model.TypeFqn));
            valid = false;
        }

        if (!model.IsReadOnly)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.NotReadOnlyStruct, model.Location.ToLocation(), model.TypeFqn));
            valid = false;
        }

        if (model.TableName.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.NameConflict, model.Location.ToLocation(),
                model.TypeFqn, "테이블 이름이 비어 있다."));
            valid = false;
        }

        if (model.Columns.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.NoColumns, model.Location.ToLocation(), model.TypeFqn));
            return false;
        }

        valid &= ValidateNames(context, model);
        valid &= ValidateKey(context, model);

        foreach (ColumnModel column in model.Columns)
        {
            valid &= ValidateColumn(context, column);
        }

        return valid;
    }

    private static bool ValidateNames(SourceProductionContext context, RowModel model)
    {
        bool valid = true;
        HashSet<string> columnNames = [];

        foreach (ColumnModel column in model.Columns)
        {
            if (!columnNames.Add(column.ColumnName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    StaticTableDiagnostics.NameConflict, column.Location.ToLocation(),
                    model.TypeFqn, $"열 이름 '{column.ColumnName}' 이 중복된다. CSV 헤더에서 어느 열인지 정해지지 않는다."));
                valid = false;
            }

            if (ReservedMemberNames.Contains(column.PropertyName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    StaticTableDiagnostics.NameConflict, column.Location.ToLocation(),
                    model.TypeFqn, $"속성 이름 '{column.PropertyName}' 은 생성 멤버와 충돌한다. [StaticTableColumn(Name = ...)] 로 CSV 열 이름만 맞추고 속성 이름을 바꾼다."));
                valid = false;
            }

            // 참조 열에는 {속성명}RowIndex 가 함께 생성된다 — 그 이름이 이미 쓰이면 충돌한다.
            if (column.ReferencesTable is not null
                && model.DeclaredMemberNames.Contains(column.PropertyName + "RowIndex"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    StaticTableDiagnostics.NameConflict, column.Location.ToLocation(),
                    model.TypeFqn, $"참조 열 '{column.PropertyName}' 이 생성할 '{column.PropertyName}RowIndex' 가 이미 선언돼 있다."));
                valid = false;
            }
        }

        return valid;
    }

    private static bool ValidateKey(SourceProductionContext context, RowModel model)
    {
        List<ColumnModel> keys = [.. model.Columns.Where(static c => c.IsKey)];

        if (keys.Count != 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidKeyColumn, model.Location.ToLocation(), model.TypeFqn,
                $"{keys.Count}개다. [StaticTableColumn(Key = true)] 를 정확히 하나에 붙인다 — 키 없는 표는 순차 훑기밖에 못 하고, 키가 둘이면 어느 것으로 찾을지 정해지지 않는다."));
            return false;
        }

        if (!keys[0].Required)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidKeyColumn, keys[0].Location.ToLocation(), model.TypeFqn,
                $"'{keys[0].PropertyName}' 이 선택(Optional)이다. 키 칸이 비면 그 행은 키 사전에 들어가지 않아 로딩은 성공하는데 영원히 찾히지 않는다."));
            return false;
        }

        return true;
    }

    private static bool ValidateColumn(SourceProductionContext context, ColumnModel column)
    {
        if (column.ColumnType is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.UnsupportedColumnType, column.Location.ToLocation(),
                column.PropertyName, column.DeclaredTypeFqn));
            return false;
        }

        bool valid = true;
        bool isString = column.ColumnType == "String";
        bool isInteger = column.ColumnType is "Int32" or "Int64";
        bool isReal = column.ColumnType == "Double";

        if (isString && !column.Required && column.NullableContextEnabled && !column.IsNullableAnnotated)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.OptionalStringMustBeNullable, column.Location.ToLocation(),
                column.PropertyName));
            valid = false;
        }

        if ((column.MinimumInteger is not null || column.MaximumInteger is not null) && !isInteger)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidRange, column.Location.ToLocation(), column.PropertyName,
                $"정수 범위는 int·long 열에만 걸 수 있다(이 열은 {column.ColumnType}). 조용히 무시되는 제약은 걸지 않은 것보다 나쁘다."));
            valid = false;
        }

        if ((column.MinimumReal is not null || column.MaximumReal is not null) && !isReal)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidRange, column.Location.ToLocation(), column.PropertyName,
                $"실수 범위는 double 열에만 걸 수 있다(이 열은 {column.ColumnType}). 정수 열에는 MinimumInteger/MaximumInteger 를 쓴다."));
            valid = false;
        }

        if (column.MinimumInteger is { } minInteger && column.MaximumInteger is { } maxInteger
            && minInteger > maxInteger)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidRange, column.Location.ToLocation(), column.PropertyName,
                $"범위가 뒤집혔다: [{minInteger}, {maxInteger}]. 통과할 수 있는 값이 없다."));
            valid = false;
        }

        if (column.MinimumReal is { } minReal && column.MaximumReal is { } maxReal && minReal > maxReal)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidRange, column.Location.ToLocation(), column.PropertyName,
                $"범위가 뒤집혔다: [{Literal(minReal)}, {Literal(maxReal)}]. 통과할 수 있는 값이 없다."));
            valid = false;
        }

        if (column.ReferenceTargetInvalid)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidReference, column.Location.ToLocation(), column.PropertyName,
                "References 에 준 타입에 [StaticTableRow] 가 없다. 참조 대상은 행 타입이어야 대상 표 이름을 읽을 수 있다."));
            valid = false;
        }
        else if (column.ReferencesTable is not null && !isString)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StaticTableDiagnostics.InvalidReference, column.Location.ToLocation(), column.PropertyName,
                $"참조 열은 string 이어야 한다(대상의 키가 문자열로 대조된다). 이 열은 {column.ColumnType}."));
            valid = false;
        }

        return valid;
    }

    // ── 코드 생성 ────────────────────────────────────────────────────

    private static string Render(RowModel model)
    {
        StringBuilder source = new();
        string indent = string.Empty;

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("// ChServerM.SourceGen 이 [StaticTableRow] 선언에서 생성했다. 직접 수정하지 않는다.");
        source.AppendLine("#nullable enable");
        source.AppendLine();

        if (model.Namespace is not null)
        {
            source.AppendLine($"namespace {model.Namespace}");
            source.AppendLine("{");
            indent += "    ";
        }

        foreach (string outer in model.ContainingTypes)
        {
            source.AppendLine($"{indent}{outer}");
            source.AppendLine($"{indent}{{");
            indent += "    ";
        }

        RenderRow(source, indent, model);

        for (int i = 0; i < model.ContainingTypes.Count; i++)
        {
            indent = indent.Substring(4);
            source.AppendLine($"{indent}}}");
        }

        if (model.Namespace is not null)
        {
            source.AppendLine("}");
        }

        return source.ToString();
    }

    private static void RenderRow(StringBuilder source, string indent, RowModel model)
    {
        string body = indent + "    ";

        // IEquatable 을 생성 쪽에서 붙인다. 선언 쪽에 요구하면 사용자가 매번 적어야 하고,
        // 붙이지 않으면 값 타입 규칙(CA1815)이 **사용자 파일에** 경고를 낸다 —
        // 생성기가 만든 타입의 비용을 사용자가 치르게 하지 않는다.
        source.AppendLine($"{indent}readonly partial struct {model.TypeName} : global::System.IEquatable<{model.TypeName}>");
        source.AppendLine($"{indent}{{");
        source.AppendLine($"{body}private readonly {Ns}.StaticTable _table;");
        source.AppendLine($"{body}private readonly int _row;");
        source.AppendLine();
        source.AppendLine($"{body}/// <summary>표와 행 번호로 행을 만든다. 뷰(<see cref=\"Table\"/>)만 부른다.</summary>");
        source.AppendLine($"{body}internal {model.TypeName}({Ns}.StaticTable table, int row)");
        source.AppendLine($"{body}{{");
        source.AppendLine($"{body}    _table = table;");
        source.AppendLine($"{body}    _row = row;");
        source.AppendLine($"{body}}}");
        source.AppendLine();
        source.AppendLine($"{body}/// <summary>이 행 타입이 선언한 표 이름.</summary>");
        source.AppendLine($"{body}public const string TableName = {Literal(model.TableName)};");
        source.AppendLine();
        source.AppendLine($"{body}/// <summary>선언에서 생성된 스키마. <b>로딩에 이 인스턴스를 쓴다</b>.</summary>");
        source.AppendLine($"{body}/// <remarks>뷰가 참조 동일성으로 확인하므로, 구조만 같은 다른 스키마로 로딩한 표는 거부된다.</remarks>");
        source.AppendLine($"{body}public static {Ns}.StaticTableSchema Schema => SchemaHolder.Value;");
        source.AppendLine();
        source.AppendLine($"{body}/// <summary>이 행의 행 번호.</summary>");
        source.AppendLine($"{body}public int RowIndex => _row;");

        RenderEquality(source, body, model);
        RenderColumns(source, body, model);
        RenderSchemaHolder(source, body, model);
        RenderView(source, body, model);

        source.AppendLine($"{indent}}}");
    }

    /// <summary>행의 동등성 — <b>같은 표의 같은 행</b>인가.</summary>
    /// <remarks>
    /// 값을 비교하지 않는다. 행은 표를 가리키는 좌표이고, 값 비교는 열이 늘어날수록 비싸지는
    /// 데다 "다른 표의 우연히 같은 행" 을 같다고 말하게 된다.
    /// </remarks>
    private static void RenderEquality(StringBuilder source, string body, RowModel model)
    {
        string row = model.TypeName;

        source.AppendLine();
        source.AppendLine($"{body}/// <summary>같은 표의 같은 행인가. <b>값이 아니라 좌표를 비교한다</b>.</summary>");
        source.AppendLine($"{body}/// <param name=\"other\">비교 대상.</param>");
        source.AppendLine($"{body}/// <returns>같은 표의 같은 행이면 true.</returns>");
        source.AppendLine($"{body}public bool Equals({row} other)");
        source.AppendLine($"{body}    => object.ReferenceEquals(_table, other._table) && _row == other._row;");
        source.AppendLine();
        source.AppendLine($"{body}/// <inheritdoc/>");
        source.AppendLine($"{body}public override bool Equals(object? obj) => obj is {row} other && Equals(other);");
        source.AppendLine();
        source.AppendLine($"{body}/// <inheritdoc/>");
        source.AppendLine($"{body}public override int GetHashCode()");
        source.AppendLine($"{body}    => global::System.HashCode.Combine(");
        source.AppendLine($"{body}        global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_table), _row);");
        source.AppendLine();
        source.AppendLine($"{body}/// <summary>두 행이 같은지 비교한다.</summary>");
        source.AppendLine($"{body}/// <param name=\"left\">왼쪽 값.</param>");
        source.AppendLine($"{body}/// <param name=\"right\">오른쪽 값.</param>");
        source.AppendLine($"{body}/// <returns>같으면 true.</returns>");
        source.AppendLine($"{body}public static bool operator ==({row} left, {row} right) => left.Equals(right);");
        source.AppendLine();
        source.AppendLine($"{body}/// <summary>두 행이 다른지 비교한다.</summary>");
        source.AppendLine($"{body}/// <param name=\"left\">왼쪽 값.</param>");
        source.AppendLine($"{body}/// <param name=\"right\">오른쪽 값.</param>");
        source.AppendLine($"{body}/// <returns>다르면 true.</returns>");
        source.AppendLine($"{body}public static bool operator !=({row} left, {row} right) => !left.Equals(right);");
    }

    private static void RenderColumns(StringBuilder source, string body, RowModel model)
    {
        for (int ordinal = 0; ordinal < model.Columns.Count; ordinal++)
        {
            ColumnModel column = model.Columns[ordinal];

            source.AppendLine();
            source.AppendLine($"{body}/// <summary>열 '{column.ColumnName}' ({column.ColumnType}, 서수 {ordinal}).</summary>");
            source.AppendLine($"{body}public partial {column.DeclaredTypeFqn} {column.PropertyName}");
            source.AppendLine($"{body}{{");
            source.AppendLine($"{body}    get {{ return {Read(column, ordinal)}; }}");
            source.AppendLine($"{body}}}");

            if (column.ReferencesTable is null)
            {
                continue;
            }

            source.AppendLine();
            source.AppendLine($"{body}/// <summary>'{column.PropertyName}' 이 가리키는 '{column.ReferencesTable}' 의 <b>행 번호</b>. 비었으면 <c>-1</c>.</summary>");
            source.AppendLine($"{body}/// <remarks>키로 다시 찾지 않는다 — 로딩 때 검증과 같은 패스에서 이미 행 번호로 바꿔 뒀다.</remarks>");
            source.AppendLine($"{body}public int {column.PropertyName}RowIndex");
            source.AppendLine($"{body}{{");
            source.AppendLine($"{body}    get {{ return _table.GetReference(_row, {ordinal.ToString(CultureInfo.InvariantCulture)}); }}");
            source.AppendLine($"{body}}}");
        }
    }

    /// <summary>열 하나를 읽는 식. 서수는 컴파일 타임 상수다.</summary>
    private static string Read(ColumnModel column, int ordinal)
    {
        string index = ordinal.ToString(CultureInfo.InvariantCulture);

        return column.ColumnType switch
        {
            // 필수 문자열 열은 로딩이 빈 값을 이미 거부했으므로 null 이 아니다.
            "String" => column.Required
                ? $"_table.GetString(_row, {index})!"
                : $"_table.GetString(_row, {index})",
            "Int32" => $"_table.GetInt32(_row, {index})",
            "Int64" => $"_table.GetInt64(_row, {index})",
            "Double" => $"_table.GetDouble(_row, {index})",
            _ => $"_table.GetBoolean(_row, {index})",
        };
    }

    private static void RenderSchemaHolder(StringBuilder source, string body, RowModel model)
    {
        string inner = body + "    ";
        ColumnModel key = model.Columns.First(static c => c.IsKey);

        source.AppendLine();
        source.AppendLine($"{body}/// <summary>스키마를 한 번만 만든다. 정적 초기화는 상수만 읽으므로 초기화 순서 의존이 없다.</summary>");
        source.AppendLine($"{body}private static class SchemaHolder");
        source.AppendLine($"{body}{{");
        source.AppendLine($"{inner}internal static readonly {Ns}.StaticTableSchema Value = Create();");
        source.AppendLine();
        source.AppendLine($"{inner}private static {Ns}.StaticTableSchema Create()");
        source.AppendLine($"{inner}{{");
        source.AppendLine($"{inner}    {Ns}.StaticTableColumn[] columns = new {Ns}.StaticTableColumn[{model.Columns.Count.ToString(CultureInfo.InvariantCulture)}];");

        for (int ordinal = 0; ordinal < model.Columns.Count; ordinal++)
        {
            ColumnModel column = model.Columns[ordinal];
            List<string> initializers = [];

            if (column.ReferencesTable is not null)
            {
                initializers.Add($"ReferencesTable = {Literal(column.ReferencesTable)}");
            }

            if (column.MinimumInteger is { } minInteger)
            {
                initializers.Add($"MinimumInteger = {minInteger.ToString(CultureInfo.InvariantCulture)}L");
            }

            if (column.MaximumInteger is { } maxInteger)
            {
                initializers.Add($"MaximumInteger = {maxInteger.ToString(CultureInfo.InvariantCulture)}L");
            }

            if (column.MinimumReal is { } minReal)
            {
                initializers.Add($"MinimumReal = {Literal(minReal)}");
            }

            if (column.MaximumReal is { } maxReal)
            {
                initializers.Add($"MaximumReal = {Literal(maxReal)}");
            }

            string suffix = initializers.Count == 0 ? ";" : $" {{ {string.Join(", ", initializers)} }};";

            source.AppendLine(
                $"{inner}    columns[{ordinal.ToString(CultureInfo.InvariantCulture)}] = new {Ns}.StaticTableColumn("
                + $"{Literal(column.ColumnName)}, {Ns}.StaticTableColumnType.{column.ColumnType}, "
                + $"{(column.Required ? "true" : "false")}){suffix}");
        }

        source.AppendLine();
        source.AppendLine($"{inner}    return new {Ns}.StaticTableSchema({Literal(model.TableName)}, {Literal(key.ColumnName)}, columns);");
        source.AppendLine($"{inner}}}");
        source.AppendLine($"{body}}}");
    }

    private static void RenderView(StringBuilder source, string body, RowModel model)
    {
        string inner = body + "    ";
        string deep = inner + "    ";
        string row = model.TypeName;

        source.AppendLine();
        source.AppendLine($"{body}/// <summary>'{model.TableName}' 표의 강타입 뷰 — <b>서수도 문자열 열 이름도 노출하지 않는다</b>.</summary>");
        source.AppendLine($"{body}/// <remarks>");
        source.AppendLine($"{body}/// <para><b>스레드 규약.</b> 바탕 표가 불변이므로 여러 스레드가 동시에 읽어도 안전하다.</para>");
        source.AppendLine($"{body}/// <para><b>⚠ <c>default</c> 는 유효하지 않다.</b> 반드시 생성자로 만든다.</para>");
        source.AppendLine($"{body}/// </remarks>");
        source.AppendLine($"{body}public readonly struct Table");
        source.AppendLine($"{body}{{");
        source.AppendLine($"{inner}private readonly {Ns}.StaticTable _table;");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>묶음에서 '{model.TableName}' 표를 찾아 묶는다.</summary>");
        source.AppendLine($"{inner}/// <param name=\"set\">로딩이 끝난 묶음.</param>");
        source.AppendLine($"{inner}/// <exception cref=\"global::System.ArgumentNullException\"><paramref name=\"set\"/> 가 null 이다.</exception>");
        source.AppendLine($"{inner}/// <exception cref=\"global::System.InvalidOperationException\">표가 없거나 다른 스키마로 로딩됐다.</exception>");
        source.AppendLine($"{inner}public Table({Ns}.StaticTableSet set)");
        source.AppendLine($"{inner}{{");
        source.AppendLine($"{inner}    if (set is null) {{ throw new global::System.ArgumentNullException(nameof(set)); }}");
        source.AppendLine();
        source.AppendLine($"{inner}    if (!set.TryGetTable(TableName, out {Ns}.StaticTable? found) || found is null)");
        source.AppendLine($"{inner}    {{");
        source.AppendLine($"{inner}        throw new global::System.InvalidOperationException(");
        source.AppendLine($"{inner}            \"묶음에 표 '\" + TableName + \"' 이 없다. {row}.Schema 로 로딩했는지 확인한다.\");");
        source.AppendLine($"{inner}    }}");
        source.AppendLine();
        source.AppendLine($"{inner}    _table = Verify(found);");
        source.AppendLine($"{inner}}}");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>이미 얻은 표를 묶는다.</summary>");
        source.AppendLine($"{inner}/// <param name=\"table\">이 행 타입의 스키마로 로딩된 표.</param>");
        source.AppendLine($"{inner}/// <exception cref=\"global::System.ArgumentNullException\"><paramref name=\"table\"/> 가 null 이다.</exception>");
        source.AppendLine($"{inner}/// <exception cref=\"global::System.InvalidOperationException\">다른 스키마로 로딩됐다.</exception>");
        source.AppendLine($"{inner}public Table({Ns}.StaticTable table)");
        source.AppendLine($"{inner}{{");
        source.AppendLine($"{inner}    _table = Verify(table);");
        source.AppendLine($"{inner}}}");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>바탕 표. 서수 기반 API 가 필요할 때만 쓴다.</summary>");
        source.AppendLine($"{inner}public {Ns}.StaticTable Source => _table;");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>행 수.</summary>");
        source.AppendLine($"{inner}public int Count => _table.RowCount;");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>행 번호로 행을 얻는다.</summary>");
        source.AppendLine($"{inner}/// <param name=\"row\">행 번호.</param>");
        source.AppendLine($"{inner}public {row} this[int row] => new {row}(_table, row);");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>키로 행을 찾는다.</summary>");
        source.AppendLine($"{inner}/// <param name=\"key\">키 열의 <b>원문</b>. CSV 에 적힌 문자열 그대로 대조한다.</param>");
        source.AppendLine($"{inner}/// <param name=\"row\">찾은 행.</param>");
        source.AppendLine($"{inner}/// <returns>찾았으면 true.</returns>");
        source.AppendLine($"{inner}public bool TryGetRow(string key, out {row} row)");
        source.AppendLine($"{inner}{{");
        source.AppendLine($"{inner}    if (_table.TryGetRow(key, out int index))");
        source.AppendLine($"{inner}    {{");
        source.AppendLine($"{inner}        row = new {row}(_table, index);");
        source.AppendLine($"{inner}        return true;");
        source.AppendLine($"{inner}    }}");
        source.AppendLine();
        source.AppendLine($"{inner}    row = default;");
        source.AppendLine($"{inner}    return false;");
        source.AppendLine($"{inner}}}");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>모든 행을 순서대로 훑는다. 열거자가 struct 라 foreach 에 할당이 없다.</summary>");
        source.AppendLine($"{inner}/// <returns>열거자.</returns>");
        source.AppendLine($"{inner}public Enumerator GetEnumerator() => new Enumerator(_table);");
        source.AppendLine();
        source.AppendLine($"{inner}private static {Ns}.StaticTable Verify({Ns}.StaticTable table)");
        source.AppendLine($"{inner}{{");
        source.AppendLine($"{inner}    if (table is null) {{ throw new global::System.ArgumentNullException(nameof(table)); }}");
        source.AppendLine();
        source.AppendLine($"{inner}    // ⚠ 참조 동일성이다. 구조만 같은 다른 스키마를 받아들이면 서수 일치 보장이 사라진다.");
        source.AppendLine($"{inner}    if (!object.ReferenceEquals(table.Schema, Schema))");
        source.AppendLine($"{inner}    {{");
        source.AppendLine($"{inner}        throw new global::System.InvalidOperationException(");
        source.AppendLine($"{inner}            \"표 '\" + table.Schema.Name + \"' 이 {row}.Schema 로 로딩되지 않았다. 로딩할 때 {row}.Schema 를 넘긴다.\");");
        source.AppendLine($"{inner}    }}");
        source.AppendLine();
        source.AppendLine($"{inner}    return table;");
        source.AppendLine($"{inner}}}");
        source.AppendLine();
        source.AppendLine($"{inner}/// <summary>행 열거자. 할당하지 않는다.</summary>");
        source.AppendLine($"{inner}public struct Enumerator");
        source.AppendLine($"{inner}{{");
        source.AppendLine($"{deep}private readonly {Ns}.StaticTable _table;");
        source.AppendLine($"{deep}private int _index;");
        source.AppendLine();
        source.AppendLine($"{deep}internal Enumerator({Ns}.StaticTable table)");
        source.AppendLine($"{deep}{{");
        source.AppendLine($"{deep}    _table = table;");
        source.AppendLine($"{deep}    _index = -1;");
        source.AppendLine($"{deep}}}");
        source.AppendLine();
        source.AppendLine($"{deep}/// <summary>현재 행.</summary>");
        source.AppendLine($"{deep}public readonly {row} Current => new {row}(_table, _index);");
        source.AppendLine();
        source.AppendLine($"{deep}/// <summary>다음 행으로 옮긴다.</summary>");
        source.AppendLine($"{deep}/// <returns>행이 남아 있으면 true.</returns>");
        source.AppendLine($"{deep}public bool MoveNext() => ++_index < _table.RowCount;");
        source.AppendLine($"{inner}}}");
        source.AppendLine($"{body}}}");
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    /// <summary>왕복 가능한 double 리터럴. 비유한값은 이름 상수로 적는다.</summary>
    private static string Literal(double value)
    {
        if (double.IsNaN(value))
        {
            return "global::System.Double.NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "global::System.Double.PositiveInfinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "global::System.Double.NegativeInfinity";
        }

        return value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }

    // ── 모델 ─────────────────────────────────────────────────────────

    /// <summary>행 타입 하나의 선언 결과. 값 동등성이 증분 캐시의 전제라 record 다.</summary>
    private sealed record RowModel(
        string TypeName,
        string? Namespace,
        EquatableArray<string> ContainingTypes,
        string TypeFqn,
        string TableName,
        bool IsReadOnly,
        bool AllPartial,
        EquatableArray<string> DeclaredMemberNames,
        EquatableArray<ColumnModel> Columns,
        LocationModel Location)
    {
        /// <summary>생성 파일 이름의 바탕. 어셈블리 안에서 유일해야 한다.</summary>
        public string HintName => TypeFqn.Replace("global::", string.Empty).Replace('<', '_').Replace('>', '_');
    }

    /// <summary>열 하나의 선언 결과.</summary>
    private sealed record ColumnModel(
        string PropertyName,
        string ColumnName,
        string? ColumnType,
        string DeclaredTypeFqn,
        bool IsNullableAnnotated,
        bool NullableContextEnabled,
        bool IsKey,
        bool Required,
        string? ReferencesTable,
        bool ReferenceTargetInvalid,
        long? MinimumInteger,
        long? MaximumInteger,
        double? MinimumReal,
        double? MaximumReal,
        LocationModel Location);
}
