using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.DataTable;
using Xunit;

namespace ChServerM.DataTable.Tests;

/// <summary>
/// 핫 리로드 — <b>검증에 성공했을 때만 바뀐다</b>는 것이 이 타입의 전부다 (Phase 14).
/// </summary>
public sealed class ReloadableStaticTableSetTests
{
    private static StaticTableSchema ItemSchema() => new(
        "Item",
        "id",
        [
            new StaticTableColumn("id", StaticTableColumnType.String),
            new StaticTableColumn("price", StaticTableColumnType.Int32) { MinimumInteger = 0 },
        ]);

    private const string V1 = """
        id,price
        sword,100
        """;

    private const string V2 = """
        id,price
        sword,250
        """;

    private static StaticTableSet Load(string csv) =>
        new StaticTableSetBuilder().Add(ItemSchema(), csv).Build();

    private static int Price(StaticTableSet set)
    {
        StaticTable items = set.GetTable("Item");
        ItemSchema().TryGetOrdinal("price", out int price);
        items.TryGetRow("sword", out int row);
        return items.GetInt32(row, price);
    }

    // ── 성공 경로 ───────────────────────────────────────────────────────────

    [Fact]
    public void Successful_reload_swaps_the_set_and_bumps_the_generation()
    {
        ReloadableStaticTableSet reloadable = new(Load(V1));
        Assert.Equal(100, Price(reloadable.Current));
        Assert.Equal(1, reloadable.Generation);

        StaticTableReloadResult result = reloadable.TryReload(() => Load(V2));

        Assert.True(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.Equal(2, result.Generation);
        Assert.Equal(2, reloadable.Generation);
        Assert.Equal(250, Price(reloadable.Current));
    }

    [Fact]
    public void Csv_overload_reloads_from_sources()
    {
        ReloadableStaticTableSet reloadable = new(Load(V1));

        StaticTableReloadResult result = reloadable.TryReload(
            new List<(StaticTableSchema, string)> { (ItemSchema(), V2) });

        Assert.True(result.Succeeded);
        Assert.Equal(250, Price(reloadable.Current));
    }

    // ── ⭐ 실패 경로가 이 타입의 존재 이유다 ────────────────────────────────

    [Fact]
    public void Failed_reload_keeps_serving_the_old_data()
    {
        // ★ 기동과 정반대다 — 기동 검증 실패는 기동 실패지만, 재적재 검증 실패는
        // 옛 데이터 유지다. 돌고 있는 서버를 표 오타로 죽이면 안 된다.
        ReloadableStaticTableSet reloadable = new(Load(V1));

        StaticTableReloadResult result = reloadable.TryReload(() => Load("id,price\nsword,가격아님\n"));

        Assert.False(result.Succeeded);
        Assert.Equal(100, Price(reloadable.Current)); // 그대로 서비스한다
        Assert.Equal(1, reloadable.Generation);       // 세대도 늘지 않는다
        Assert.Equal(1, result.Generation);
    }

    [Fact]
    public void Failure_carries_the_errors_so_an_operator_can_fix_them()
    {
        ReloadableStaticTableSet reloadable = new(Load(V1));

        StaticTableReloadResult result = reloadable.TryReload(() => Load("id,price\nsword,-5\n"));

        Assert.NotNull(result.Failure);
        Assert.Contains(result.Failure!.Errors, e => e.Message.Contains("최솟값", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_reload_does_not_block_a_later_good_one()
    {
        ReloadableStaticTableSet reloadable = new(Load(V1));

        Assert.False(reloadable.TryReload(() => Load("id,price\nsword,틀림\n")).Succeeded);
        Assert.True(reloadable.TryReload(() => Load(V2)).Succeeded);

        Assert.Equal(250, Price(reloadable.Current));
        Assert.Equal(2, reloadable.Generation); // 실패는 세대를 소비하지 않았다
    }

    [Fact]
    public void Environment_errors_are_not_swallowed()
    {
        // ★ 파일이 없는 것은 데이터 오류가 아니라 환경 오류다. 그것까지 삼키면
        // "재적재가 계속 실패하는데 아무도 모르는" 상태가 된다.
        ReloadableStaticTableSet reloadable = new(Load(V1));

        Assert.Throws<System.IO.FileNotFoundException>(() =>
            reloadable.TryReload(new Func<StaticTableSet>(
                () => throw new System.IO.FileNotFoundException("balance.csv"))));
    }

    // ── 동시성 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readers_see_a_consistent_set_while_reloads_run()
    {
        // ★ 불변이 동시성 문제를 미리 없앤다 — 읽는 도중 교체가 일어나도 이미 받은
        // 묶음은 계속 유효하다. 읽기 쪽에는 어떤 동기화도 없다.
        ReloadableStaticTableSet reloadable = new(Load(V1));
        using CancellationTokenSource cts = new();

        Task reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                int price = Price(reloadable.Current);
                Assert.True(price is 100 or 250); // 중간 상태는 존재하지 않는다
            }
        });

        for (int i = 0; i < 200; i++)
        {
            reloadable.TryReload(() => Load(i % 2 == 0 ? V2 : V1));
        }

        await cts.CancelAsync();
        await reader;

        Assert.Equal(201, reloadable.Generation);
    }

    [Fact]
    public async Task Concurrent_reloads_are_serialized()
    {
        ReloadableStaticTableSet reloadable = new(Load(V1));

        Task[] writers = new Task[8];
        for (int w = 0; w < writers.Length; w++)
        {
            writers[w] = Task.Run(() =>
            {
                for (int i = 0; i < 25; i++)
                {
                    reloadable.TryReload(() => Load(V2));
                }
            });
        }

        await Task.WhenAll(writers);

        // 200 번 전부 성공했고 세대가 정확히 그만큼 늘었다 — 잃어버린 갱신이 없다.
        Assert.Equal(201, reloadable.Generation);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ReloadableStaticTableSet(null!));

        ReloadableStaticTableSet reloadable = new(Load(V1));
        Assert.Throws<ArgumentNullException>(() => reloadable.TryReload((Func<StaticTableSet>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            reloadable.TryReload((IReadOnlyList<(StaticTableSchema, string)>)null!));
    }
}
