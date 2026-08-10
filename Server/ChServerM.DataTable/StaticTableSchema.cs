using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ChServerM.DataTable;

/// <summary>데이터 테이블 열의 값 종류.</summary>
/// <remarks>
/// <b>⚠ 문자열이 기본이 아니다.</b> 레거시 <c>MetaDataM</c> 은 모든 값을 <c>string</c> 으로
/// 들고 조회마다 <c>int.Parse</c> 를 했다 — 조회 비용 + 컬처 의존이 둘 다 붙는다
/// (docs/legacy/11-data-table 문제점 1). 여기서는 <b>로딩 시점에 한 번 파싱</b>하고,
/// 파싱 실패는 조회가 아니라 <b>로딩이 실패한다</b>.
/// </remarks>
// CA1720 억제 — 'Int32'·'Double' 같은 이름이 형식 이름과 겹치지만, 열의 값 종류를 가리키는
// 데는 이 이름이 가장 정확하다. 'Integer32'·'Number' 같은 회피 이름은 무엇을 파싱하는지를
// 오히려 흐린다. 이 열거형의 독자는 스키마를 쓰는 사람이고, 그에게는 C# 형식 이름이 곧 답이다.
#pragma warning disable CA1720
public enum StaticTableColumnType
{
    /// <summary>문자열. 파싱하지 않는다.</summary>
    String = 0,

    /// <summary>32비트 정수.</summary>
    Int32 = 1,

    /// <summary>64비트 정수.</summary>
    Int64 = 2,

    /// <summary>배정밀도 실수.</summary>
    Double = 3,

    /// <summary>참·거짓. <c>true/false</c>, <c>1/0</c> 을 받는다.</summary>
    Boolean = 4,
}
#pragma warning restore CA1720

/// <summary>테이블 열 하나의 정의.</summary>
/// <param name="Name">열 이름. CSV 헤더와 대조한다.</param>
/// <param name="Type">값 종류.</param>
/// <param name="Required">
/// 빈 값을 허용하지 않는가. <see langword="true"/> 인데 비어 있으면 <b>로딩이 실패한다</b>.
/// </param>
/// <remarks>
/// <para>
/// <b>제약은 로딩 시점에 검사한다.</b> 범위를 벗어난 값이나 존재하지 않는 참조는 조회
/// 시점이 아니라 <b>기동 시점</b>에 드러나야 한다 — 레거시는 그 검사가 없어 잘못된 값이
/// 첫 조회에서 예외가 되거나 조용히 기본값이 됐다(docs/legacy/11-data-table 문제점 2).
/// </para>
/// <para>
/// <b>⚠ 여기 넣는 것은 "표 자체로 판정 가능한" 제약뿐이다.</b> 범위와 참조 무결성은 다른
/// 행·다른 표만 보면 판정되지만, "레벨 10 이상이면 가격이 100 이상이어야 한다" 같은 도메인
/// 규칙은 아니다. 그것까지 넣기 시작하면 <b>스키마가 곧 앱</b>이 된다.
/// </para>
/// </remarks>
public sealed record StaticTableColumn(string Name, StaticTableColumnType Type, bool Required = true)
{
    /// <summary>
    /// 이 열의 값이 가리키는 다른 테이블 이름. <see langword="null"/> 이면 참조가 아니다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 참조 대상은 <b>그 테이블의 키 열</b>이다. 로딩 시 <b>모든 값이 실제로 존재하는지</b>
    /// 검사하고(참조 무결성), 동시에 <b>대상 행 번호로 변환</b>해 둔다 —
    /// 조회 때마다 키로 찾지 않도록(레거시 <c>ConvertColToIndexRefMetaM</c> 의 승계).
    /// </para>
    /// <para>
    /// 참조 열은 <see cref="StaticTableColumnType.String"/> 이어야 한다 — 키가 문자열이기 때문이다.
    /// </para>
    /// </remarks>
    public string? ReferencesTable { get; init; }

    /// <summary>정수 열의 최솟값(포함). <see langword="null"/> 이면 검사하지 않는다.</summary>
    public long? MinimumInteger { get; init; }

    /// <summary>정수 열의 최댓값(포함). <see langword="null"/> 이면 검사하지 않는다.</summary>
    public long? MaximumInteger { get; init; }

    /// <summary>실수 열의 최솟값(포함). <see langword="null"/> 이면 검사하지 않는다.</summary>
    /// <remarks>
    /// 정수 열에는 <see cref="MinimumInteger"/> 를 쓴다. 하나의 <c>double</c> 범위로 통일하지
    /// 않은 이유: <c>Int64</c> 의 2⁵³ 초과 값이 <c>double</c> 로 정확히 표현되지 않아
    /// <b>경계에서 조용히 틀린 판정</b>이 나온다.
    /// </remarks>
    public double? MinimumReal { get; init; }

    /// <summary>실수 열의 최댓값(포함). <see langword="null"/> 이면 검사하지 않는다.</summary>
    public double? MaximumReal { get; init; }

    /// <summary>이 열이 다른 테이블을 참조하는가.</summary>
    public bool IsReference => ReferencesTable is not null;

    /// <summary>열 정의 자체가 앞뒤가 맞는지 확인한다.</summary>
    /// <returns>문제 설명. 없으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>모순된 스키마는 조립 시점에 막는다.</b> 예를 들어 문자열 열에 정수 범위를 걸면
    /// 그 제약은 <b>영원히 검사되지 않는데</b>, 작성자는 걸었다고 믿는다 — 조용히 무시되는
    /// 설정이 가장 위험하다.
    /// </remarks>
    internal string? Validate()
    {
        bool numeric = Type is StaticTableColumnType.Int32 or StaticTableColumnType.Int64;
        bool real = Type is StaticTableColumnType.Double;

        if ((MinimumInteger is not null || MaximumInteger is not null) && !numeric)
        {
            return $"정수 범위는 정수 열에만 걸 수 있다(이 열은 {Type}).";
        }

        if ((MinimumReal is not null || MaximumReal is not null) && !real)
        {
            return $"실수 범위는 Double 열에만 걸 수 있다(이 열은 {Type}).";
        }

        if (MinimumInteger is { } minInteger && MaximumInteger is { } maxInteger && minInteger > maxInteger)
        {
            return $"정수 범위가 뒤집혔다: [{minInteger}, {maxInteger}]";
        }

        if (MinimumReal is { } minReal && MaximumReal is { } maxReal && minReal > maxReal)
        {
            return $"실수 범위가 뒤집혔다: [{minReal}, {maxReal}]";
        }

        if (IsReference && Type != StaticTableColumnType.String)
        {
            return $"참조 열은 String 이어야 한다(키가 문자열이다). 이 열은 {Type}.";
        }

        return null;
    }
}

/// <summary>
/// 테이블의 열 구성과 키 — <b>로딩 전에 확정되는 계약</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 스키마 없이 파일을 읽으면 "무엇이 잘못됐는지" 를 판정할 기준이 없다.
/// 레거시는 스키마가 없어 <b>잘못된 값이 첫 조회 시점에 예외가 되거나 조용히 기본값</b>이
/// 됐다(문제점 2). 스키마가 있으면 그 판정을 <b>로딩 시점</b>으로 당길 수 있다.
/// </para>
/// <para>
/// <b>키 열은 필수다.</b> 키 없는 테이블은 순차 훑기밖에 못 하는데, 데이터 테이블의 용도가
/// 대개 "ID 로 한 행 찾기" 이기 때문이다. 키는 <b>중복될 수 없고</b>, 중복은 로딩 실패다.
/// </para>
/// <para><b>스레드 규약.</b> 불변이다. 만든 뒤에는 어디서든 공유해도 안전하다.</para>
/// </remarks>
public sealed class StaticTableSchema
{
    private readonly Dictionary<string, int> _ordinalByName;

    /// <summary>스키마를 만든다.</summary>
    /// <param name="name">테이블 이름. 오류 메시지에 쓰인다.</param>
    /// <param name="keyColumnName">키 열 이름. 반드시 <paramref name="columns"/> 에 있어야 한다.</param>
    /// <param name="columns">열 정의. 순서가 곧 서수(ordinal)다.</param>
    /// <exception cref="ArgumentException">열이 비었거나, 이름이 중복이거나, 키 열이 없다.</exception>
    public StaticTableSchema(string name, string keyColumnName, IReadOnlyList<StaticTableColumn> columns)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(keyColumnName);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException($"테이블 '{name}' 에 열이 하나도 없다.", nameof(columns));
        }

        _ordinalByName = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        for (int i = 0; i < columns.Count; i++)
        {
            if (!_ordinalByName.TryAdd(columns[i].Name, i))
            {
                throw new ArgumentException(
                    $"테이블 '{name}' 에 중복된 열 이름이 있다: '{columns[i].Name}'", nameof(columns));
            }
        }

        foreach (StaticTableColumn column in columns)
        {
            if (column.Validate() is { } problem)
            {
                throw new ArgumentException(
                    $"테이블 '{name}' 의 열 '{column.Name}' 정의가 모순이다: {problem}", nameof(columns));
            }
        }

        if (!_ordinalByName.TryGetValue(keyColumnName, out int keyOrdinal))
        {
            throw new ArgumentException(
                $"테이블 '{name}' 의 키 열 '{keyColumnName}' 이 열 목록에 없다.", nameof(keyColumnName));
        }

        Name = name;
        KeyColumnName = keyColumnName;
        KeyOrdinal = keyOrdinal;
        Columns = new ReadOnlyCollection<StaticTableColumn>([.. columns]);
    }

    /// <summary>테이블 이름.</summary>
    public string Name { get; }

    /// <summary>키 열 이름.</summary>
    public string KeyColumnName { get; }

    /// <summary>키 열의 서수.</summary>
    public int KeyOrdinal { get; }

    /// <summary>열 정의. 인덱스가 곧 서수다.</summary>
    public IReadOnlyList<StaticTableColumn> Columns { get; }

    /// <summary>열 이름으로 서수를 찾는다.</summary>
    /// <param name="columnName">열 이름.</param>
    /// <param name="ordinal">서수.</param>
    /// <returns>찾았으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>조회 핫패스에서 쓰라고 있는 것이 아니다.</b> 생성된 접근자가 <b>조립 시점에 한 번</b>
    /// 서수를 확보하고, 이후에는 서수로만 접근한다 — 문자열 키 조회를 핫패스에 두는 것이
    /// 레거시의 문제였다(문제점 4).
    /// </remarks>
    public bool TryGetOrdinal(string columnName, out int ordinal) =>
        _ordinalByName.TryGetValue(columnName, out ordinal);
}
