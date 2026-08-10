using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ChServerM.DataTable;
using Xunit;

namespace ChServerM.DataTable.Tests;

/// <summary>
/// 정적 데이터 테이블의 로딩·검증·조회를 검증한다 (Phase 14).
/// </summary>
/// <remarks>
/// <para>
/// 단언의 절반은 <b>레거시가 하지 않았던 것</b>에 관한 것이다
/// (docs/legacy/11-data-table "새 코드에 절대 옮기면 안 되는 것"):
/// 로딩 시점 검증 · 컬처 불변 파싱 · 오류를 한 번에 보고 · 키 중복 거부.
/// </para>
/// </remarks>
public sealed class StaticTableTests
{
    private static StaticTableSchema ItemSchema() => new(
        "Item",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("name", StaticTableColumnType.String),
            new StaticTableColumn("price", StaticTableColumnType.Int32),
            new StaticTableColumn("weight", StaticTableColumnType.Double),
            new StaticTableColumn("tradable", StaticTableColumnType.Boolean),
            new StaticTableColumn("note", StaticTableColumnType.String, Required: false),
        ]);

    private const string ValidCsv = """
        # 아이템 기본 표 — 주석은 무시된다
        id,name,price,weight,tradable,note
        sword,롱소드,100,3.5,true,
        shield,라운드실드,80,5.25,false,방어구

        potion,"물약, 소형",15,0.1,1,
        """;

    // ── 기본 왕복 ───────────────────────────────────────────────────────────

    [Fact]
    public void Values_are_parsed_at_load_and_read_by_ordinal()
    {
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);

        Assert.Equal(3, table.RowCount);
        Assert.True(table.TryGetRow("shield", out int row));

        Assert.True(schema.TryGetOrdinal("price", out int price));
        Assert.True(schema.TryGetOrdinal("weight", out int weight));
        Assert.True(schema.TryGetOrdinal("tradable", out int tradable));
        Assert.True(schema.TryGetOrdinal("name", out int name));

        Assert.Equal(80, table.GetInt32(row, price));
        Assert.Equal(5.25, table.GetDouble(row, weight));
        Assert.False(table.GetBoolean(row, tradable));
        Assert.Equal("라운드실드", table.GetString(row, name));
    }

    [Fact]
    public void Quoted_fields_keep_their_commas()
    {
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);

        Assert.True(table.TryGetRow("potion", out int row));
        schema.TryGetOrdinal("name", out int name);

        Assert.Equal("물약, 소형", table.GetString(row, name));
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        // 주석을 허용하는 이유: 밸런스 표에는 "이 값은 왜 이런가" 를 적을 자리가 필요하다.
        StaticTable table = CsvStaticTableReader.Read(ItemSchema(), ValidCsv);

        Assert.Equal(3, table.RowCount); // 주석 1줄 + 빈 줄 1줄은 행이 아니다
    }

    [Fact]
    public void Optional_column_may_be_empty()
    {
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);

        table.TryGetRow("sword", out int row);
        schema.TryGetOrdinal("note", out int note);

        Assert.Null(table.GetString(row, note));
    }

    [Fact]
    public void Boolean_accepts_one_and_zero()
    {
        // 표 편집기에서 참·거짓을 숫자로 쓰는 습관이 흔하다.
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);

        table.TryGetRow("potion", out int row);
        schema.TryGetOrdinal("tradable", out int tradable);

        Assert.True(table.GetBoolean(row, tradable));
    }

    // ── ★ 로딩 시점 검증 (레거시가 하지 않던 것) ────────────────────────────

    [Fact]
    public void All_errors_are_reported_at_once()
    {
        // ★★ 첫 오류에서 멈추지 않는 것이 설계다. 오류가 20개인데 하나씩 알려 주면
        // "고치고 → 다시 띄우고" 를 20번 한다.
        const string Csv = """
            id,name,price,weight,tradable,note
            a,이름,열,1.0,true,
            b,이름,10,실수아님,true,
            c,이름,10,1.0,아마도,
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), Csv));

        Assert.Equal(3, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.ColumnName == "price");
        Assert.Contains(ex.Errors, e => e.ColumnName == "weight");
        Assert.Contains(ex.Errors, e => e.ColumnName == "tradable");
    }

    [Fact]
    public void Errors_carry_the_line_number()
    {
        // 줄 번호가 없으면 수천 줄짜리 표에서 어디를 고쳐야 하는지 알 수 없다.
        const string Csv = """
            id,name,price,weight,tradable,note
            a,이름,10,1.0,true,
            b,이름,망가짐,1.0,true,
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), Csv));

        StaticTableError error = Assert.Single(ex.Errors);
        Assert.Equal(3, error.Line);
        Assert.Contains("3행", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_column_in_header_fails()
    {
        const string Csv = """
            id,name,price,weight
            a,이름,10,1.0
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), Csv));

        Assert.Contains(ex.Errors, e => e.ColumnName == "tradable");
    }

    [Fact]
    public void Empty_required_value_fails()
    {
        const string Csv = """
            id,name,price,weight,tradable,note
            a,,10,1.0,true,
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), Csv));

        Assert.Contains(ex.Errors, e => e.ColumnName == "name");
    }

    [Fact]
    public void Duplicate_key_fails_rather_than_silently_winning()
    {
        // ★ 조용히 넘기면 **나중에 쓴 행이 이긴다** — 어느 행이 살아남았는지 아무도 모른다.
        const string Csv = """
            id,name,price,weight,tradable,note
            a,첫번째,10,1.0,true,
            a,두번째,20,2.0,true,
            """;

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), Csv));

        Assert.Contains(ex.Errors, e => e.Message.Contains("중복", StringComparison.Ordinal));
    }

    [Fact]
    public void Int32_range_is_checked()
    {
        string csv = string.Create(
            CultureInfo.InvariantCulture,
            $"""
            id,name,price,weight,tradable,note
            a,이름,{(long)int.MaxValue + 1},1.0,true,
            """);

        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), csv));

        Assert.Contains(ex.Errors, e => e.Message.Contains("범위", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_content_fails_with_a_clear_message()
    {
        StaticTableLoadException ex = Assert.Throws<StaticTableLoadException>(() =>
            CsvStaticTableReader.Read(ItemSchema(), "   \n\n# 주석뿐\n"));

        Assert.Contains(ex.Errors, e => e.Message.Contains("헤더", StringComparison.Ordinal));
    }

    // ── ★ 컬처 불변 파싱 (레거시의 컬처 의존 결함) ──────────────────────────

    [Fact]
    public void Parsing_is_culture_invariant()
    {
        // ★ 같은 파일이 배포 지역에 따라 다르게 읽히면 안 된다. 레거시는 조회마다
        // 컬처 의존 파싱을 했다 — 소수점이 ',' 인 로캘에서 "1.5" 가 어떻게 되는지 생각하면 된다.
        //
        // ⚠ 이름으로 컬처를 만들지 않는다. 이 프로젝트는 InvariantGlobalization=true 라
        // `new CultureInfo("de-DE")` 자체가 던진다(CLAUDE.md 6절). 대신 **소수점 구분자만
        // 바꾼 컬처**를 만들어 같은 조건을 재현한다 — ICU 없이도 성립하고, 재는 대상
        // (파서가 현재 컬처를 보는가)은 정확히 같다.
        //
        // 그리고 이 테스트는 프레임워크가 **라이브러리**이기 때문에 필요하다. 우리 프로세스가
        // invariant 라도, 이 어셈블리를 쓰는 앱은 그렇지 않을 수 있다.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
            commaDecimal.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = commaDecimal;

            StaticTableSchema schema = ItemSchema();
            StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);

            table.TryGetRow("shield", out int row);
            schema.TryGetOrdinal("weight", out int weight);

            Assert.Equal(5.25, table.GetDouble(row, weight));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── 조회 계약 ───────────────────────────────────────────────────────────

    [Fact]
    public void Reading_a_column_as_the_wrong_type_is_a_caller_bug()
    {
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);
        schema.TryGetOrdinal("name", out int name);

        // 데이터 문제가 아니라 호출자 버그다 — 데이터 문제는 전부 로딩에서 걸러졌다.
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetInt32(0, name));
    }

    [Fact]
    public void Out_of_range_row_or_ordinal_throws()
    {
        StaticTable table = CsvStaticTableReader.Read(ItemSchema(), ValidCsv);

        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetString(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetString(table.RowCount, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetString(0, 99));
    }

    [Fact]
    public void Unknown_key_is_not_found()
    {
        StaticTable table = CsvStaticTableReader.Read(ItemSchema(), ValidCsv);

        Assert.False(table.TryGetRow("없는키", out _));
    }

    [Fact]
    public void Int32_column_can_be_read_as_Int64()
    {
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);
        table.TryGetRow("sword", out int row);
        schema.TryGetOrdinal("price", out int price);

        Assert.Equal(100L, table.GetInt64(row, price));
    }

    // ── 스키마 조립 검증 ────────────────────────────────────────────────────

    [Fact]
    public void Schema_rejects_duplicate_column_names()
    {
        Assert.Throws<ArgumentException>(() => new StaticTableSchema(
            "T", "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("id", StaticTableColumnType.Int32),
            ]));
    }

    [Fact]
    public void Schema_rejects_a_key_that_is_not_a_column()
    {
        Assert.Throws<ArgumentException>(() => new StaticTableSchema(
            "T", "없는열", [new StaticTableColumn("id", StaticTableColumnType.String)]));
    }

    [Fact]
    public void Schema_rejects_no_columns()
    {
        Assert.Throws<ArgumentException>(() => new StaticTableSchema("T", "id", []));
    }

    // ── 스레드 규약 ─────────────────────────────────────────────────────────

    [Fact]
    public void Table_is_safe_to_read_concurrently()
    {
        // 여러 파티션 워커가 같은 테이블을 동시에 읽는 것이 기본 사용 형태다.
        StaticTableSchema schema = ItemSchema();
        StaticTable table = CsvStaticTableReader.Read(schema, ValidCsv);
        schema.TryGetOrdinal("price", out int price);

        int[] results = new int[64];
        using Barrier barrier = new(results.Length);

        System.Threading.Tasks.Parallel.For(0, results.Length, i =>
        {
            barrier.SignalAndWait();
            table.TryGetRow("sword", out int row);
            results[i] = table.GetInt32(row, price);
        });

        Assert.True(results.All(static value => value == 100));
    }
}
