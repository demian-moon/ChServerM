using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChServerM.DataTable;

/// <summary>
/// 로딩이 끝난 표 묶음을 <b>바이트로 굽고 되살리는</b> 이식 가능한 형식 —
/// 서버가 들고 있는 표를 클라이언트에 그대로 내려보내기 위한 것.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 지문 대조(ADR-0044)는 "어긋났다" 를 알려 줄 뿐 <b>고쳐 주지는 않는다</b>.
/// 서버가 로딩한 표를 클라이언트가 그대로 받으면 <b>불일치가 원천 차단</b>된다 —
/// 레거시가 실제로 그렇게 했고 승계 판정이 🟢 다(docs/legacy/11-data-table).
/// 클라이언트는 <b>데이터 파일을 아예 갖지 않아도 되고</b>, 서버가 값의 단일 출처가 된다.
/// </para>
///
/// <para>
/// <b>⚠ 스키마는 와이어에 실리지만 <see cref="Read"/> 는 로컬 스키마를 쓴다.</b>
/// 실린 스키마는 <b>대조용</b>이다. 값을 열 우선(column-major)으로 굽기 때문에, 스키마가
/// 한 칸이라도 어긋난 채로 읽으면 <b>전부 엉뚱한 열로 조용히 해석</b>된다 — 그것이 이
/// 형식에서 가장 위험한 실패다. 그래서 열 이름·종류·필수 여부·순서를 전부 대조하고
/// 하나라도 다르면 거부한다.
/// </para>
/// <para>
/// 로컬 스키마를 쓰는 두 번째 이유는 <b>생성된 접근자와의 호환</b>이다. 강타입 뷰는 스키마
/// <b>참조 동일성</b>으로 서수 일치를 보장하므로(ADR-0043), 와이어에서 만든 새 스키마
/// 인스턴스로 표를 세우면 <c>ItemRow.Table</c> 이 그 표를 거부한다. 로컬 인스턴스를 그대로
/// 쓰면 받은 표에도 생성된 접근자를 쓸 수 있다.
/// </para>
///
/// <para>
/// <b>⚠ 지문이 보존된다는 것이 이 형식의 합격 기준이다.</b> 굽고 되살린 묶음의
/// <see cref="StaticTableSet.Fingerprint"/> 는 원본과 같아야 한다. 같지 않다면 값이나 순서가
/// 어딘가에서 뒤틀린 것이고, 그 사실을 <b>대조 한 번으로</b> 잡을 수 있다.
/// </para>
///
/// <para>
/// <b>참조 해결 결과는 싣지 않는다.</b> 그것은 값에서 유도되는 것이라 따로 실으면 값과
/// 어긋난 상태를 만들 수 있다. 받는 쪽에서 <b>로딩과 같은 패스로 다시 푼다</b>.
/// </para>
///
/// <para>
/// <b>형식은 버전을 갖는다.</b> 매직(<c>CHSMTBL\0</c>) + 버전으로 시작하므로 다른 페이로드를
/// 이 형식으로 오독하지 않고, 형식을 바꿔야 할 때 버전으로 갈래를 낼 수 있다 —
/// 핸드셰이크와 달리 이 형식은 <b>동결이 아니다</b>(양쪽이 같은 배포에서 나온다는 전제가
/// 지문 게이트로 이미 강제되기 때문이다).
/// </para>
///
/// <para>
/// <b>압축·가변 길이 정수를 쓰지 않는다.</b> 정수를 8바이트 고정으로 쓰면 varint 보다 크지만,
/// <b>측정 없는 최적화 금지</b>(CLAUDE.md 2절)이고 페이로드 압축은 이미 축으로 존재한다
/// (<c>IPayloadCodec</c>). 여기서 한 번 더 압축하면 두 계층이 같은 일을 한다.
/// </para>
///
/// <para><b>스레드 규약.</b> 상태 없는 정적 클래스.</para>
/// </remarks>
public static class StaticTableSnapshot
{
    /// <summary>형식 매직. 다른 페이로드를 이 형식으로 오독하지 않기 위한 것.</summary>
    private static readonly byte[] Magic = "CHSMTBL\0"u8.ToArray();

    /// <summary>현재 형식 버전.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>문자열 값이 <see langword="null"/> 임을 뜻하는 길이.</summary>
    private const int NullLength = -1;

    // ── 쓰기 ─────────────────────────────────────────────────────────

    /// <summary>묶음을 버퍼 라이터에 굽는다.</summary>
    /// <param name="set">로딩이 끝난 묶음.</param>
    /// <param name="destination">쓸 대상. 프레임 페이로드 라이터를 그대로 넘길 수 있다.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    /// <remarks>
    /// <b>표는 이름 순으로 굽는다.</b> 같은 묶음이 언제 구워도 같은 바이트가 되어야
    /// 캐시·재전송 판단이 가능하다.
    /// </remarks>
    public static void Write(StaticTableSet set, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(destination);

        WriteBytes(destination, Magic);
        WriteUInt16(destination, FormatVersion);
        WriteUInt16(destination, 0); // 예약
        WriteInt32(destination, set.Count);

        foreach (string name in set.TableNames)
        {
            WriteTable(destination, set.GetTable(name));
        }
    }

    /// <summary>묶음을 새 배열에 굽는다.</summary>
    /// <param name="set">로딩이 끝난 묶음.</param>
    /// <returns>구운 바이트.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>편의용이다. 전송 경로에서는 <see cref="Write"/> 로 라이터에 직접 쓴다.</remarks>
    public static byte[] ToArray(StaticTableSet set)
    {
        ArrayBufferWriter<byte> buffer = new();
        Write(set, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTable(IBufferWriter<byte> destination, StaticTable table)
    {
        StaticTableSchema schema = table.Schema;

        WriteString(destination, schema.Name);
        WriteString(destination, schema.KeyColumnName);
        WriteInt32(destination, schema.Columns.Count);
        WriteInt32(destination, table.RowCount);

        foreach (StaticTableColumn column in schema.Columns)
        {
            WriteString(destination, column.Name);
            WriteByte(destination, (byte)column.Type);
            WriteByte(destination, column.Required ? (byte)1 : (byte)0);
        }

        // 열 우선. 열 하나의 값이 연속으로 놓여 캐시와 압축에 모두 유리하고,
        // 표의 열 저장(column store) 레이아웃과 같은 순서다.
        for (int ordinal = 0; ordinal < schema.Columns.Count; ordinal++)
        {
            for (int row = 0; row < table.RowCount; row++)
            {
                switch (schema.Columns[ordinal].Type)
                {
                    case StaticTableColumnType.String:
                        WriteString(destination, table.GetString(row, ordinal));
                        break;

                    case StaticTableColumnType.Int32:
                    case StaticTableColumnType.Int64:
                        WriteInt64(destination, table.GetInt64(row, ordinal));
                        break;

                    case StaticTableColumnType.Double:
                        WriteInt64(destination, BitConverter.DoubleToInt64Bits(table.GetDouble(row, ordinal)));
                        break;

                    case StaticTableColumnType.Boolean:
                        WriteByte(destination, table.GetBoolean(row, ordinal) ? (byte)1 : (byte)0);
                        break;

                    default:
                        break;
                }
            }
        }
    }

    // ── 읽기 ─────────────────────────────────────────────────────────

    /// <summary>구운 바이트에서 묶음을 되살린다.</summary>
    /// <param name="source">구운 바이트.</param>
    /// <param name="schemas">
    /// <b>로컬</b> 스키마 목록. 와이어의 스키마와 대조하고, 표는 <b>이 인스턴스</b>로 세운다.
    /// </param>
    /// <returns>참조 무결성까지 해결된 묶음.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schemas"/> 가 <see langword="null"/> 이다.</exception>
    /// <exception cref="StaticTableLoadException">
    /// 형식이 깨졌거나, 스키마가 어긋났거나, 참조가 해결되지 않는다. <b>오류는 모아서</b> 던진다.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>⚠ 스키마가 한 칸이라도 어긋나면 거부한다.</b> 값이 열 우선으로 구워져 있어,
    /// 어긋난 스키마로 읽으면 전부 엉뚱한 열로 <b>조용히</b> 해석된다.
    /// </para>
    /// <para>
    /// <b>로컬 스키마 인스턴스를 그대로 쓴다</b> — 그래야 강타입 뷰(ADR-0043)가 받은 표를
    /// 받아들인다. 뷰는 스키마 참조 동일성으로 서수 일치를 보장하기 때문이다.
    /// </para>
    /// </remarks>
    public static StaticTableSet Read(ReadOnlySpan<byte> source, IReadOnlyList<StaticTableSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        Dictionary<string, StaticTableSchema> local = new(schemas.Count, StringComparer.Ordinal);
        foreach (StaticTableSchema schema in schemas)
        {
            if (!local.TryAdd(schema.Name, schema))
            {
                throw Fail($"로컬 스키마에 같은 표 이름이 둘 이상 있다: '{schema.Name}'");
            }
        }

        Reader reader = new(source);

        if (!reader.TryReadBytes(Magic.Length, out ReadOnlySpan<byte> magic) || !magic.SequenceEqual(Magic))
        {
            throw Fail("스냅샷 매직이 맞지 않는다. 이 페이로드는 표 스냅샷이 아니다.");
        }

        if (!reader.TryReadUInt16(out ushort version) || version != FormatVersion)
        {
            throw Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"스냅샷 형식 버전 {version} 을 읽을 수 없다. 이 빌드는 {FormatVersion} 을 읽는다."));
        }

        if (!reader.TryReadUInt16(out _) || !reader.TryReadInt32(out int tableCount) || tableCount < 0)
        {
            throw Fail("스냅샷 머리말이 잘렸다.");
        }

        // ⚠ 선언된 개수를 믿고 자료구조를 잡지 않는다 — 문자열 길이(TryReadString)와 같은
        //   원칙이다. 표 하나의 최소 크기는 머리말 16바이트(이름·키 길이 접두 4+4,
        //   열 수 4, 행 수 4)이므로, 남은 바이트로 담을 수 없는 tableCount 는 손상이다.
        //   여기서 거르지 않으면 손상된 값 하나가 수 GB 선할당 → OOM 이 된다(감사 2026-08-18 R-5).
        if (tableCount > reader.Remaining / 16)
        {
            throw Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"스냅샷이 선언한 표 수({tableCount})가 남은 바이트({reader.Remaining})로 성립하지 않는다."));
        }

        List<StaticTableError> errors = [];
        Dictionary<string, StaticTable> tables = new(tableCount, StringComparer.Ordinal);

        for (int i = 0; i < tableCount; i++)
        {
            StaticTable table = ReadTable(ref reader, local, errors);
            if (!tables.TryAdd(table.Schema.Name, table))
            {
                errors.Add(new StaticTableError(0, null, $"스냅샷에 표 이름이 중복된다: '{table.Schema.Name}'"));
            }
        }

        // 로컬이 기대하는데 서버가 보내지 않은 표는 조용히 없는 표가 되면 안 된다 —
        // 첫 조회에서 KeyNotFoundException 이 나고, 그때는 원인이 여기서 멀어져 있다.
        foreach (string expected in local.Keys)
        {
            if (!tables.ContainsKey(expected))
            {
                errors.Add(new StaticTableError(0, null, $"스냅샷에 기대한 표가 없다: '{expected}'"));
            }
        }

        if (!reader.IsAtEnd)
        {
            errors.Add(new StaticTableError(0, null, "스냅샷 뒤에 해석되지 않은 바이트가 남았다."));
        }

        if (errors.Count > 0)
        {
            throw new StaticTableLoadException("<스냅샷>", errors);
        }

        foreach (StaticTable table in tables.Values)
        {
            StaticTableSetBuilder.ResolveReferences(table, tables, errors);
        }

        if (errors.Count > 0)
        {
            throw new StaticTableLoadException("<스냅샷>", errors);
        }

        return new StaticTableSet(tables);
    }

    private static StaticTable ReadTable(
        ref Reader reader, Dictionary<string, StaticTableSchema> local, List<StaticTableError> errors)
    {
        if (!reader.TryReadString(out string? name) || name is null
            || !reader.TryReadString(out string? keyColumn) || keyColumn is null
            || !reader.TryReadInt32(out int columnCount)
            || !reader.TryReadInt32(out int rowCount)
            || columnCount < 0 || rowCount < 0)
        {
            throw Fail("표 머리말이 잘렸다.");
        }

        // 스키마를 먼저 읽어 소비 위치를 맞춘다 — 대조에 실패해도 스트림은 계속 읽을 수
        // 있어야 나머지 표의 문제까지 한 번에 보고할 수 있다.
        StaticTableColumnType[] wireTypes = new StaticTableColumnType[columnCount];
        string[] wireNames = new string[columnCount];
        bool[] wireRequired = new bool[columnCount];

        for (int i = 0; i < columnCount; i++)
        {
            if (!reader.TryReadString(out string? columnName) || columnName is null
                || !reader.TryReadByte(out byte type)
                || !reader.TryReadByte(out byte required))
            {
                throw Fail($"표 '{name}' 의 열 정의가 잘렸다.");
            }

            wireNames[i] = columnName;
            wireTypes[i] = (StaticTableColumnType)type;
            wireRequired[i] = required != 0;
        }

        if (!local.TryGetValue(name, out StaticTableSchema? schema))
        {
            throw Fail($"스냅샷의 표 '{name}' 에 대응하는 로컬 스키마가 없다. 읽을 수 없으므로 중단한다.");
        }

        // ⚠ 대조 실패는 치명적이다. 값이 열 우선이라 어긋난 스키마로 읽으면 전부 엉뚱한
        // 열로 조용히 해석된다. 여기서 멈추지 않으면 그 조용한 오독이 그대로 나간다.
        if (Mismatch(schema, keyColumn, wireNames, wireTypes, wireRequired) is { } problem)
        {
            throw Fail($"표 '{name}' 의 스키마가 어긋난다: {problem}");
        }

        // ⚠ 선언된 행 수를 믿고 배열을 잡지 않는다 — 이 형식은 서버→클라이언트 전송용이라
        //   받는 쪽은 신뢰 경계 밖 바이트를 다룬다. 행 하나의 최소 바이트(문자열 4, 정수·실수 8,
        //   불리언 1)로 담을 수 없는 rowCount 는 손상이다. 여기서 거르지 않으면
        //   rowCount ≈ int.MaxValue 하나가 수 GB 선할당 → OOM 이 된다(감사 2026-08-18 R-5).
        int minRowBytes = 0;
        foreach (StaticTableColumn column in schema.Columns)
        {
            minRowBytes += column.Type switch
            {
                StaticTableColumnType.String => 4,
                StaticTableColumnType.Int32 or StaticTableColumnType.Int64 => 8,
                StaticTableColumnType.Double => 8,
                StaticTableColumnType.Boolean => 1,
                _ => 0,
            };
        }

        if (rowCount > 0 && (minRowBytes == 0 || rowCount > reader.Remaining / minRowBytes))
        {
            throw Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"표 '{name}' 이 선언한 행 수({rowCount})가 남은 바이트({reader.Remaining})로 성립하지 않는다."));
        }

        return Materialize(ref reader, schema, rowCount, errors);
    }

    /// <summary>와이어 스키마와 로컬 스키마를 대조한다. 문제 없으면 <see langword="null"/>.</summary>
    private static string? Mismatch(
        StaticTableSchema schema,
        string keyColumn,
        string[] names,
        StaticTableColumnType[] types,
        bool[] required)
    {
        if (schema.Columns.Count != names.Length)
        {
            return $"열 수가 다르다(로컬 {schema.Columns.Count}, 스냅샷 {names.Length}).";
        }

        if (!string.Equals(schema.KeyColumnName, keyColumn, StringComparison.Ordinal))
        {
            return $"키 열이 다르다(로컬 '{schema.KeyColumnName}', 스냅샷 '{keyColumn}').";
        }

        for (int i = 0; i < names.Length; i++)
        {
            StaticTableColumn column = schema.Columns[i];

            if (!string.Equals(column.Name, names[i], StringComparison.Ordinal))
            {
                return $"서수 {i} 의 열 이름이 다르다(로컬 '{column.Name}', 스냅샷 '{names[i]}').";
            }

            if (column.Type != types[i])
            {
                return $"열 '{column.Name}' 의 종류가 다르다(로컬 {column.Type}, 스냅샷 {types[i]}).";
            }

            if (column.Required != required[i])
            {
                return $"열 '{column.Name}' 의 필수 여부가 다르다(로컬 {column.Required}, 스냅샷 {required[i]}).";
            }
        }

        return null;
    }

    /// <summary>열 우선으로 실린 값을 표의 열 저장 레이아웃으로 되돌린다.</summary>
    private static StaticTable Materialize(
        ref Reader reader, StaticTableSchema schema, int rowCount, List<StaticTableError> errors)
    {
        int columnCount = schema.Columns.Count;
        int[] typedIndex = new int[columnCount];
        int strings = 0, integers = 0, doubles = 0, booleans = 0;

        for (int i = 0; i < columnCount; i++)
        {
            typedIndex[i] = schema.Columns[i].Type switch
            {
                StaticTableColumnType.String => strings++,
                StaticTableColumnType.Int32 or StaticTableColumnType.Int64 => integers++,
                StaticTableColumnType.Double => doubles++,
                StaticTableColumnType.Boolean => booleans++,
                _ => 0,
            };
        }

        string?[] stringValues = new string?[rowCount * strings];
        long[] integerValues = new long[rowCount * integers];
        double[] doubleValues = new double[rowCount * doubles];
        bool[] booleanValues = new bool[rowCount * booleans];
        Dictionary<string, int> rowByKey = new(rowCount, StringComparer.Ordinal);

        for (int ordinal = 0; ordinal < columnCount; ordinal++)
        {
            StaticTableColumnType type = schema.Columns[ordinal].Type;

            for (int row = 0; row < rowCount; row++)
            {
                switch (type)
                {
                    case StaticTableColumnType.String:
                    {
                        if (!reader.TryReadString(out string? value))
                        {
                            throw Fail($"표 '{schema.Name}' 의 값이 잘렸다.");
                        }

                        stringValues[(row * strings) + typedIndex[ordinal]] = value;

                        if (ordinal == schema.KeyOrdinal && !string.IsNullOrEmpty(value)
                            && !rowByKey.TryAdd(value, row))
                        {
                            // 키 중복은 원본 로딩이 이미 걸렀어야 한다. 여기서 나온다면
                            // 스냅샷이 손상됐거나 만든 쪽이 규약을 어긴 것이다.
                            errors.Add(new StaticTableError(
                                row, schema.KeyColumnName, $"[{schema.Name}] 스냅샷의 키가 중복된다: '{value}'"));
                        }

                        break;
                    }

                    case StaticTableColumnType.Int32:
                    case StaticTableColumnType.Int64:
                    {
                        if (!reader.TryReadInt64(out long value))
                        {
                            throw Fail($"표 '{schema.Name}' 의 값이 잘렸다.");
                        }

                        integerValues[(row * integers) + typedIndex[ordinal]] = value;
                        break;
                    }

                    case StaticTableColumnType.Double:
                    {
                        if (!reader.TryReadInt64(out long bits))
                        {
                            throw Fail($"표 '{schema.Name}' 의 값이 잘렸다.");
                        }

                        doubleValues[(row * doubles) + typedIndex[ordinal]] = BitConverter.Int64BitsToDouble(bits);
                        break;
                    }

                    case StaticTableColumnType.Boolean:
                    {
                        if (!reader.TryReadByte(out byte value))
                        {
                            throw Fail($"표 '{schema.Name}' 의 값이 잘렸다.");
                        }

                        booleanValues[(row * booleans) + typedIndex[ordinal]] = value != 0;
                        break;
                    }

                    default:
                        break;
                }
            }
        }

        return new StaticTable(
            schema, rowCount, stringValues, integerValues, doubleValues, booleanValues, typedIndex, rowByKey);
    }

    private static StaticTableLoadException Fail(string message) =>
        new("<스냅샷>", [new StaticTableError(0, null, message)]);

    // ── 원시 쓰기 ────────────────────────────────────────────────────

    private static void WriteBytes(IBufferWriter<byte> destination, ReadOnlySpan<byte> value)
    {
        value.CopyTo(destination.GetSpan(value.Length));
        destination.Advance(value.Length);
    }

    private static void WriteByte(IBufferWriter<byte> destination, byte value)
    {
        destination.GetSpan(1)[0] = value;
        destination.Advance(1);
    }

    private static void WriteUInt16(IBufferWriter<byte> destination, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.GetSpan(2), value);
        destination.Advance(2);
    }

    private static void WriteInt32(IBufferWriter<byte> destination, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.GetSpan(4), value);
        destination.Advance(4);
    }

    private static void WriteInt64(IBufferWriter<byte> destination, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination.GetSpan(8), value);
        destination.Advance(8);
    }

    /// <summary>길이 접두 UTF-8. <see langword="null"/> 은 길이 <c>-1</c> 로 구분한다.</summary>
    private static void WriteString(IBufferWriter<byte> destination, string? value)
    {
        if (value is null)
        {
            WriteInt32(destination, NullLength);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(destination, byteCount);

        if (byteCount == 0)
        {
            return;
        }

        Span<byte> span = destination.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(value, span);
        destination.Advance(written);
    }

    // ── 원시 읽기 ────────────────────────────────────────────────────

    /// <summary>
    /// 전진형 스팬 리더. <b>모든 읽기가 길이를 먼저 확인</b>하므로 잘린 입력이 예외가 아니라
    /// <see langword="false"/> 가 된다 — 신뢰할 수 없는 바이트를 다루는 코드의 기본 자세다.
    /// </summary>
    private ref struct Reader(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> _source = source;
        private int _position;

        public readonly bool IsAtEnd => _position == _source.Length;

        /// <summary>아직 읽지 않은 바이트 수. 선언된 개수의 그럴듯함 검증에 쓴다.</summary>
        public readonly int Remaining => _source.Length - _position;

        public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
        {
            if (count < 0 || _source.Length - _position < count)
            {
                value = default;
                return false;
            }

            value = _source.Slice(_position, count);
            _position += count;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            if (!TryReadBytes(1, out ReadOnlySpan<byte> span))
            {
                value = 0;
                return false;
            }

            value = span[0];
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (!TryReadBytes(2, out ReadOnlySpan<byte> span))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(span);
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (!TryReadBytes(4, out ReadOnlySpan<byte> span))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(span);
            return true;
        }

        public bool TryReadInt64(out long value)
        {
            if (!TryReadBytes(8, out ReadOnlySpan<byte> span))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt64LittleEndian(span);
            return true;
        }

        public bool TryReadString(out string? value)
        {
            value = null;

            if (!TryReadInt32(out int length))
            {
                return false;
            }

            if (length == NullLength)
            {
                return true;
            }

            // ⚠ 길이를 믿고 배열을 잡지 않는다. 손상된 스냅샷의 큰 길이가 곧 OOM 이 된다.
            if (length < 0 || !TryReadBytes(length, out ReadOnlySpan<byte> span))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(span);
            return true;
        }
    }
}
