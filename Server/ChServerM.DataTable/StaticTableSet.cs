using System;
using System.Collections.Generic;

namespace ChServerM.DataTable;

/// <summary>
/// 함께 로딩·검증된 테이블 묶음 — <b>참조 무결성이 보장된 상태</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 참조 무결성은 <b>테이블 하나만 봐서는 판정할 수 없다.</b> "이 레시피가
/// 가리키는 아이템이 실재하는가" 는 두 표를 함께 봐야 답이 나온다. 그래서 로딩의 단위를
/// 파일 하나가 아니라 <b>묶음</b>으로 올린다.
/// </para>
/// <para>
/// <b>⚠ 검증과 인덱스 변환은 같은 패스다.</b> 참조가 유효한지 확인하려면 어차피 대상 행을
/// 찾아야 하고, 찾은 김에 <b>행 번호를 저장</b>해 두면 조회 때마다 키로 다시 찾지 않아도
/// 된다. 레거시 <c>ConvertColToIndexRefMetaM</c> 이 하던 일이며 승계 판정이 🟢 다
/// (docs/legacy/11-data-table). 검증만 하고 버리는 것이 오히려 낭비다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 만든 뒤 불변이며 스레드 안전하다. 갱신은 <b>묶음 전체를 새로 만들어
/// 교체</b>한다 — 표 하나만 바꾸면 참조 무결성이 그 순간 깨질 수 있기 때문이다.
/// </para>
/// </remarks>
public sealed class StaticTableSet
{
    private readonly Dictionary<string, StaticTable> _tables;

    internal StaticTableSet(Dictionary<string, StaticTable> tables) => _tables = tables;

    /// <summary>묶음에 든 테이블 수.</summary>
    public int Count => _tables.Count;

    /// <summary>이름으로 테이블을 찾는다.</summary>
    /// <param name="name">테이블 이름(스키마의 이름).</param>
    /// <param name="table">테이블.</param>
    /// <returns>찾았으면 <see langword="true"/>.</returns>
    public bool TryGetTable(string name, out StaticTable? table) => _tables.TryGetValue(name, out table);

    /// <summary>이름으로 테이블을 얻는다.</summary>
    /// <param name="name">테이블 이름.</param>
    /// <returns>테이블.</returns>
    /// <exception cref="KeyNotFoundException">그 이름의 테이블이 없다.</exception>
    public StaticTable GetTable(string name) => _tables[name];
}

/// <summary>
/// 여러 테이블을 함께 로딩하고 <b>참조를 검증·해결</b>하는 조립기.
/// </summary>
/// <remarks>
/// <para>
/// <b>사용법</b> — 표를 전부 넣고 <see cref="Build"/> 를 한 번 부른다.
/// </para>
/// <code>
///   var set = new StaticTableSetBuilder()
///       .Add(itemSchema, itemCsv)
///       .Add(recipeSchema, recipeCsv)   // recipe.itemId → Item 참조
///       .Build();                       // 여기서 참조 검증 + 인덱스 변환
/// </code>
/// <para>
/// <b>⚠ 개별 표의 오류와 참조 오류를 함께 보고한다.</b> 표 A 가 깨졌다고 먼저 던지면
/// 사용자는 A 를 고친 뒤에야 B 의 문제를 알게 된다 — 로딩 오류를 한 번에 보여 주는
/// <see cref="StaticTableLoadException"/> 의 취지가 묶음 단위에서도 유지돼야 한다.
/// </para>
/// <para><b>스레드 규약.</b> 조립기는 스레드 안전하지 않다. 조립은 기동 시 한 스레드에서 한다.</para>
/// </remarks>
public sealed class StaticTableSetBuilder
{
    private readonly List<(StaticTableSchema Schema, string Content)> _sources = [];

    /// <summary>CSV 내용을 묶음에 더한다.</summary>
    /// <param name="schema">스키마.</param>
    /// <param name="csvContent">CSV 내용.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    public StaticTableSetBuilder Add(StaticTableSchema schema, string csvContent)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(csvContent);

        _sources.Add((schema, csvContent));
        return this;
    }

    /// <summary>전부 로딩하고 참조를 검증·해결한다.</summary>
    /// <returns>참조 무결성이 보장된 묶음.</returns>
    /// <exception cref="StaticTableLoadException">어느 표든 오류가 있으면 <b>전부 모아</b> 던진다.</exception>
    public StaticTableSet Build()
    {
        Dictionary<string, StaticTable> tables = new(_sources.Count, StringComparer.Ordinal);
        List<StaticTableError> errors = [];

        foreach ((StaticTableSchema schema, string content) in _sources)
        {
            if (tables.ContainsKey(schema.Name))
            {
                errors.Add(new StaticTableError(0, null, $"테이블 이름이 중복된다: '{schema.Name}'"));
                continue;
            }

            try
            {
                tables[schema.Name] = CsvStaticTableReader.Read(schema, content);
            }
            catch (StaticTableLoadException ex)
            {
                // ⚠ 여기서 다시 던지지 않는다 — 남은 표의 오류도 함께 보여 줘야 한다.
                foreach (StaticTableError error in ex.Errors)
                {
                    errors.Add(error with { Message = $"[{schema.Name}] {error.Message}" });
                }
            }
        }

        // 참조 해결은 모든 표가 로딩된 뒤에만 가능하다. 하나라도 실패했으면 대상이 없어
        // "참조 대상 없음" 오류가 쏟아지므로 여기서 끊는다.
        if (errors.Count > 0)
        {
            throw new StaticTableLoadException("<묶음>", errors);
        }

        foreach (StaticTable table in tables.Values)
        {
            ResolveReferences(table, tables, errors);
        }

        if (errors.Count > 0)
        {
            throw new StaticTableLoadException("<묶음>", errors);
        }

        return new StaticTableSet(tables);
    }

    /// <summary>한 표의 참조 열을 검증하고 대상 행 번호로 변환한다.</summary>
    private static void ResolveReferences(
        StaticTable table, Dictionary<string, StaticTable> tables, List<StaticTableError> errors)
    {
        StaticTableSchema schema = table.Schema;

        for (int ordinal = 0; ordinal < schema.Columns.Count; ordinal++)
        {
            StaticTableColumn column = schema.Columns[ordinal];
            if (column.ReferencesTable is not { } targetName)
            {
                continue;
            }

            if (!tables.TryGetValue(targetName, out StaticTable? target))
            {
                errors.Add(new StaticTableError(
                    0, column.Name, $"[{schema.Name}] 참조 대상 테이블 '{targetName}' 이 묶음에 없다."));
                continue;
            }

            int[] resolved = new int[table.RowCount];

            for (int row = 0; row < table.RowCount; row++)
            {
                string? value = table.GetString(row, ordinal);

                if (string.IsNullOrEmpty(value))
                {
                    // 선택 참조는 비어 있을 수 있다. 필수라면 로딩 단계가 이미 걸렀다.
                    resolved[row] = StaticTable.NoReference;
                    continue;
                }

                if (target.TryGetRow(value, out int targetRow))
                {
                    resolved[row] = targetRow;
                }
                else
                {
                    resolved[row] = StaticTable.NoReference;
                    errors.Add(new StaticTableError(
                        row, column.Name,
                        $"[{schema.Name}] 참조가 '{targetName}' 에 없다: '{value}' (행 인덱스 {row})"));
                }
            }

            table.SetResolvedReferences(ordinal, resolved);
        }
    }
}
