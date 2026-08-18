using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;

namespace ChServerM.DataTable.Tests;

/// <summary>
/// 표 스냅샷의 왕복과 <b>거부 조건</b> 검증 (ADR-0045).
/// </summary>
/// <remarks>
/// <para>
/// <b>⭐ 합격 기준은 지문 보존이다.</b> 굽고 되살린 묶음의 지문이 원본과 같다는 것은
/// 스키마·값·행 순서가 전부 그대로라는 뜻이다 — 필드를 하나씩 비교하는 것보다 강하고,
/// 나중에 열 종류가 늘어도 이 단언은 그대로 유효하다.
/// </para>
/// <para>
/// <b>거부 조건이 절반이다.</b> 값이 열 우선으로 구워져 있어 <b>어긋난 스키마로 읽으면
/// 전부 엉뚱한 열로 조용히 해석</b>된다 — 이 형식에서 가장 위험한 실패이므로, 어긋남을
/// 실제로 거부하는지가 왕복만큼 중요하다.
/// </para>
/// </remarks>
public sealed class StaticTableSnapshotTests
{
    private static readonly StaticTableSchema ItemSchema = new(
        "Item",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("damage", StaticTableColumnType.Int32),
            new StaticTableColumn("cost", StaticTableColumnType.Int64),
            new StaticTableColumn("rate", StaticTableColumnType.Double),
            new StaticTableColumn("tradable", StaticTableColumnType.Boolean),
            new StaticTableColumn("recipe", StaticTableColumnType.String, Required: false)
            {
                ReferencesTable = "Recipe",
            },
        ]);

    private static readonly StaticTableSchema RecipeSchema = new(
        "Recipe",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("cost", StaticTableColumnType.Int64),
        ]);

    private const string ItemCsv = """
        # 밸런스 표
        id,damage,cost,rate,tradable,recipe
        sword,10,1000,0.5,true,r1
        shield,5,250,0.25,false,
        """;

    private const string RecipeCsv = """
        id,cost
        r1,100
        r2,250
        """;

    private static StaticTableSet Load() =>
        new StaticTableSetBuilder()
            .Add(ItemSchema, ItemCsv)
            .Add(RecipeSchema, RecipeCsv)
            .Build();

    private static StaticTableSchema[] Schemas => [ItemSchema, RecipeSchema];

    // ── 손상 방어 ────────────────────────────────────────────────────

    [Fact]
    public void Corrupted_huge_rowCount_is_rejected_before_allocation()
    {
        // 회귀(감사 2026-08-18 R-5): 선언된 행 수를 믿고 배열을 선할당하면 손상 스냅샷
        // 하나(rowCount ≈ int.MaxValue)가 수 GB 할당 → OOM 이 된다. 값을 읽기 전에
        // "남은 바이트로 성립하는가"를 검증해 거부해야 한다.
        byte[] bytes = StaticTableSnapshot.ToArray(Load());

        // Item 표 머리말의 (columnCount=6, rowCount=2) 8바이트 패턴을 찾아 rowCount 를 조작한다.
        ReadOnlySpan<byte> pattern = [6, 0, 0, 0, 2, 0, 0, 0];
        int at = bytes.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0, "표 머리말 패턴을 찾지 못했다 — 스냅샷 형식이 바뀌었으면 테스트를 갱신한다.");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), int.MaxValue);

        Assert.Throws<StaticTableLoadException>(() => StaticTableSnapshot.Read(bytes, Schemas));
    }

    [Fact]
    public void Corrupted_huge_tableCount_is_rejected_before_allocation()
    {
        byte[] bytes = StaticTableSnapshot.ToArray(Load());

        // 머리말 레이아웃: 매직 "CHSMTBL\0"(8) + 버전(2) + 예약(2) + tableCount(4).
        const int tableCountOffset = 12;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(tableCountOffset), int.MaxValue);

        Assert.Throws<StaticTableLoadException>(() => StaticTableSnapshot.Read(bytes, Schemas));
    }

    // ── 왕복 ─────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_preservesFingerprint()
    {
        // ⭐ 이 한 줄이 스키마·값·행 순서를 전부 덮는다.
        StaticTableSet original = Load();
        StaticTableSet restored = StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(original), Schemas);

        Assert.Equal(original.Fingerprint, restored.Fingerprint);
    }

    [Fact]
    public void RoundTrip_preservesEveryValueType()
    {
        StaticTableSet restored = StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), Schemas);
        StaticTable items = restored.GetTable("Item");

        Assert.True(items.TryGetRow("sword", out int row));
        Assert.Equal("sword", items.GetString(row, 0));
        Assert.Equal(10, items.GetInt32(row, 1));
        Assert.Equal(1000L, items.GetInt64(row, 2));
        Assert.Equal(0.5, items.GetDouble(row, 3));
        Assert.True(items.GetBoolean(row, 4));
        Assert.Equal("r1", items.GetString(row, 5));
    }

    [Fact]
    public void RoundTrip_preservesNullInOptionalColumn()
    {
        StaticTableSet restored = StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), Schemas);
        StaticTable items = restored.GetTable("Item");

        Assert.True(items.TryGetRow("shield", out int row));
        Assert.Null(items.GetString(row, 5));
    }

    [Fact]
    public void RoundTrip_resolvesReferencesAgain()
    {
        // 참조 해결 결과는 싣지 않는다 — 값에서 유도되는 것이라 따로 실으면 값과 어긋난
        // 상태를 만들 수 있다. 받는 쪽이 로딩과 같은 패스로 다시 푼다.
        StaticTableSet restored = StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), Schemas);
        StaticTable items = restored.GetTable("Item");

        Assert.True(items.TryGetRow("sword", out int sword));
        Assert.Equal(0, items.GetReference(sword, 5));

        Assert.True(items.TryGetRow("shield", out int shield));
        Assert.Equal(StaticTable.NoReference, items.GetReference(shield, 5));
    }

    [Fact]
    public void RoundTrip_keepsTheLocalSchemaInstance()
    {
        // ⭐ 강타입 뷰(ADR-0043)가 참조 동일성으로 서수 일치를 보장하므로, 와이어에서 만든
        // 새 인스턴스로 표를 세우면 생성된 접근자가 받은 표를 거부한다.
        StaticTableSet restored = StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), Schemas);

        Assert.Same(ItemSchema, restored.GetTable("Item").Schema);
        Assert.Same(RecipeSchema, restored.GetTable("Recipe").Schema);
    }

    [Fact]
    public void Write_isDeterministic_regardlessOfRegistrationOrder()
    {
        // 같은 묶음이 언제 구워도 같은 바이트여야 캐시·재전송 판단이 가능하다.
        StaticTableSet forward = new StaticTableSetBuilder()
            .Add(ItemSchema, ItemCsv).Add(RecipeSchema, RecipeCsv).Build();
        StaticTableSet reversed = new StaticTableSetBuilder()
            .Add(RecipeSchema, RecipeCsv).Add(ItemSchema, ItemCsv).Build();

        Assert.Equal(StaticTableSnapshot.ToArray(forward), StaticTableSnapshot.ToArray(reversed));
    }

    [Fact]
    public void Write_toBufferWriter_matchesToArray()
    {
        ArrayBufferWriter<byte> buffer = new();
        StaticTableSnapshot.Write(Load(), buffer);

        Assert.Equal(StaticTableSnapshot.ToArray(Load()), buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void RoundTrip_emptyTable_works()
    {
        // 행이 0개인 표도 유효하다. 열 우선 쓰기는 이때 값을 하나도 쓰지 않으므로
        // 경계에서 위치 계산이 어긋나기 쉽다.
        StaticTableSet set = new StaticTableSetBuilder()
            .Add(RecipeSchema, "id,cost")
            .Build();

        StaticTableSchema[] only = [RecipeSchema];
        StaticTableSet restored = StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(set), only);

        Assert.Equal(0, restored.GetTable("Recipe").RowCount);
        Assert.Equal(set.Fingerprint, restored.Fingerprint);
    }

    // ── 거부 조건 ────────────────────────────────────────────────────

    [Fact]
    public void Read_rejectsForeignPayload()
    {
        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read("이건 스냅샷이 아니다"u8, Schemas));

        Assert.Contains(error.Errors, e => e.Message.Contains("매직", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_rejectsUnknownFormatVersion()
    {
        byte[] snapshot = StaticTableSnapshot.ToArray(Load());
        snapshot[8] = 99; // 버전 필드(매직 8바이트 바로 뒤)

        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(snapshot, Schemas));

        Assert.Contains(error.Errors, e => e.Message.Contains("형식 버전", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(60)]
    public void Read_rejectsTruncatedInput(int cutFromEnd)
    {
        // ⚠ 잘린 입력이 예외가 아니라 조용한 오독이 되면 안 된다. 모든 읽기가 길이를
        // 먼저 확인하므로 어디서 잘려도 실패로 끝나야 한다.
        byte[] snapshot = StaticTableSnapshot.ToArray(Load());

        Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(snapshot.AsSpan(0, snapshot.Length - cutFromEnd), Schemas));
    }

    [Fact]
    public void Read_rejectsTrailingBytes()
    {
        byte[] snapshot = [.. StaticTableSnapshot.ToArray(Load()), 0, 0, 0];

        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(snapshot, Schemas));

        Assert.Contains(error.Errors, e => e.Message.Contains("해석되지 않은 바이트", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_rejectsMissingLocalSchema()
    {
        StaticTableSchema[] incomplete = [ItemSchema];

        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), incomplete));

        Assert.Contains(error.Errors, e => e.Message.Contains("로컬 스키마가 없다", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_rejectsTableExpectedButNotSent()
    {
        // 조용히 없는 표가 되면 첫 조회에서 KeyNotFoundException 이 나고, 그때는 원인이
        // 여기서 멀어져 있다.
        StaticTableSet onlyRecipes = new StaticTableSetBuilder().Add(RecipeSchema, RecipeCsv).Build();

        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(onlyRecipes), Schemas));

        Assert.Contains(error.Errors, e => e.Message.Contains("기대한 표가 없다", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_rejectsRenamedColumn()
    {
        StaticTableSchema renamed = new(
            "Recipe",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("price", StaticTableColumnType.Int64),
            ]);

        AssertSchemaRejected(renamed, "열 이름이 다르다");
    }

    [Fact]
    public void Read_rejectsChangedColumnType()
    {
        StaticTableSchema retyped = new(
            "Recipe",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("cost", StaticTableColumnType.Int32),
            ]);

        AssertSchemaRejected(retyped, "종류가 다르다");
    }

    [Fact]
    public void Read_rejectsChangedRequiredness()
    {
        StaticTableSchema relaxed = new(
            "Recipe",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("cost", StaticTableColumnType.Int64, Required: false),
            ]);

        AssertSchemaRejected(relaxed, "필수 여부가 다르다");
    }

    [Fact]
    public void Read_rejectsExtraColumn()
    {
        StaticTableSchema widened = new(
            "Recipe",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("cost", StaticTableColumnType.Int64),
                new StaticTableColumn("extra", StaticTableColumnType.String),
            ]);

        AssertSchemaRejected(widened, "열 수가 다르다");
    }

    [Fact]
    public void Read_rejectsChangedKeyColumn()
    {
        StaticTableSchema rekeyed = new(
            "Recipe",
            "cost",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("cost", StaticTableColumnType.Int64),
            ]);

        AssertSchemaRejected(rekeyed, "키 열이 다르다");
    }

    [Fact]
    public void Read_rejectsDuplicateLocalSchemaNames()
    {
        StaticTableSchema[] duplicated = [RecipeSchema, RecipeSchema];

        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), duplicated));

        Assert.Contains(error.Errors, e => e.Message.Contains("같은 표 이름이 둘 이상", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_nullSchemas_throws() =>
        Assert.Throws<ArgumentNullException>(
            () => StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(Load()), null!));

    [Fact]
    public void Write_nullArguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => StaticTableSnapshot.ToArray(null!));
        Assert.Throws<ArgumentNullException>(() => StaticTableSnapshot.Write(Load(), null!));
    }

    /// <summary>레시피 표만 담은 스냅샷을 어긋난 로컬 스키마로 읽어 거부를 확인한다.</summary>
    private static void AssertSchemaRejected(StaticTableSchema local, string expectedFragment)
    {
        StaticTableSet set = new StaticTableSetBuilder().Add(RecipeSchema, RecipeCsv).Build();
        List<StaticTableSchema> schemas = [local];

        StaticTableLoadException error = Assert.Throws<StaticTableLoadException>(
            () => StaticTableSnapshot.Read(StaticTableSnapshot.ToArray(set), schemas));

        Assert.Contains(error.Errors, e => e.Message.Contains(expectedFragment, StringComparison.Ordinal));
    }
}
