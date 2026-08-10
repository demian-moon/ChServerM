using System;

namespace ChServerM.DataTable;

/// <summary>
/// 이 타입이 정적 데이터 테이블의 <b>한 행</b>임을 선언한다 — 스키마와 접근자가 여기서 생성된다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 스키마를 손으로 조립하면(<see cref="StaticTableSchema"/> 생성자에 열
/// 목록을 넘기면) <b>서수를 사람이 관리</b>하게 된다. 열을 가운데에 하나 끼워 넣는 순간
/// 그 뒤의 모든 <c>GetInt32(row, 3)</c> 이 조용히 다른 열을 읽는다 — 컴파일도 되고 예외도
/// 나지 않으며, 밸런스 값만 틀린다. 레거시의 문제점 4(문자열 키 조회)를 서수로 바꿔 풀었더니
/// 이번에는 <b>서수 관리</b>가 새 위험이 된 것이고, 이 어트리뷰트가 그 마지막 손 작업을 없앤다.
/// </para>
///
/// <para>
/// <b>사용법</b> — <c>readonly partial struct</c> 에 붙이고, 열을 <c>partial</c> 속성으로 적는다.
/// 선언 순서가 곧 서수이고, 그 서수를 <b>사람이 보거나 적을 일이 없다</b>.
/// </para>
/// <code>
/// [StaticTableRow("Item")]
/// public readonly partial struct ItemRow
/// {
///     [StaticTableColumn(Key = true)]
///     public partial string Id { get; }
///
///     public partial int Damage { get; }
///
///     [StaticTableColumn(Name = "drop_rate", MinimumReal = 0.0, MaximumReal = 1.0)]
///     public partial double DropRate { get; }
///
///     [StaticTableColumn(Optional = true, References = typeof(RecipeRow))]
///     public partial string? RecipeId { get; }
/// }
///
/// // 생성된 것: ItemRow.Schema · ItemRow.Table(뷰) · 각 속성의 구현
/// var set = new StaticTableSetBuilder().Add(ItemRow.Schema, csv).Build();
/// ItemRow.Table items = new(set);
/// if (items.TryGetRow("sword", out ItemRow sword)) { int d = sword.Damage; }
/// </code>
///
/// <para>
/// <b>⚠ 이것은 선언일 뿐 런타임 동작이 없다.</b> 리플렉션으로 읽지 않는다 — 읽는 것은
/// 컴파일 타임의 소스 제너레이터(<c>CHSM2xxx</c> 진단)뿐이다. CLAUDE.md 2절
/// "리플렉션 대신 소스 제너레이터" 의 데이터 테이블 축 적용이다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 어트리뷰트는 메타데이터라 무관하다. 생성된 행 구조체는 불변이며
/// 스레드 안전하다(<see cref="StaticTable"/> 이 불변이기 때문이다).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class StaticTableRowAttribute : Attribute
{
    /// <summary>행 타입을 테이블에 묶는다.</summary>
    /// <param name="tableName">
    /// 테이블 이름. <see cref="StaticTableSet"/> 안에서 이 이름으로 찾으며, 오류 메시지에도 쓰인다.
    /// </param>
    public StaticTableRowAttribute(string tableName) => TableName = tableName;

    /// <summary>테이블 이름.</summary>
    public string TableName { get; }
}

/// <summary>
/// 행 속성 하나에 붙는 열 메타데이터 — <b>붙이지 않아도 열이다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>기본값이 곧 흔한 경우다.</b> <c>partial</c> 속성은 그 자체로 열이고, 열 이름은 속성
/// 이름이며, 필수다. 이 어트리뷰트는 <b>기본값에서 벗어날 때만</b> 붙인다 — 표의 대부분을
/// 차지하는 평범한 열에 장식을 요구하면 선언이 읽히지 않는다.
/// </para>
/// <para>
/// <b>⚠ 여기 넣을 수 있는 것은 "표 자체로 판정 가능한" 제약뿐이다</b>
/// (<see cref="StaticTableColumn"/> 문서와 같은 선). 범위·참조 무결성은 다른 행·다른 표만
/// 보면 판정되지만, "레벨 10 이상이면 가격이 100 이상" 같은 도메인 규칙은 아니다.
/// </para>
/// <para>
/// <b>⚠ 범위는 정수·실수를 따로 둔다.</b> 하나의 <c>double</c> 로 통일하면 <c>Int64</c> 의
/// 2⁵³ 초과 값이 <b>경계에서 조용히 틀린 판정</b>을 낸다 — 런타임 스키마와 같은 이유다.
/// 열 종류와 맞지 않는 범위를 걸면 <b>빌드가 실패한다</b>(CHSM2007). 조용히 무시되는 제약은
/// 걸지 않은 것보다 나쁘다. 작성자는 걸었다고 믿기 때문이다.
/// </para>
/// <para>
/// <b>범위를 "설정하지 않음" 과 구분하는 방법.</b> 어트리뷰트 인자는 <c>long?</c> 같은
/// 널 허용 값 타입이 될 수 없다. 그래서 <b>제너레이터가 명명 인자의 존재 여부</b>를 보고
/// 판단한다 — 적지 않으면 제약이 없는 것이고, 적으면 그 값이 그대로 제약이다. 센티넬 값을
/// 두지 않았으므로 "설정한 <c>0</c>" 과 "설정하지 않음" 이 섞이지 않는다.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class StaticTableColumnAttribute : Attribute
{
    /// <summary>
    /// CSV 헤더의 열 이름. 지정하지 않으면 <b>속성 이름</b>을 쓴다.
    /// </summary>
    /// <remarks>
    /// 표는 사람이 만들고 <c>snake_case</c> 헤더가 흔하다. C# 속성 이름을 표에 맞추는 것보다
    /// 여기서 한 번 이어 주는 편이 양쪽 모두의 관례를 지킨다.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>
    /// 이 열이 <b>키</b>인가. 테이블에 정확히 하나 있어야 한다.
    /// </summary>
    /// <remarks>
    /// 별도 <c>[StaticTableKey]</c> 를 두지 않은 이유: 어트리뷰트 하나가 더 늘면 선언에
    /// 붙는 줄도 늘어난다. 키는 열의 성질이므로 열 메타데이터 안에 있는 것이 맞다.
    /// </remarks>
    public bool Key { get; set; }

    /// <summary>
    /// 빈 칸을 허용하는가. 기본은 <see langword="false"/>(필수)다.
    /// </summary>
    /// <remarks>
    /// <b>⚠ 선택 문자열 열은 속성을 <c>string?</c> 로 선언해야 한다</b>(CHSM2006).
    /// 빈 칸이 <see langword="null"/> 로 오는데 <c>string</c> 이라고 적으면 그 거짓말이
    /// 호출자에게 그대로 전달된다. 숫자·참거짓 열의 빈 칸은 <c>0</c>/<c>false</c> 다.
    /// </remarks>
    public bool Optional { get; set; }

    /// <summary>
    /// 이 열이 가리키는 다른 표의 <b>행 타입</b>. <see langword="null"/> 이면 참조가 아니다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>왜 표 이름 문자열이 아니라 <c>typeof</c> 인가.</b> 이 증분의 목적이 문자열 키를
    /// 없애는 것이다. 대상 타입을 적으면 오타가 <b>컴파일 오류</b>가 되고, 대상 표 이름은
    /// 그 타입의 <see cref="StaticTableRowAttribute"/> 에서 제너레이터가 읽어 온다 —
    /// 같은 이름을 두 군데 적을 일이 없다.
    /// </para>
    /// <para>
    /// 참조 열에는 대상 <b>행 번호</b>를 돌려주는 속성이 함께 생성된다(<c>{속성명}RowIndex</c>).
    /// 조회 때마다 키로 다시 찾지 않기 위해서다 — 검증과 인덱스 변환이 같은 패스라는
    /// <see cref="StaticTableSetBuilder"/> 의 성질을 그대로 쓴다.
    /// </para>
    /// </remarks>
    public Type? References { get; set; }

    /// <summary>정수 열의 최솟값(포함).</summary>
    public long MinimumInteger { get; set; }

    /// <summary>정수 열의 최댓값(포함).</summary>
    public long MaximumInteger { get; set; }

    /// <summary>실수 열의 최솟값(포함).</summary>
    public double MinimumReal { get; set; }

    /// <summary>실수 열의 최댓값(포함).</summary>
    public double MaximumReal { get; set; }
}
