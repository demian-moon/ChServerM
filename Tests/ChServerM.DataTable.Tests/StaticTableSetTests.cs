using System;
using ChServerM.DataTable;
using Xunit;

namespace ChServerM.DataTable.Tests;

/// <summary>
/// 참조 무결성과 범위 검사 — <b>표 하나만 봐서는 판정할 수 없는 것</b>들 (Phase 14).
/// </summary>
public sealed class StaticTableSetTests
{
    private static StaticTableSchema ItemSchema() => new(
        "Item",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("price", StaticTableColumnType.Int32) { MinimumInteger = 0 },
        ]);

    private static StaticTableSchema RecipeSchema() => new(
        "Recipe",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),

            // 이 열이 Item 테이블의 키를 가리킨다.
            new StaticTableColumn("itemId", StaticTableColumnType.String) { ReferencesTable = "Item" },
            new StaticTableColumn("bonusId", StaticTableColumnType.String, Required: false)
            {
                ReferencesTable = "Item",
            },
        ]);

    private const string ItemCsv = """
        id,price
        sword,100
        shield,80
        """;

    private const string RecipeCsv = """
        id,itemId,bonusId
        r1,sword,shield
        r2,shield,
        """;

    // ── 참조 무결성 + 인덱스 변환 ───────────────────────────────────────────

    [Fact]
    public void References_are_validated_and_resolved_to_row_indexes()
    {
        // ★ 검증과 인덱스 변환은 같은 패스다 — 유효한지 확인하려면 어차피 대상 행을 찾아야
        // 하고, 찾은 김에 저장해 두면 조회 때마다 키로 다시 찾지 않아도 된다.
        StaticTableSet set = new StaticTableSetBuilder()
            .Add(ItemSchema(), ItemCsv)
            .Add(RecipeSchema(), RecipeCsv)
            .Build();

        StaticTable recipes = set.GetTable("Recipe");
        StaticTable items = set.GetTable("Item");

        RecipeSchema().TryGetOrdinal("itemId", out int itemId);
        ItemSchema().TryGetOrdinal("price", out int price);

        Assert.True(recipes.TryGetRow("r1", out int r1));
        int targetRow = recipes.GetReference(r1, itemId);

        Assert.NotEqual(StaticTable.NoReference, targetRow);
        Assert.Equal(100, items.GetInt32(targetRow, price)); // sword 를 정확히 가리킨다
    }

    [Fact]
    public void Optional_reference_may_be_empty()
    {
        StaticTableSet set = new StaticTableSetBuilder()
            .Add(ItemSchema(), ItemCsv)
            .Add(RecipeSchema(), RecipeCsv)
            .Build();

        StaticTable recipes = set.GetTable("Recipe");
        RecipeSchema().TryGetOrdinal("bonusId", out int bonusId);

        recipes.TryGetRow("r2", out int r2);
        Assert.Equal(StaticTable.NoReference, recipes.GetReference(r2, bonusId));
    }

    [Fact]
    public void Dangling_reference_fails_the_whole_set()
    {
        // ★ 이것이 표 하나만 봐서는 판정할 수 없는 것이다.
        const string BadRecipes = """
            id,itemId,bonusId
            r1,없는아이템,
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            new StaticTableSetBuilder()
                .Add(ItemSchema(), ItemCsv)
                .Add(RecipeSchema(), BadRecipes)
                .Build());

        Assert.Contains(ex.Errors, e => e.Message.Contains("없는아이템", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_target_table_is_reported()
    {
        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            new StaticTableSetBuilder()
                .Add(RecipeSchema(), RecipeCsv) // Item 을 넣지 않았다
                .Build());

        Assert.Contains(ex.Errors, e => e.Message.Contains("Item", StringComparison.Ordinal));
    }

    [Fact]
    public void Errors_from_multiple_tables_are_reported_together()
    {
        // ★ 표 A 가 깨졌다고 먼저 던지면 사용자는 A 를 고친 뒤에야 B 의 문제를 알게 된다.
        const string BadItems = """
            id,price
            sword,가격아님
            """;

        const string BadRecipes = """
            id,itemId,bonusId
            r1,
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            new StaticTableSetBuilder()
                .Add(ItemSchema(), BadItems)
                .Add(RecipeSchema(), BadRecipes)
                .Build());

        Assert.Contains(ex.Errors, e => e.Message.Contains("[Item]", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Message.Contains("[Recipe]", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_table_names_are_rejected()
    {
        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            new StaticTableSetBuilder()
                .Add(ItemSchema(), ItemCsv)
                .Add(ItemSchema(), ItemCsv)
                .Build());

        Assert.Contains(ex.Errors, e => e.Message.Contains("중복", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_lookup_without_a_set_is_a_clear_error()
    {
        // 묶음 없이 읽은 표는 참조가 해결되지 않았다 — 반쯤 만들어진 상태가 조용히 쓰이면 안 된다.
        StaticTable recipes = CsvStaticTableReader.Read(RecipeSchema(), RecipeCsv);
        RecipeSchema().TryGetOrdinal("itemId", out int itemId);

        recipes.TryGetRow("r1", out int row);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            recipes.GetReference(row, itemId));

        Assert.Contains(nameof(StaticTableSetBuilder), ex.Message, StringComparison.Ordinal);
    }

    // ── 범위 검사 ───────────────────────────────────────────────────────────

    [Fact]
    public void Integer_range_is_enforced_at_load()
    {
        const string Csv = """
            id,price
            sword,-1
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), Csv));

        Assert.Contains(ex.Errors, e => e.Message.Contains("최솟값", StringComparison.Ordinal));
    }

    [Fact]
    public void Real_range_is_enforced_at_load()
    {
        StaticTableSchema schema = new(
            "Rate", "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("ratio", StaticTableColumnType.Double)
                {
                    MinimumReal = 0.0,
                    MaximumReal = 1.0,
                },
            ]);

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(schema, "id,ratio\na,1.5\n"));

        Assert.Contains(ex.Errors, e => e.Message.Contains("최댓값", StringComparison.Ordinal));
    }

    // ── 모순된 스키마는 조립 시점에 막는다 ──────────────────────────────────

    [Fact]
    public void Integer_range_on_a_string_column_is_rejected()
    {
        // ★ 조용히 무시되는 설정이 가장 위험하다 — 작성자는 제약을 걸었다고 믿는다.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new StaticTableSchema(
            "T", "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String) { MinimumInteger = 0 },
            ]));

        Assert.Contains("정수 범위", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_range_on_an_integer_column_is_rejected()
    {
        // Int64 의 2^53 초과 값은 double 로 정확히 표현되지 않아 경계에서 조용히 틀린다 —
        // 그래서 정수 열에는 정수 범위만 허용한다.
        Assert.Throws<ArgumentException>(() => new StaticTableSchema(
            "T", "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("n", StaticTableColumnType.Int64) { MinimumReal = 0.0 },
            ]));
    }

    [Fact]
    public void Inverted_range_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new StaticTableSchema(
            "T", "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("n", StaticTableColumnType.Int32)
                {
                    MinimumInteger = 10,
                    MaximumInteger = 1,
                },
            ]));
    }

    [Fact]
    public void Reference_on_a_non_string_column_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new StaticTableSchema(
            "T", "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("ref", StaticTableColumnType.Int32) { ReferencesTable = "Item" },
            ]));
    }

    // ── 묶음 조회 ───────────────────────────────────────────────────────────

    [Fact]
    public void Set_exposes_tables_by_name()
    {
        StaticTableSet set = new StaticTableSetBuilder()
            .Add(ItemSchema(), ItemCsv)
            .Add(RecipeSchema(), RecipeCsv)
            .Build();

        Assert.Equal(2, set.Count);
        Assert.True(set.TryGetTable("Item", out StaticTable? item));
        Assert.NotNull(item);
        Assert.False(set.TryGetTable("없는표", out _));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        StaticTableSetBuilder builder = new();

        Assert.Throws<ArgumentNullException>(() => builder.Add(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => builder.Add(ItemSchema(), null!));
    }
}
