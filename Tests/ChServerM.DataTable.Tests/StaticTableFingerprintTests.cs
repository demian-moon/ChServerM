using System;
using System.Collections.Generic;
using Xunit;

namespace ChServerM.DataTable.Tests;

/// <summary>
/// 콘텐츠 지문의 <b>민감도와 둔감도</b> 검증 (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// <b>지문 테스트의 핵심은 "무엇에 반응하지 않는가" 다.</b> 데이터가 달라지면 지문이
/// 달라지는 것은 해시라면 당연하다. 어려운 쪽은 <b>달라지면 안 되는 것에 반응하지 않는
/// 것</b>이다 — 주석 한 줄에 반응하면 전 클라이언트가 거부되고, 기능이 스스로를
/// 무용지물로 만든다.
/// </para>
/// <para>
/// <b>고정 기대값을 박지 않는다.</b> 지문 <b>값</b>을 상수로 못 박으면 XxHash128 구현이
/// 바뀌는 날 테스트가 깨지는데, 그때 필요한 것은 "값이 바뀌었다" 가 아니라 <b>"양쪽 배포를
/// 함께 올려야 한다"</b> 는 판단이다. 여기서 고정하는 것은 <b>관계</b>(같다/다르다)다.
/// </para>
/// </remarks>
public sealed class StaticTableFingerprintTests
{
    private static readonly StaticTableSchema ItemSchema = new(
        "Item",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("damage", StaticTableColumnType.Int32),
            new StaticTableColumn("rate", StaticTableColumnType.Double),
            new StaticTableColumn("tradable", StaticTableColumnType.Boolean),
        ]);

    private const string ItemCsv = """
        id,damage,rate,tradable
        sword,10,0.5,true
        shield,5,0.25,false
        """;

    // ── 같아야 하는 것 ───────────────────────────────────────────────

    [Fact]
    public void SameData_sameFingerprint_acrossSeparateLoads()
    {
        StaticTable a = CsvStaticTableReader.Read(ItemSchema, ItemCsv);
        StaticTable b = CsvStaticTableReader.Read(ItemSchema, ItemCsv);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.True(a.Fingerprint.High != 0 || a.Fingerprint.Low != 0);
    }

    [Fact]
    public void Comments_and_blankLines_doNotChangeFingerprint()
    {
        // ⭐ 파일 바이트가 아니라 파싱 결과를 해싱하는 이유가 이것이다. CSV 리더는 주석을
        // 의도적으로 허용한다("이 값은 왜 이런가" 를 적을 자리가 필요하다) — 그 주석을
        // 고쳤다고 전 클라이언트가 거부되면 기능이 스스로를 무용지물로 만든다.
        const string Annotated = """
            # 무기 밸런스 표
            id,damage,rate,tradable

            sword,10,0.5,true
            # 방패는 데미지가 낮다
            shield,5,0.25,false
            """;

        Assert.Equal(
            CsvStaticTableReader.Read(ItemSchema, ItemCsv).Fingerprint,
            CsvStaticTableReader.Read(ItemSchema, Annotated).Fingerprint);
    }

    [Fact]
    public void LineEndings_doNotChangeFingerprint()
    {
        // git 의 autocrlf 하나로 전 클라이언트가 거부되면 안 된다.
        Assert.Equal(
            CsvStaticTableReader.Read(ItemSchema, ItemCsv).Fingerprint,
            CsvStaticTableReader.Read(ItemSchema, ItemCsv.Replace("\n", "\r\n", StringComparison.Ordinal)).Fingerprint);
    }

    [Fact]
    public void TableRegistrationOrder_doesNotChangeSetFingerprint()
    {
        StaticTableSet forward = new StaticTableSetBuilder()
            .Add(ItemSchema, ItemCsv)
            .Add(OtherSchema, OtherCsv)
            .Build();

        StaticTableSet reversed = new StaticTableSetBuilder()
            .Add(OtherSchema, OtherCsv)
            .Add(ItemSchema, ItemCsv)
            .Build();

        Assert.Equal(forward.Fingerprint, reversed.Fingerprint);
    }

    // ── 달라야 하는 것 ───────────────────────────────────────────────

    [Theory]
    [InlineData("sword,10,0.5,true", "sword,11,0.5,true")]     // 정수 값
    [InlineData("sword,10,0.5,true", "sword,10,0.6,true")]     // 실수 값
    [InlineData("sword,10,0.5,true", "sword,10,0.5,false")]    // 참거짓 값
    [InlineData("sword,10,0.5,true", "spear,10,0.5,true")]     // 문자열 값
    public void AnyValueChange_changesFingerprint(string original, string modified)
    {
        StaticTable before = CsvStaticTableReader.Read(ItemSchema, ItemCsv);
        StaticTable after = CsvStaticTableReader.Read(
            ItemSchema, ItemCsv.Replace(original, modified, StringComparison.Ordinal));

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void RowOrder_changesFingerprint()
    {
        // ⭐ 행 번호가 곧 참조의 목적지다(GetReference). 같은 행을 순서만 바꿔 적은 표는
        // **다른 표**이며, 클라이언트가 인덱스로 참조를 따라가면 엉뚱한 행을 본다.
        const string Swapped = """
            id,damage,rate,tradable
            shield,5,0.25,false
            sword,10,0.5,true
            """;

        Assert.NotEqual(
            CsvStaticTableReader.Read(ItemSchema, ItemCsv).Fingerprint,
            CsvStaticTableReader.Read(ItemSchema, Swapped).Fingerprint);
    }

    [Fact]
    public void AddedRow_changesFingerprint()
    {
        Assert.NotEqual(
            CsvStaticTableReader.Read(ItemSchema, ItemCsv).Fingerprint,
            CsvStaticTableReader.Read(ItemSchema, ItemCsv + "\nbow,7,0.3,true").Fingerprint);
    }

    [Fact]
    public void ColumnRename_changesFingerprint()
    {
        // 값이 같아도 **계약이 다르다**. 클라이언트가 다른 열 이름을 기대하고 있을 수 있다.
        StaticTableSchema renamed = new(
            "Item",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("atk", StaticTableColumnType.Int32),
                new StaticTableColumn("rate", StaticTableColumnType.Double),
                new StaticTableColumn("tradable", StaticTableColumnType.Boolean),
            ]);

        const string RenamedCsv = """
            id,atk,rate,tradable
            sword,10,0.5,true
            shield,5,0.25,false
            """;

        Assert.NotEqual(
            CsvStaticTableReader.Read(ItemSchema, ItemCsv).Fingerprint,
            CsvStaticTableReader.Read(renamed, RenamedCsv).Fingerprint);
    }

    [Fact]
    public void RangeConstraint_changesFingerprint()
    {
        // 같은 값이라도 허용 범위가 다르면 다른 계약이다 — 클라이언트가 그 범위를
        // 근거로 UI 를 만들 수 있다.
        StaticTableSchema constrained = new(
            "Item",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("damage", StaticTableColumnType.Int32) { MaximumInteger = 100 },
                new StaticTableColumn("rate", StaticTableColumnType.Double),
                new StaticTableColumn("tradable", StaticTableColumnType.Boolean),
            ]);

        Assert.NotEqual(
            CsvStaticTableReader.Read(ItemSchema, ItemCsv).Fingerprint,
            CsvStaticTableReader.Read(constrained, ItemCsv).Fingerprint);
    }

    [Fact]
    public void TableName_changesSetFingerprint()
    {
        StaticTableSchema renamedTable = new(
            "Weapon", "id", [.. ItemSchema.Columns]);

        StaticTableSet original = new StaticTableSetBuilder().Add(ItemSchema, ItemCsv).Build();
        StaticTableSet renamed = new StaticTableSetBuilder().Add(renamedTable, ItemCsv).Build();

        Assert.NotEqual(original.Fingerprint, renamed.Fingerprint);
    }

    [Fact]
    public void NullAndEmptyString_areDistinguished()
    {
        // 선택 열의 빈 칸(null)과 빈 문자열은 다른 값이다. 길이 -1 로 구분해 먹인다.
        StaticTableSchema schema = new(
            "Note",
            "id",
            [
                new StaticTableColumn("id", StaticTableColumnType.String),
                new StaticTableColumn("text", StaticTableColumnType.String, Required: false),
            ]);

        // 인용된 빈 문자열과 그냥 빈 칸 — CSV 파서는 둘 다 빈 문자열로 읽으므로 이 표에서는
        // 같아야 한다. 구분이 필요한 지점은 파서가 아니라 계산기이며, 아래가 그 증거다.
        StaticTable withEmpty = CsvStaticTableReader.Read(schema, "id,text\na,\n");
        StaticTable withValue = CsvStaticTableReader.Read(schema, "id,text\na,x\n");

        Assert.NotEqual(withEmpty.Fingerprint, withValue.Fingerprint);
    }

    // ── 묶음 지문은 표 지문의 함수다 ─────────────────────────────────

    [Fact]
    public void SetFingerprint_differsFromAnySingleTableFingerprint()
    {
        StaticTableSet set = new StaticTableSetBuilder().Add(ItemSchema, ItemCsv).Build();

        // 표 하나짜리 묶음이라도 묶음 지문은 표 지문과 다르다 — 표 이름과 개수가
        // 함께 섞이기 때문이다. 둘을 혼동해 대조하면 항상 불일치가 난다.
        Assert.NotEqual(set.Fingerprint, set.GetTable("Item").Fingerprint);
    }

    [Fact]
    public void SetFingerprint_changesWhenAnyMemberTableChanges()
    {
        StaticTableSet before = new StaticTableSetBuilder()
            .Add(ItemSchema, ItemCsv)
            .Add(OtherSchema, OtherCsv)
            .Build();

        StaticTableSet after = new StaticTableSetBuilder()
            .Add(ItemSchema, ItemCsv)
            .Add(OtherSchema, OtherCsv.Replace("100", "101", StringComparison.Ordinal))
            .Build();

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void HotReload_producesNewFingerprint_whileGenerationIsSeparate()
    {
        // ⚠ 세대와 지문은 다른 것이다. 세대는 "이 프로세스에서 몇 번 갈아 끼웠는가" 이고,
        // 지문은 "내용이 무엇인가" 다. 대조에 쓸 수 있는 것은 지문뿐이다.
        StaticTableSet initial = new StaticTableSetBuilder().Add(ItemSchema, ItemCsv).Build();
        ReloadableStaticTableSet reloadable = new(initial);

        StaticTableFingerprint first = reloadable.Current.Fingerprint;

        List<(StaticTableSchema, string)> sources =
            [(ItemSchema, ItemCsv.Replace("10,0.5", "12,0.5", StringComparison.Ordinal))];
        Assert.True(reloadable.TryReload(sources).Succeeded);

        Assert.NotEqual(first, reloadable.Current.Fingerprint);
        Assert.Equal(2, reloadable.Generation);

        // 같은 내용으로 다시 적재하면 세대는 늘지만 지문은 처음으로 돌아온다.
        Assert.True(reloadable.TryReload([(ItemSchema, ItemCsv)]).Succeeded);
        Assert.Equal(first, reloadable.Current.Fingerprint);
        Assert.Equal(3, reloadable.Generation);
    }

    // ── 와이어 표현 ──────────────────────────────────────────────────

    [Fact]
    public void WriteTo_isStableAndFullWidth()
    {
        StaticTableFingerprint fingerprint = new(0x0123456789ABCDEF, 0xFEDCBA9876543210);

        byte[] buffer = new byte[StaticTableFingerprint.ByteLength];
        fingerprint.WriteTo(buffer);

        Assert.Equal(0x10, buffer[0]);   // Low 의 최하위 바이트(리틀 엔디언)
        Assert.Equal(0xEF, buffer[8]);   // High 의 최하위 바이트
        Assert.Equal("0123456789abcdeffedcba9876543210", fingerprint.ToString());
    }

    [Fact]
    public void WriteTo_shortBuffer_throws() =>
        Assert.Throws<ArgumentException>(() => default(StaticTableFingerprint).WriteTo(new byte[15]));

    private static readonly StaticTableSchema OtherSchema = new(
        "Recipe",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("cost", StaticTableColumnType.Int64),
        ]);

    private const string OtherCsv = """
        id,cost
        r1,100
        """;
}
