using System;
using System.Collections.Generic;
using System.Globalization;

namespace ChServerM.DataTable;

/// <summary>
/// 로딩·검증이 끝난 데이터 테이블 — <b>파싱된 값</b>을 서수로 읽는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 정적 데이터(밸런스 표·요금표·룰 설정)를 <b>기동 시 한 번 읽고</b>
/// 이후에는 조회만 하는 자료구조다. 레거시 <c>MetaDataM</c> 이 하려던 일이지만 세 가지가 다르다:
/// </para>
/// <list type="number">
///   <item><b>값이 파싱돼 있다.</b> 레거시는 모든 값을 <c>string</c> 으로 들고 조회마다
///   <c>int.Parse</c> 를 했다 — 조회 비용과 컬처 의존이 둘 다 붙었다(문제점 1)</item>
///   <item><b>조회가 서수 기반이다.</b> 문자열 키 조회는 오타를 컴파일 타임에 못 잡는다
///   (문제점 4). 열 이름 → 서수 변환은 <b>조립 시점에 한 번</b>만 한다</item>
///   <item><b>로딩 시점에 전수 검증된다.</b> 여기 들어온 값은 이미 스키마를 만족한다 —
///   조회는 실패하지 않는다</item>
/// </list>
///
/// <para>
/// <b>⚠ 조회는 실패하지 않는다는 것이 계약이다.</b> 범위를 벗어난 행·서수는
/// <see cref="ArgumentOutOfRangeException"/> 이지만, 그것은 <b>호출자 버그</b>이지 데이터
/// 문제가 아니다. 데이터 문제는 전부 로딩에서 걸러진다.
/// </para>
///
/// <para>
/// <b>스레드 규약 — 만든 뒤 불변이며 스레드 안전하다.</b> 여러 파티션 워커가 같은 테이블을
/// 동시에 읽는 것이 기본 사용 형태이므로 불변이 아니면 성립하지 않는다.
/// 갱신은 <b>새 인스턴스로 교체</b>한다(핫 리로드는 그 교체를 원자적으로 만드는 문제다).
/// </para>
///
/// <para>
/// <b>레이아웃.</b> 열 종류별로 배열을 따로 둔다(<i>column store</i>). 한 열을 훑는 접근이
/// 캐시에 유리하고, 값 타입을 <c>object</c> 로 박싱하지 않는다.
/// </para>
/// </remarks>
public sealed class StaticTable
{
    private readonly string?[] _strings;
    private readonly long[] _integers;
    private readonly double[] _doubles;
    private readonly bool[] _booleans;

    /// <summary>열 서수 → 해당 종류 배열에서의 열 인덱스.</summary>
    private readonly int[] _typedColumnIndex;

    /// <summary>
    /// 종류별 열 수. <b>조회마다 다시 세지 않기 위해</b> 생성 시 한 번 구한다.
    /// </summary>
    /// <remarks>
    /// 처음에는 접근할 때마다 열을 훑어 세도록 썼는데, 그것은 조회 하나가 열 수에 비례하는
    /// 비용을 내는 것이다 — <b>O(1) 조회를 만들려던 자료구조가 O(열 수)가 된다.</b>
    /// </remarks>
    private readonly int _stringColumnCount;
    private readonly int _integerColumnCount;
    private readonly int _doubleColumnCount;
    private readonly int _booleanColumnCount;

    private readonly Dictionary<string, int> _rowByKey;

    internal StaticTable(
        StaticTableSchema schema,
        int rowCount,
        string?[] strings,
        long[] integers,
        double[] doubles,
        bool[] booleans,
        int[] typedColumnIndex,
        Dictionary<string, int> rowByKey)
    {
        Schema = schema;
        RowCount = rowCount;
        _strings = strings;
        _integers = integers;
        _doubles = doubles;
        _booleans = booleans;
        _typedColumnIndex = typedColumnIndex;
        _rowByKey = rowByKey;

        foreach (StaticTableColumn column in schema.Columns)
        {
            switch (column.Type)
            {
                case StaticTableColumnType.String: _stringColumnCount++; break;
                case StaticTableColumnType.Int32:
                case StaticTableColumnType.Int64: _integerColumnCount++; break;
                case StaticTableColumnType.Double: _doubleColumnCount++; break;
                case StaticTableColumnType.Boolean: _booleanColumnCount++; break;
                default: break;
            }
        }
    }

    /// <summary>이 테이블의 스키마.</summary>
    public StaticTableSchema Schema { get; }

    /// <summary>행 수.</summary>
    public int RowCount { get; }

    /// <summary>키로 행 번호를 찾는다.</summary>
    /// <param name="key">키 값.</param>
    /// <param name="row">행 번호.</param>
    /// <returns>찾았으면 <see langword="true"/>.</returns>
    public bool TryGetRow(string key, out int row) => _rowByKey.TryGetValue(key, out row);

    /// <summary>문자열 값을 읽는다.</summary>
    /// <param name="row">행 번호.</param>
    /// <param name="ordinal">열 서수.</param>
    /// <returns>값. 선택 열이 비어 있으면 <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">행·서수가 범위를 벗어났거나 열 종류가 다르다.</exception>
    public string? GetString(int row, int ordinal) =>
        _strings[Index(row, ordinal, StaticTableColumnType.String, _stringColumnCount)];

    /// <summary>32비트 정수 값을 읽는다.</summary>
    /// <param name="row">행 번호.</param>
    /// <param name="ordinal">열 서수.</param>
    /// <returns>값.</returns>
    /// <exception cref="ArgumentOutOfRangeException">행·서수가 범위를 벗어났거나 열 종류가 다르다.</exception>
    public int GetInt32(int row, int ordinal) =>
        checked((int)_integers[Index(row, ordinal, StaticTableColumnType.Int32, _integerColumnCount)]);

    /// <summary>64비트 정수 값을 읽는다.</summary>
    /// <param name="row">행 번호.</param>
    /// <param name="ordinal">열 서수.</param>
    /// <returns>값.</returns>
    /// <exception cref="ArgumentOutOfRangeException">행·서수가 범위를 벗어났거나 열 종류가 다르다.</exception>
    public long GetInt64(int row, int ordinal) =>
        _integers[Index(row, ordinal, StaticTableColumnType.Int64, _integerColumnCount)];

    /// <summary>실수 값을 읽는다.</summary>
    /// <param name="row">행 번호.</param>
    /// <param name="ordinal">열 서수.</param>
    /// <returns>값.</returns>
    /// <exception cref="ArgumentOutOfRangeException">행·서수가 범위를 벗어났거나 열 종류가 다르다.</exception>
    public double GetDouble(int row, int ordinal) =>
        _doubles[Index(row, ordinal, StaticTableColumnType.Double, _doubleColumnCount)];

    /// <summary>참·거짓 값을 읽는다.</summary>
    /// <param name="row">행 번호.</param>
    /// <param name="ordinal">열 서수.</param>
    /// <returns>값.</returns>
    /// <exception cref="ArgumentOutOfRangeException">행·서수가 범위를 벗어났거나 열 종류가 다르다.</exception>
    public bool GetBoolean(int row, int ordinal) =>
        _booleans[Index(row, ordinal, StaticTableColumnType.Boolean, _booleanColumnCount)];

    /// <summary>행·서수를 검증하고 해당 종류 배열의 인덱스를 계산한다.</summary>
    /// <remarks>
    /// <c>Int32</c> 와 <c>Int64</c> 는 같은 정수 배열을 공유하므로 종류 검사에서 호환으로 본다.
    /// </remarks>
    private int Index(int row, int ordinal, StaticTableColumnType expected, int typedColumnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, Schema.Columns.Count);

        StaticTableColumnType actual = Schema.Columns[ordinal].Type;
        bool compatible = actual == expected
            || (expected is StaticTableColumnType.Int32 or StaticTableColumnType.Int64
                && actual is StaticTableColumnType.Int32 or StaticTableColumnType.Int64);

        if (!compatible)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"열 '{Schema.Columns[ordinal].Name}' 은 {actual} 인데 {expected} 로 읽으려 했다."));
        }

        return (row * typedColumnCount) + _typedColumnIndex[ordinal];
    }
}
