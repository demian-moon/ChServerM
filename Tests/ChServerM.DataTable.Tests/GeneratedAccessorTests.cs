using System;
using System.Collections.Generic;
using System.IO;
using ChServerM.DataTable.Generated;
using Xunit;

namespace ChServerM.DataTable.Tests;

/// <summary>
/// <c>[StaticTableRow]</c> 로 선언한 행 타입의 <b>종단 검증</b> — 실제 빌드가 생성한 코드를 돌린다.
/// </summary>
/// <remarks>
/// <para>
/// <b>드라이버 테스트와 다른 것.</b> <c>SourceGen.Tests</c> 는 생성 문자열이 문법적으로 맞고
/// 진단이 제대로 나오는지를 본다. 여기서는 그 코드가 <b>실제로 옳은 값을 읽는지</b>를 본다 —
/// 서수를 하나 밀어서 생성해도 드라이버 테스트는 통과하지만 이쪽은 통과하지 못한다.
/// </para>
/// <para>
/// <b>이 파일 자체가 제너레이터의 사용 예다.</b> 아래 선언에 <b>서수도, 열 이름 문자열 조회도
/// 없다</b> — 그것이 이 증분의 목적이다.
/// </para>
/// </remarks>
public sealed class GeneratedAccessorTests
{
    private const string ItemCsv = """
        # 밸런스 표. 주석과 빈 줄은 건너뛴다.
        id,damage,drop_rate,tradable,recipe
        sword,10,0.5,true,r1
        shield,5,0.25,false,
        """;

    private const string RecipeCsv = """
        id,cost
        r1,100
        r2,250
        """;

    private static StaticTableSet Load(string itemCsv = ItemCsv, string recipeCsv = RecipeCsv) =>
        new StaticTableSetBuilder()
            .Add(RecipeRow.Schema, recipeCsv)
            .Add(ItemRow.Schema, itemCsv)
            .Build();

    // ── 스키마 생성 ──────────────────────────────────────────────────

    [Fact]
    public void Schema_ComesFromDeclaration_InDeclarationOrder()
    {
        StaticTableSchema schema = ItemRow.Schema;

        Assert.Equal("Item", schema.Name);
        Assert.Equal("id", schema.KeyColumnName);
        Assert.Equal(0, schema.KeyOrdinal);

        // 선언 순서가 곧 서수다 — 사람이 서수를 적을 자리가 없다는 것이 요점이다.
        Assert.Equal(
            ["id", "damage", "drop_rate", "tradable", "recipe"],
            Names(schema));

        Assert.Equal(StaticTableColumnType.String, schema.Columns[0].Type);
        Assert.Equal(StaticTableColumnType.Int32, schema.Columns[1].Type);
        Assert.Equal(StaticTableColumnType.Double, schema.Columns[2].Type);
        Assert.Equal(StaticTableColumnType.Boolean, schema.Columns[3].Type);
        Assert.Equal(StaticTableColumnType.String, schema.Columns[4].Type);
    }

    [Fact]
    public void Schema_IsSingleton_SoViewsCanCompareByReference()
    {
        Assert.Same(ItemRow.Schema, ItemRow.Schema);
    }

    [Fact]
    public void Schema_CarriesConstraints_FromAttribute()
    {
        StaticTableSchema schema = ItemRow.Schema;

        Assert.Equal(0L, schema.Columns[1].MinimumInteger);
        Assert.Equal(9999L, schema.Columns[1].MaximumInteger);
        Assert.Equal(0.0, schema.Columns[2].MinimumReal);
        Assert.Equal(1.0, schema.Columns[2].MaximumReal);

        // 제약을 적지 않은 열에는 제약이 없다 — 센티넬 기본값이 들어가지 않는다.
        Assert.Null(schema.Columns[0].MinimumInteger);
        Assert.Null(schema.Columns[0].MaximumInteger);
        Assert.Null(schema.Columns[1].MinimumReal);
    }

    [Fact]
    public void Schema_MarksOptionalAndReference()
    {
        StaticTableSchema schema = ItemRow.Schema;

        Assert.True(schema.Columns[0].Required);
        Assert.False(schema.Columns[4].Required);
        Assert.Equal("Recipe", schema.Columns[4].ReferencesTable);
        Assert.True(schema.Columns[4].IsReference);
    }

    // ── 값 읽기 ──────────────────────────────────────────────────────

    [Fact]
    public void TypedRead_ReturnsParsedValues()
    {
        ItemRow.Table items = new(Load());

        Assert.True(items.TryGetRow("sword", out ItemRow sword));
        Assert.Equal("sword", sword.Id);
        Assert.Equal(10, sword.Damage);
        Assert.Equal(0.5, sword.DropRate);
        Assert.True(sword.Tradable);
        Assert.Equal("r1", sword.RecipeId);
        Assert.Equal(0, sword.RowIndex);
    }

    [Fact]
    public void OptionalColumn_EmptyCell_IsNull()
    {
        ItemRow.Table items = new(Load());

        Assert.True(items.TryGetRow("shield", out ItemRow shield));
        Assert.Null(shield.RecipeId);
    }

    [Fact]
    public void Reference_IsResolvedToRowIndex_NotLookedUpByKey()
    {
        StaticTableSet set = Load();
        ItemRow.Table items = new(set);
        RecipeRow.Table recipes = new(set);

        Assert.True(items.TryGetRow("sword", out ItemRow sword));

        // 참조가 이미 행 번호다 — 여기서 키로 다시 찾는 코드가 없다는 것이 요점이다.
        RecipeRow recipe = recipes[sword.RecipeIdRowIndex];
        Assert.Equal("r1", recipe.Id);
        Assert.Equal(100L, recipe.Cost);
    }

    [Fact]
    public void Reference_Empty_IsNoReference()
    {
        ItemRow.Table items = new(Load());

        Assert.True(items.TryGetRow("shield", out ItemRow shield));
        Assert.Equal(StaticTable.NoReference, shield.RecipeIdRowIndex);
    }

    [Fact]
    public void TryGetRow_MissingKey_ReturnsFalse()
    {
        ItemRow.Table items = new(Load());

        Assert.False(items.TryGetRow("bow", out _));
    }

    [Fact]
    public void Indexer_And_Count_FollowFileOrder()
    {
        ItemRow.Table items = new(Load());

        Assert.Equal(2, items.Count);
        Assert.Equal("sword", items[0].Id);
        Assert.Equal("shield", items[1].Id);
    }

    [Fact]
    public void Enumeration_YieldsEveryRowInOrder()
    {
        ItemRow.Table items = new(Load());
        List<string> ids = [];

        foreach (ItemRow item in items)
        {
            ids.Add(item.Id);
        }

        Assert.Equal(["sword", "shield"], ids);
    }

    // ── 뷰를 묶는 규약 ───────────────────────────────────────────────

    [Fact]
    public void View_RejectsTableLoadedWithADifferentSchemaInstance()
    {
        // 구조가 완전히 같은 스키마를 손으로 만든다. 그래도 거부돼야 한다 —
        // 서수 일치의 근거가 "같은 선언에서 나왔다" 는 사실 자체이기 때문이다.
        StaticTableSchema lookalike = new(
            "Item",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("damage", StaticTableColumnType.Int32),
                new StaticTableColumn("drop_rate", StaticTableColumnType.Double),
                new StaticTableColumn("tradable", StaticTableColumnType.Boolean),
                new StaticTableColumn("recipe", StaticTableColumnType.String, Required: false),
            ]);

        StaticTable table = CsvStaticTableReader.Read(lookalike, ItemCsv);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new ItemRow.Table(table));

        Assert.Contains("ItemRow.Schema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void View_MissingTableInSet_Throws()
    {
        StaticTableSet recipesOnly = new StaticTableSetBuilder()
            .Add(RecipeRow.Schema, RecipeCsv)
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new ItemRow.Table(recipesOnly));

        Assert.Contains("Item", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void View_NullSet_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ItemRow.Table((StaticTableSet)null!));
    }

    // ── 생성된 스키마로 로딩하면 검증이 그대로 걸린다 ───────────────

    [Fact]
    public void RangeFromAttribute_IsEnforcedAtLoad()
    {
        const string OverMaximum = """
            id,damage,drop_rate,tradable,recipe
            sword,10000,0.5,true,
            """;

        StaticTableLoadException error =
            Assert.Throws<StaticTableLoadException>(() => Load(itemCsv: OverMaximum));

        Assert.Contains(error.Errors, e => e.ColumnName == "damage");
    }

    [Fact]
    public void ReferenceIntegrity_IsEnforcedAtLoad()
    {
        const string DanglingReference = """
            id,damage,drop_rate,tradable,recipe
            sword,10,0.5,true,does-not-exist
            """;

        StaticTableLoadException error =
            Assert.Throws<StaticTableLoadException>(() => Load(itemCsv: DanglingReference));

        Assert.Contains(error.Errors, e => e.ColumnName == "recipe");
    }

    // ── 핫 리로드와의 조합 ───────────────────────────────────────────

    [Fact]
    public void HotReload_RebindingTheViewSeesNewData()
    {
        ReloadableStaticTableSet reloadable = new(Load());
        ItemRow.Table before = new(reloadable.Current);
        Assert.Equal(10, Damage(before, "sword"));

        const string Buffed = """
            id,damage,drop_rate,tradable,recipe
            sword,12,0.5,true,r1
            shield,5,0.25,false,
            """;

        StaticTableReloadResult result = reloadable.TryReload(() => Load(itemCsv: Buffed));
        Assert.True(result.Succeeded);

        // 재적재 전에 받은 뷰는 옛 데이터를 계속 본다 — 한 작업 안에서 표가 섞이지 않는다.
        Assert.Equal(10, Damage(before, "sword"));

        ItemRow.Table after = new(reloadable.Current);
        Assert.Equal(12, Damage(after, "sword"));
    }

    [Fact]
    public void HotReload_ValidationFailure_KeepsOldDataAndTheViewStaysValid()
    {
        ReloadableStaticTableSet reloadable = new(Load());

        const string Broken = """
            id,damage,drop_rate,tradable,recipe
            sword,99999,0.5,true,r1
            """;

        StaticTableReloadResult result = reloadable.TryReload(() => Load(itemCsv: Broken));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(10, Damage(new ItemRow.Table(reloadable.Current), "sword"));
    }

    // ── 어셈블리별 스키마 레지스트리 ─────────────────────────────────

    [Fact]
    public void Registry_containsEveryDeclaredSchema()
    {
        // 손 목록을 없애는 것이 목적이다 — 이 어셈블리에 행 타입을 추가하면 저절로 들어온다.
        Assert.Contains(ItemRow.Schema, GeneratedStaticTableSchemas.All);
        Assert.Contains(RecipeRow.Schema, GeneratedStaticTableSchemas.All);
    }

    // ── 실제 빌드가 대조한 CSV ───────────────────────────────────────

    [Fact]
    public void RealCsvFiles_loadWithTheDeclaredSchemas()
    {
        // ⭐ 이 파일들은 `AdditionalFiles` 로도 들어가 **빌드 때 헤더가 대조됐다**(CHSM2011).
        // 열 이름을 하나 고치면 이 테스트가 아니라 **빌드**가 먼저 실패한다 — 기동 시점
        // 검증을 컴파일 타임으로 당긴 것이 실제로 도는지 확인하는 자리다.
        string directory = Path.Combine(AppContext.BaseDirectory, "Tables");

        StaticTableSet set = new StaticTableSetBuilder()
            .Add(RecipeRow.Schema, File.ReadAllText(Path.Combine(directory, "Recipe.csv")))
            .Add(ItemRow.Schema, File.ReadAllText(Path.Combine(directory, "Item.csv")))
            .Build();

        ItemRow.Table items = new(set);
        Assert.True(items.TryGetRow("sword", out ItemRow sword));
        Assert.Equal(10, sword.Damage);
    }

    // ── 스냅샷으로 받은 표 ───────────────────────────────────────────

    [Fact]
    public void SnapshotRoundTrip_worksWithTheGeneratedView()
    {
        // ⭐ 두 증분이 맞물리는 지점이다. 뷰는 스키마 **참조 동일성**으로 서수 일치를
        // 보장하므로(ADR-0043), 스냅샷 리더가 와이어 스키마로 표를 세웠다면 여기서
        // 거부당한다. 로컬 스키마를 쓴다는 결정(ADR-0045)이 이 테스트로 고정된다 —
        // 클라이언트는 **데이터 파일 없이** 서버가 보낸 표를 강타입으로 읽는다.
        byte[] wire = StaticTableSnapshot.ToArray(Load());

        // ⭐ 스키마 목록을 손으로 적지 않는다. 표를 하나 추가하고 이 목록에 넣는 것을 잊는
        // 사고가 서수를 손으로 적던 것과 같은 종류라, 레지스트리도 선언에서 생성한다.
        StaticTableSet received = StaticTableSnapshot.Read(wire, GeneratedStaticTableSchemas.All);

        ItemRow.Table items = new(received);
        RecipeRow.Table recipes = new(received);

        Assert.True(items.TryGetRow("sword", out ItemRow sword));
        Assert.Equal(10, sword.Damage);
        Assert.Equal(0.5, sword.DropRate);
        Assert.Equal("r1", sword.RecipeId);

        // 참조도 되살아난다 — 받는 쪽에서 다시 풀기 때문이다.
        Assert.Equal("r1", recipes[sword.RecipeIdRowIndex].Id);
    }

    // ── 무할당 ───────────────────────────────────────────────────────

    [Fact]
    public void TypedRead_DoesNotAllocate()
    {
        ItemRow.Table items = new(Load());

        // 워밍업 — JIT 과 첫 접근의 정적 초기화를 측정에서 뺀다.
        Sum(items);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = Sum(items);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(15, checksum);

        // 행 구조체·열거자·인덱서가 전부 값 타입이라 힙에 아무것도 남지 않아야 한다.
        // 문자열 열을 읽지 않는 이유: 그것은 이미 존재하는 인스턴스를 돌려주지만
        // 이 게이트의 대상은 "읽는 행위 자체가 할당하는가" 이다.
        Assert.Equal(0, allocated);
    }

    private static long Sum(ItemRow.Table items)
    {
        long total = 0;

        foreach (ItemRow item in items)
        {
            total += item.Damage;
        }

        return total;
    }

    private static int Damage(ItemRow.Table items, string id)
    {
        Assert.True(items.TryGetRow(id, out ItemRow row));
        return row.Damage;
    }

    private static string[] Names(StaticTableSchema schema)
    {
        string[] names = new string[schema.Columns.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = schema.Columns[i].Name;
        }

        return names;
    }
}

/// <summary>테스트용 아이템 표 — <b>서수도 열 이름 문자열 조회도 없는 선언</b>.</summary>
[StaticTableRow("Item")]
public readonly partial struct ItemRow
{
    /// <summary>아이템 식별자(키).</summary>
    [StaticTableColumn(Name = "id", Key = true)]
    public partial string Id { get; }

    /// <summary>공격력.</summary>
    [StaticTableColumn(Name = "damage", MinimumInteger = 0, MaximumInteger = 9999)]
    public partial int Damage { get; }

    /// <summary>드롭 확률.</summary>
    [StaticTableColumn(Name = "drop_rate", MinimumReal = 0.0, MaximumReal = 1.0)]
    public partial double DropRate { get; }

    /// <summary>거래 가능 여부.</summary>
    [StaticTableColumn(Name = "tradable")]
    public partial bool Tradable { get; }

    /// <summary>제작법 참조. 비어 있을 수 있다.</summary>
    [StaticTableColumn(Name = "recipe", Optional = true, References = typeof(RecipeRow))]
    public partial string? RecipeId { get; }
}

/// <summary>테스트용 제작법 표 — 참조 대상.</summary>
[StaticTableRow("Recipe")]
public readonly partial struct RecipeRow
{
    /// <summary>제작법 식별자(키).</summary>
    [StaticTableColumn(Name = "id", Key = true)]
    public partial string Id { get; }

    /// <summary>제작 비용.</summary>
    [StaticTableColumn(Name = "cost")]
    public partial long Cost { get; }
}
