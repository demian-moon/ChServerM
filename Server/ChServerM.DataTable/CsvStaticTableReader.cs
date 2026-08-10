using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ChServerM.DataTable;

/// <summary>
/// CSV 를 읽어 <see cref="StaticTable"/> 로 만든다 — <b>로딩 시점에 전수 검증</b>한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 CSV 인가.</b> 사람이 읽고 <c>git diff</c> 가 되는 형식이어야 밸런스 표를 리뷰할 수
/// 있다. Excel 은 <b>빌드 타임 변환 도구</b>의 입력이지 런타임의 입력이 아니다 —
/// 레거시는 Excel/ODBC 파서 3,093줄을 런타임 어셈블리에 넣어 두고 <b>한 번도 호출하지
/// 않았다</b>(docs/legacy/11-data-table, 절대 옮기면 안 되는 것 4번).
/// </para>
///
/// <para>
/// <b>⚠ 첫 오류에서 멈추지 않는다.</b> 발견한 오류를 <b>전부 모아</b>
/// <see cref="StaticTableLoadException"/> 로 던진다. 테이블은 사람이 손으로 고치는
/// 데이터이므로, 오류를 하나씩 알려 주면 "고치고 → 다시 띄우고" 를 오류 수만큼 반복하게 된다.
/// </para>
///
/// <para>
/// <b>⚠ 파싱은 컬처 불변이다.</b> <see cref="CultureInfo.InvariantCulture"/> 로 고정한다 —
/// 레거시는 조회마다 컬처 의존 파싱을 했고, 그러면 <b>같은 파일이 배포 지역에 따라 다르게
/// 읽힌다</b>(소수점이 <c>,</c> 인 로캘에서 <c>1.5</c> 가 어떻게 되는지 생각하면 된다).
/// </para>
///
/// <para>
/// <b>지원하는 CSV 범위</b>: 쉼표 구분, 큰따옴표 인용(안에서 <c>""</c> 는 따옴표 하나),
/// 인용 안의 줄바꿈은 <b>지원하지 않는다</b>. 데이터 테이블에 여러 줄 값이 필요하면 그것은
/// 대개 스키마 설계가 잘못된 신호다 — 지원 범위를 좁혀 파서를 작고 예측 가능하게 유지한다.
/// </para>
///
/// <para><b>스레드 규약.</b> 상태가 없는 정적 클래스다.</para>
/// </remarks>
public static class CsvStaticTableReader
{
    /// <summary>파일에서 읽는다.</summary>
    /// <param name="schema">스키마.</param>
    /// <param name="path">CSV 경로.</param>
    /// <returns>검증을 통과한 테이블.</returns>
    /// <exception cref="StaticTableLoadException">형식·검증 오류가 하나라도 있다.</exception>
    /// <remarks>
    /// <b>경로를 직접 조립하지 않는다.</b> 호출자가 <see cref="Path.Combine(string, string)"/>
    /// 으로 만든 경로를 받는다 — 레거시가 <c>@"SysTable\ServerConfig.smt"</c> 처럼 구분자를
    /// 하드코딩해 <b>Linux 에서 파일을 찾지 못한</b> 것이 크로스 플랫폼 차단 요인이었다.
    /// </remarks>
    public static StaticTable ReadFile(StaticTableSchema schema, string path)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return Read(schema, File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>문자열에서 읽는다.</summary>
    /// <param name="schema">스키마.</param>
    /// <param name="content">CSV 내용.</param>
    /// <returns>검증을 통과한 테이블.</returns>
    /// <exception cref="StaticTableLoadException">형식·검증 오류가 하나라도 있다.</exception>
    public static StaticTable Read(StaticTableSchema schema, string content)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(content);

        List<StaticTableError> errors = [];
        string[] lines = content.Split('\n');

        int headerLine = FindHeader(lines);
        if (headerLine < 0)
        {
            throw new StaticTableLoadException(
                schema.Name, [new StaticTableError(1, null, "헤더 줄이 없다(파일이 비었다).")]);
        }

        int[] sourceOrdinal = MapHeader(schema, lines[headerLine], headerLine + 1, errors);

        // 열 매핑이 깨졌으면 값 검증은 의미가 없다 — 전부 "열 없음" 오류로 도배된다.
        if (errors.Count > 0)
        {
            throw new StaticTableLoadException(schema.Name, errors);
        }

        return BuildTable(schema, lines, headerLine, sourceOrdinal, errors);
    }

    private static int FindHeader(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsSkippable(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>빈 줄과 주석(<c>#</c>)은 건너뛴다.</summary>
    /// <remarks>
    /// 주석을 허용하는 이유: 밸런스 표에는 "이 값은 왜 이런가" 를 적을 자리가 필요하다.
    /// 적을 자리가 없으면 그 설명은 사라지고, 다음 사람은 값을 바꿔도 되는지 알 수 없다.
    /// </remarks>
    private static bool IsSkippable(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    /// <summary>헤더를 스키마와 대조해 <c>스키마 서수 → CSV 열 위치</c> 를 만든다.</summary>
    private static int[] MapHeader(
        StaticTableSchema schema, string headerLine, int lineNumber, List<StaticTableError> errors)
    {
        List<string> header = ParseLine(headerLine);
        int[] sourceOrdinal = new int[schema.Columns.Count];

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            sourceOrdinal[i] = header.IndexOf(schema.Columns[i].Name);
            if (sourceOrdinal[i] < 0)
            {
                errors.Add(new StaticTableError(
                    lineNumber, schema.Columns[i].Name, "스키마가 요구하는 열이 헤더에 없다."));
            }
        }

        // 스키마에 없는 열은 오류가 아니다 — 테이블이 프레임워크보다 많은 정보를 가질 수 있고,
        // 그것을 오류로 만들면 스키마를 늘릴 때마다 배포 순서가 문제가 된다.
        return sourceOrdinal;
    }

    private static StaticTable BuildTable(
        StaticTableSchema schema,
        string[] lines,
        int headerLine,
        int[] sourceOrdinal,
        List<StaticTableError> errors)
    {
        int columnCount = schema.Columns.Count;
        int[] typedIndex = new int[columnCount];
        int stringCount = 0, integerCount = 0, doubleCount = 0, booleanCount = 0;

        for (int i = 0; i < columnCount; i++)
        {
            typedIndex[i] = schema.Columns[i].Type switch
            {
                StaticTableColumnType.String => stringCount++,
                StaticTableColumnType.Int32 or StaticTableColumnType.Int64 => integerCount++,
                StaticTableColumnType.Double => doubleCount++,
                StaticTableColumnType.Boolean => booleanCount++,
                _ => 0,
            };
        }

        List<string?[]> rows = [];
        List<int> rowLines = [];

        for (int i = headerLine + 1; i < lines.Length; i++)
        {
            if (IsSkippable(lines[i]))
            {
                continue;
            }

            List<string> fields = ParseLine(lines[i]);
            string?[] row = new string?[columnCount];

            for (int c = 0; c < columnCount; c++)
            {
                int source = sourceOrdinal[c];
                row[c] = source < fields.Count ? fields[source] : null;
            }

            rows.Add(row);
            rowLines.Add(i + 1);
        }

        int rowCount = rows.Count;
        string?[] strings = new string?[rowCount * stringCount];
        long[] integers = new long[rowCount * integerCount];
        double[] doubles = new double[rowCount * doubleCount];
        bool[] booleans = new bool[rowCount * booleanCount];
        Dictionary<string, int> rowByKey = new(rowCount, StringComparer.Ordinal);

        for (int r = 0; r < rowCount; r++)
        {
            int line = rowLines[r];

            for (int c = 0; c < columnCount; c++)
            {
                StaticTableColumn column = schema.Columns[c];
                string? raw = rows[r][c];
                bool empty = string.IsNullOrEmpty(raw);

                if (empty && column.Required)
                {
                    errors.Add(new StaticTableError(line, column.Name, "필수 열이 비어 있다."));
                    continue;
                }

                int slot = (r * TypedCount(column.Type, stringCount, integerCount, doubleCount, booleanCount))
                    + typedIndex[c];

                switch (column.Type)
                {
                    case StaticTableColumnType.String:
                        strings[slot] = empty ? null : raw;
                        break;

                    case StaticTableColumnType.Int32:
                    case StaticTableColumnType.Int64:
                        if (empty)
                        {
                            integers[slot] = 0;
                        }
                        else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                        {
                            if (column.Type == StaticTableColumnType.Int32
                                && (value < int.MinValue || value > int.MaxValue))
                            {
                                errors.Add(new StaticTableError(line, column.Name, $"Int32 범위를 벗어났다: '{raw}'"));
                            }
                            else
                            {
                                integers[slot] = value;
                            }
                        }
                        else
                        {
                            errors.Add(new StaticTableError(line, column.Name, $"정수로 읽을 수 없다: '{raw}'"));
                        }

                        break;

                    case StaticTableColumnType.Double:
                        if (empty)
                        {
                            doubles[slot] = 0;
                        }
                        else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        {
                            doubles[slot] = d;
                        }
                        else
                        {
                            errors.Add(new StaticTableError(line, column.Name, $"실수로 읽을 수 없다: '{raw}'"));
                        }

                        break;

                    case StaticTableColumnType.Boolean:
                        if (empty)
                        {
                            booleans[slot] = false;
                        }
                        else if (TryParseBoolean(raw!, out bool b))
                        {
                            booleans[slot] = b;
                        }
                        else
                        {
                            errors.Add(new StaticTableError(
                                line, column.Name, $"참·거짓으로 읽을 수 없다: '{raw}' (true/false/1/0)"));
                        }

                        break;

                    default:
                        break;
                }
            }

            // 키 중복은 조용히 넘기면 **나중에 쓴 행이 이긴다** — 어느 행이 살아남았는지
            // 아무도 모르는 상태가 되므로 로딩 실패로 만든다.
            string? key = rows[r][schema.KeyOrdinal];
            if (!string.IsNullOrEmpty(key) && !rowByKey.TryAdd(key, r))
            {
                errors.Add(new StaticTableError(
                    line, schema.KeyColumnName, $"키가 중복된다: '{key}' (앞선 행 {rowLines[rowByKey[key]]})"));
            }
        }

        if (errors.Count > 0)
        {
            throw new StaticTableLoadException(schema.Name, errors);
        }

        return new StaticTable(schema, rowCount, strings, integers, doubles, booleans, typedIndex, rowByKey);
    }

    private static int TypedCount(
        StaticTableColumnType type, int strings, int integers, int doubles, int booleans) =>
        type switch
        {
            StaticTableColumnType.String => strings,
            StaticTableColumnType.Int32 or StaticTableColumnType.Int64 => integers,
            StaticTableColumnType.Double => doubles,
            StaticTableColumnType.Boolean => booleans,
            _ => 0,
        };

    private static bool TryParseBoolean(string raw, out bool value)
    {
        // 1/0 을 받는 이유: 표 편집기에서 참·거짓을 숫자로 쓰는 습관이 흔하다.
        switch (raw.Trim().ToUpperInvariant())
        {
            case "TRUE" or "1":
                value = true;
                return true;

            case "FALSE" or "0":
                value = false;
                return true;

            default:
                value = false;
                return false;
        }
    }

    /// <summary>CSV 한 줄을 필드로 나눈다. 큰따옴표 인용과 <c>""</c> 이스케이프를 지원한다.</summary>
    private static List<string> ParseLine(string line)
    {
        List<string> fields = [];
        StringBuilder current = new();
        bool quoted = false;

        // 줄 끝의 CR 을 떨어뜨린다 — CRLF 파일을 '\n' 으로 잘랐기 때문이다.
        ReadOnlySpan<char> span = line.AsSpan().TrimEnd('\r');

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < span.Length && span[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    break;

                default:
                    current.Append(c);
                    break;
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
