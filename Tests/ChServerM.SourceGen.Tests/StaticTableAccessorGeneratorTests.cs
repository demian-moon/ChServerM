using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ChServerM.SourceGen.Tests;

/// <summary>
/// <see cref="StaticTableAccessorGenerator"/> 의 드라이버 테스트 — 진단(CHSM2xxx)과 생성 코드.
/// </summary>
/// <remarks>
/// <para>
/// <b>스냅샷 규약.</b> <see cref="Snapshot_GeneratedSource_IsStable"/> 이 생성 코드 전문을
/// 고정한다. 생성 코드를 의도적으로 바꿨다면 이 테스트의 기대 문자열을 함께 바꿔야 하고,
/// 그 diff 가 리뷰에 노출된다 — PublicAPI 승인 파일과 같은 목적이다.
/// </para>
/// <para>
/// <b>여기서 보지 않는 것.</b> 생성된 코드가 <b>옳은 값을 읽는지</b>는 보지 않는다. 서수를
/// 하나 밀어서 생성해도 여기서는 통과한다 — 그것을 잡는 것은
/// <c>ChServerM.DataTable.Tests</c> 의 종단 테스트다. 두 층이 서로를 대신하지 못한다.
/// </para>
/// <para>줄바꿈은 비교 전에 LF 로 정규화한다 — 생성기는 실행 OS 의 개행을 쓴다.</para>
/// </remarks>
public sealed class StaticTableAccessorGeneratorTests
{
    private const string ValidSource = """
        using ChServerM.DataTable;

        namespace TestApp;

        [StaticTableRow("Recipe")]
        public readonly partial struct RecipeRow
        {
            [StaticTableColumn(Name = "id", Key = true)]
            public partial string Id { get; }

            [StaticTableColumn(Name = "cost")]
            public partial long Cost { get; }
        }
        """;

    [Fact]
    public void ValidDeclaration_NoDiagnostics_OutputCompiles()
    {
        (GeneratorRunResult result, Compilation output) = Run(ValidSource);

        Assert.Empty(result.Diagnostics);
        Assert.Single(result.GeneratedSources);

        // 생성 코드까지 포함한 전체 컴파일레이션이 오류 없이 컴파일돼야 한다 —
        // 부분 속성 시그니처가 선언과 정확히 일치한다는 가장 강한 검증이다.
        Diagnostic[] errors = [.. output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.Empty(errors);
    }

    [Fact]
    public void NullableAnnotation_IsPreserved_OnOptionalStringColumn()
    {
        const string Source = """
            using ChServerM.DataTable;

            namespace TestApp;

            [StaticTableRow("Item")]
            public readonly partial struct ItemRow
            {
                [StaticTableColumn(Key = true)]
                public partial string Id { get; }

                [StaticTableColumn(Optional = true)]
                public partial string? Note { get; }
            }
            """;

        (GeneratorRunResult result, Compilation output) = Run(Source);

        Assert.Empty(result.Diagnostics);

        // ⚠ FullyQualifiedFormat 은 '?' 를 뺀다. 그대로 썼다면 여기서 시그니처가 어긋난다.
        Assert.Contains("public partial string? Note", Normalize(result.GeneratedSources[0].SourceText.ToString()), StringComparison.Ordinal);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void ReferenceColumn_EmitsResolvedRowIndexAccessor()
    {
        (GeneratorRunResult result, Compilation output) = Run(ReferenceSource);
        string item = result.GeneratedSources
            .Single(s => s.HintName.Contains("ItemRow", StringComparison.Ordinal))
            .SourceText.ToString();

        Assert.Empty(result.Diagnostics);
        Assert.Contains("public int RecipeIdRowIndex", Normalize(item), StringComparison.Ordinal);

        // 대상 표 이름은 대상 타입의 [StaticTableRow] 에서 읽어 온다 — 두 군데 적지 않는다.
        Assert.Contains("ReferencesTable = \"Recipe\"", Normalize(item), StringComparison.Ordinal);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void NestedRowType_ReopensContainingTypesWithTheirOwnKind()
    {
        const string Source = """
            using ChServerM.DataTable;

            namespace TestApp;

            public static partial class Tables
            {
                [StaticTableRow("Item")]
                public readonly partial struct ItemRow
                {
                    [StaticTableColumn(Key = true)]
                    public partial string Id { get; }
                }
            }
            """;

        (GeneratorRunResult result, Compilation output) = Run(Source);

        Assert.Empty(result.Diagnostics);

        // 바깥이 class 인데 struct 로 다시 열면 컴파일이 깨진다.
        Assert.Contains("partial class Tables", Normalize(result.GeneratedSources[0].SourceText.ToString()), StringComparison.Ordinal);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ── 진단 ─────────────────────────────────────────────────────────

    [Fact]
    public void NotPartial_ReportsCHSM2001()
    {
        const string Source = """
            using ChServerM.DataTable;

            namespace TestApp;

            [StaticTableRow("Item")]
            public readonly struct ItemRow
            {
                [StaticTableColumn(Key = true)]
                public partial string Id { get; }
            }
            """;

        AssertDiagnostic(Source, "CHSM2001");
    }

    [Fact]
    public void NotReadOnly_ReportsCHSM2010()
    {
        const string Source = """
            using ChServerM.DataTable;

            namespace TestApp;

            [StaticTableRow("Item")]
            public partial struct ItemRow
            {
                [StaticTableColumn(Key = true)]
                public partial string Id { get; }
            }
            """;

        AssertDiagnostic(Source, "CHSM2010");
    }

    [Fact]
    public void NoKey_ReportsCHSM2002()
    {
        AssertDiagnostic(Row("public partial string Id { get; }"), "CHSM2002");
    }

    [Fact]
    public void TwoKeys_ReportsCHSM2002()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(Key = true)]
                    public partial string Other { get; }
                """),
            "CHSM2002");
    }

    [Fact]
    public void OptionalKey_ReportsCHSM2002()
    {
        // 키 칸이 비면 그 행은 키 사전에 들어가지 않는다 — 로딩은 성공하는데 영원히 안 찾힌다.
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true, Optional = true)]
                    public partial string? Id { get; }
                """),
            "CHSM2002");
    }

    [Fact]
    public void NoColumns_ReportsCHSM2003()
    {
        const string Source = """
            using ChServerM.DataTable;

            namespace TestApp;

            [StaticTableRow("Item")]
            public readonly partial struct ItemRow
            {
            }
            """;

        AssertDiagnostic(Source, "CHSM2003");
    }

    [Fact]
    public void UnsupportedColumnType_ReportsCHSM2004()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true)]
                    public partial string Id { get; }

                    public partial decimal Price { get; }
                """),
            "CHSM2004");
    }

    [Fact]
    public void DuplicateColumnName_ReportsCHSM2005()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Name = "id", Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(Name = "id")]
                    public partial string Alias { get; }
                """),
            "CHSM2005");
    }

    [Fact]
    public void PropertyNameCollidingWithGeneratedMember_ReportsCHSM2005()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Name = "id", Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(Name = "schema")]
                    public partial string Schema { get; }
                """),
            "CHSM2005");
    }

    [Fact]
    public void OptionalStringDeclaredNonNullable_ReportsCHSM2006()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(Optional = true)]
                    public partial string Note { get; }
                """),
            "CHSM2006");
    }

    [Fact]
    public void IntegerRangeOnRealColumn_ReportsCHSM2007()
    {
        // 조용히 무시되는 제약이 가장 위험하다 — 작성자는 걸었다고 믿는다.
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(MinimumInteger = 0)]
                    public partial double Rate { get; }
                """),
            "CHSM2007");
    }

    [Fact]
    public void ReversedRange_ReportsCHSM2007()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(MinimumInteger = 10, MaximumInteger = 1)]
                    public partial int Damage { get; }
                """),
            "CHSM2007");
    }

    [Fact]
    public void ReferenceToNonRowType_ReportsCHSM2008()
    {
        AssertDiagnostic(
            Row("""
                [StaticTableColumn(Key = true)]
                    public partial string Id { get; }

                    [StaticTableColumn(References = typeof(string))]
                    public partial string RecipeId { get; }
                """),
            "CHSM2008");
    }

    [Fact]
    public void ReferenceOnNonStringColumn_ReportsCHSM2008()
    {
        AssertDiagnostic(ReferenceSource.Replace("public partial string? RecipeId", "public partial int RecipeId", StringComparison.Ordinal), "CHSM2008");
    }

    [Fact]
    public void AnyError_SuppressesGeneration()
    {
        (GeneratorRunResult result, _) = Run(Row("public partial string Id { get; }"));

        // 반쯤 맞는 접근자를 내보내면 진단보다 컴파일 오류가 먼저 눈에 들어와 원인이 가려진다.
        Assert.Empty(result.GeneratedSources);
    }

    // ── 스냅샷 ───────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_GeneratedSource_IsStable()
    {
        (GeneratorRunResult result, _) = Run(ValidSource);

        string generated = Normalize(result.GeneratedSources[0].SourceText.ToString());

        const string Expected = """
            // <auto-generated/>
            // ChServerM.SourceGen 이 [StaticTableRow] 선언에서 생성했다. 직접 수정하지 않는다.
            #nullable enable

            namespace TestApp
            {
                readonly partial struct RecipeRow : global::System.IEquatable<RecipeRow>
                {
                    private readonly global::ChServerM.DataTable.StaticTable _table;
                    private readonly int _row;

                    /// <summary>표와 행 번호로 행을 만든다. 뷰(<see cref="Table"/>)만 부른다.</summary>
                    internal RecipeRow(global::ChServerM.DataTable.StaticTable table, int row)
                    {
                        _table = table;
                        _row = row;
                    }

                    /// <summary>이 행 타입이 선언한 표 이름.</summary>
                    public const string TableName = "Recipe";

                    /// <summary>선언에서 생성된 스키마. <b>로딩에 이 인스턴스를 쓴다</b>.</summary>
                    /// <remarks>뷰가 참조 동일성으로 확인하므로, 구조만 같은 다른 스키마로 로딩한 표는 거부된다.</remarks>
                    public static global::ChServerM.DataTable.StaticTableSchema Schema => SchemaHolder.Value;

                    /// <summary>이 행의 행 번호.</summary>
                    public int RowIndex => _row;

                    /// <summary>같은 표의 같은 행인가. <b>값이 아니라 좌표를 비교한다</b>.</summary>
                    /// <param name="other">비교 대상.</param>
                    /// <returns>같은 표의 같은 행이면 true.</returns>
                    public bool Equals(RecipeRow other)
                        => object.ReferenceEquals(_table, other._table) && _row == other._row;

                    /// <inheritdoc/>
                    public override bool Equals(object? obj) => obj is RecipeRow other && Equals(other);

                    /// <inheritdoc/>
                    public override int GetHashCode()
                        => global::System.HashCode.Combine(
                            global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_table), _row);

                    /// <summary>두 행이 같은지 비교한다.</summary>
                    /// <param name="left">왼쪽 값.</param>
                    /// <param name="right">오른쪽 값.</param>
                    /// <returns>같으면 true.</returns>
                    public static bool operator ==(RecipeRow left, RecipeRow right) => left.Equals(right);

                    /// <summary>두 행이 다른지 비교한다.</summary>
                    /// <param name="left">왼쪽 값.</param>
                    /// <param name="right">오른쪽 값.</param>
                    /// <returns>다르면 true.</returns>
                    public static bool operator !=(RecipeRow left, RecipeRow right) => !left.Equals(right);

                    /// <summary>열 'id' (String, 서수 0).</summary>
                    public partial string Id
                    {
                        get { return _table.GetString(_row, 0)!; }
                    }

                    /// <summary>열 'cost' (Int64, 서수 1).</summary>
                    public partial long Cost
                    {
                        get { return _table.GetInt64(_row, 1); }
                    }

                    /// <summary>스키마를 한 번만 만든다. 정적 초기화는 상수만 읽으므로 초기화 순서 의존이 없다.</summary>
                    private static class SchemaHolder
                    {
                        internal static readonly global::ChServerM.DataTable.StaticTableSchema Value = Create();

                        private static global::ChServerM.DataTable.StaticTableSchema Create()
                        {
                            global::ChServerM.DataTable.StaticTableColumn[] columns = new global::ChServerM.DataTable.StaticTableColumn[2];
                            columns[0] = new global::ChServerM.DataTable.StaticTableColumn("id", global::ChServerM.DataTable.StaticTableColumnType.String, true);
                            columns[1] = new global::ChServerM.DataTable.StaticTableColumn("cost", global::ChServerM.DataTable.StaticTableColumnType.Int64, true);

                            return new global::ChServerM.DataTable.StaticTableSchema("Recipe", "id", columns);
                        }
                    }

                    /// <summary>'Recipe' 표의 강타입 뷰 — <b>서수도 문자열 열 이름도 노출하지 않는다</b>.</summary>
                    /// <remarks>
                    /// <para><b>스레드 규약.</b> 바탕 표가 불변이므로 여러 스레드가 동시에 읽어도 안전하다.</para>
                    /// <para><b>⚠ <c>default</c> 는 유효하지 않다.</b> 반드시 생성자로 만든다.</para>
                    /// </remarks>
                    public readonly struct Table
                    {
                        private readonly global::ChServerM.DataTable.StaticTable _table;

                        /// <summary>묶음에서 'Recipe' 표를 찾아 묶는다.</summary>
                        /// <param name="set">로딩이 끝난 묶음.</param>
                        /// <exception cref="global::System.ArgumentNullException"><paramref name="set"/> 가 null 이다.</exception>
                        /// <exception cref="global::System.InvalidOperationException">표가 없거나 다른 스키마로 로딩됐다.</exception>
                        public Table(global::ChServerM.DataTable.StaticTableSet set)
                        {
                            if (set is null) { throw new global::System.ArgumentNullException(nameof(set)); }

                            if (!set.TryGetTable(TableName, out global::ChServerM.DataTable.StaticTable? found) || found is null)
                            {
                                throw new global::System.InvalidOperationException(
                                    "묶음에 표 '" + TableName + "' 이 없다. RecipeRow.Schema 로 로딩했는지 확인한다.");
                            }

                            _table = Verify(found);
                        }

                        /// <summary>이미 얻은 표를 묶는다.</summary>
                        /// <param name="table">이 행 타입의 스키마로 로딩된 표.</param>
                        /// <exception cref="global::System.ArgumentNullException"><paramref name="table"/> 가 null 이다.</exception>
                        /// <exception cref="global::System.InvalidOperationException">다른 스키마로 로딩됐다.</exception>
                        public Table(global::ChServerM.DataTable.StaticTable table)
                        {
                            _table = Verify(table);
                        }

                        /// <summary>바탕 표. 서수 기반 API 가 필요할 때만 쓴다.</summary>
                        public global::ChServerM.DataTable.StaticTable Source => _table;

                        /// <summary>행 수.</summary>
                        public int Count => _table.RowCount;

                        /// <summary>행 번호로 행을 얻는다.</summary>
                        /// <param name="row">행 번호.</param>
                        public RecipeRow this[int row] => new RecipeRow(_table, row);

                        /// <summary>키로 행을 찾는다.</summary>
                        /// <param name="key">키 열의 <b>원문</b>. CSV 에 적힌 문자열 그대로 대조한다.</param>
                        /// <param name="row">찾은 행.</param>
                        /// <returns>찾았으면 true.</returns>
                        public bool TryGetRow(string key, out RecipeRow row)
                        {
                            if (_table.TryGetRow(key, out int index))
                            {
                                row = new RecipeRow(_table, index);
                                return true;
                            }

                            row = default;
                            return false;
                        }

                        /// <summary>모든 행을 순서대로 훑는다. 열거자가 struct 라 foreach 에 할당이 없다.</summary>
                        /// <returns>열거자.</returns>
                        public Enumerator GetEnumerator() => new Enumerator(_table);

                        private static global::ChServerM.DataTable.StaticTable Verify(global::ChServerM.DataTable.StaticTable table)
                        {
                            if (table is null) { throw new global::System.ArgumentNullException(nameof(table)); }

                            // ⚠ 참조 동일성이다. 구조만 같은 다른 스키마를 받아들이면 서수 일치 보장이 사라진다.
                            if (!object.ReferenceEquals(table.Schema, Schema))
                            {
                                throw new global::System.InvalidOperationException(
                                    "표 '" + table.Schema.Name + "' 이 RecipeRow.Schema 로 로딩되지 않았다. 로딩할 때 RecipeRow.Schema 를 넘긴다.");
                            }

                            return table;
                        }

                        /// <summary>행 열거자. 할당하지 않는다.</summary>
                        public struct Enumerator
                        {
                            private readonly global::ChServerM.DataTable.StaticTable _table;
                            private int _index;

                            internal Enumerator(global::ChServerM.DataTable.StaticTable table)
                            {
                                _table = table;
                                _index = -1;
                            }

                            /// <summary>현재 행.</summary>
                            public readonly RecipeRow Current => new RecipeRow(_table, _index);

                            /// <summary>다음 행으로 옮긴다.</summary>
                            /// <returns>행이 남아 있으면 true.</returns>
                            public bool MoveNext() => ++_index < _table.RowCount;
                        }
                    }
                }
            }
            """;

        Assert.Equal(Expected, generated);
    }

    // ── 드라이버 도우미 ──────────────────────────────────────────────

    private const string ReferenceSource = """
        using ChServerM.DataTable;

        namespace TestApp;

        [StaticTableRow("Recipe")]
        public readonly partial struct RecipeRow
        {
            [StaticTableColumn(Key = true)]
            public partial string Id { get; }
        }

        [StaticTableRow("Item")]
        public readonly partial struct ItemRow
        {
            [StaticTableColumn(Key = true)]
            public partial string Id { get; }

            [StaticTableColumn(Optional = true, References = typeof(RecipeRow))]
            public partial string? RecipeId { get; }
        }
        """;

    /// <summary>열 선언만 바꿔 가며 쓰는 최소 행 타입.</summary>
    private static string Row(string members) => $$"""
        using ChServerM.DataTable;

        namespace TestApp;

        [StaticTableRow("Item")]
        public readonly partial struct ItemRow
        {
            {{members}}
        }
        """;

    private static void AssertDiagnostic(string source, string id)
    {
        (GeneratorRunResult result, _) = Run(source);

        Assert.Contains(result.Diagnostics, d => d.Id == id);
    }

    private static (GeneratorRunResult Result, Compilation Output) Run(string source)
    {
        List<MetadataReference> references = [];

        // 테스트 러너 런타임의 참조 어셈블리 — BCL 해석용 표준 트릭.
        // ChServerM.* 는 걸러내고 필요한 것만 명시적으로 넣는다.
        string platformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in platformAssemblies.Split(Path.PathSeparator))
        {
            if (path.Length > 0
                && !Path.GetFileName(path).StartsWith("ChServerM.", StringComparison.Ordinal))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        references.Add(MetadataReference.CreateFromFile(
            typeof(ChServerM.DataTable.StaticTable).Assembly.Location));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "GeneratorTestAssembly",
            // ⚠ 파스 옵션을 명시하지 않는다. 언어 버전을 지정하면 제너레이터가 덧붙이는
            // 트리(기본 버전)와 어긋나 "Inconsistent language versions" 로 드라이버가 죽는다.
            [CSharpSyntaxTree.ParseText(source)],
            references,
            // ⚠ 널 허용 문맥이 켜져 있어야 CHSM2006(선택 문자열은 string?) 이 판정된다.
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new StaticTableAccessorGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        return (driver.GetRunResult().Results[0], output);
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
