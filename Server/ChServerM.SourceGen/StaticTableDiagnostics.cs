using Microsoft.CodeAnalysis;

namespace ChServerM.SourceGen;

/// <summary>
/// 데이터 테이블 접근자 제너레이터의 진단 정의. 대역은 <c>CHSM2xxx</c> 다(문서: docs/DIAGNOSTICS.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>기본 심각도가 Error 인 이유.</b> 여기 걸리는 것들은 전부 <b>런타임이었다면
/// 스키마 조립 예외</b>이거나 — 더 나쁘게는 — <b>조용히 무시되는 제약</b>이다.
/// <c>StaticTableSchema</c> 생성자가 이미 같은 판정을 하지만, 그 실패는 기동 시점에
/// 나타난다. 컴파일 타임으로 당길 수 있는 실패를 런타임에 두면 이 축의 존재 이유가 없다.
/// </para>
/// <para>
/// <b>막고 있는 것은 서수 관리다.</b> 열을 가운데에 끼워 넣었을 때 뒤따르는 서수가 밀리는
/// 사고는 진단으로 잡을 수 없다 — <b>서수를 사람이 적지 않게</b> 만들어야 사라진다.
/// 그래서 이 제너레이터의 1차 산출물은 진단이 아니라 <b>스키마와 접근자의 동시 생성</b>이고,
/// 진단은 그 선언 자체가 앞뒤가 맞는지를 지킨다.
/// </para>
/// </remarks>
internal static class StaticTableDiagnostics
{
    private const string Category = "ChServerM.DataTable";
    private const string HelpUri = "https://github.com/demian-moon/ChServerM/blob/main/docs/DIAGNOSTICS.md";

    /// <summary>행 타입이나 바깥 타입이 partial 이 아니어서 구현을 덧붙일 수 없음.</summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        "CHSM2001",
        "행 타입이 partial 이 아니다",
        "{0} 과(와) 그 바깥 타입은 전부 'partial' 이어야 한다. 제너레이터가 구현을 덧붙일 자리가 없다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>키 열이 없거나, 둘 이상이거나, 선택(Optional)으로 선언됨.</summary>
    /// <remarks>
    /// <b>선택 키를 막는 이유.</b> 런타임 리더는 키 칸이 비어 있으면 그 행을
    /// <b>키 사전에 넣지 않는다</b> — 로딩은 성공하는데 그 행만 영원히 찾히지 않는다.
    /// 조용한 유실이므로 선언 단계에서 끊는다.
    /// </remarks>
    public static readonly DiagnosticDescriptor InvalidKeyColumn = new(
        "CHSM2002",
        "키 열 선언이 잘못됐다",
        "{0} 의 키 열: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>열(partial 속성)이 하나도 없음.</summary>
    public static readonly DiagnosticDescriptor NoColumns = new(
        "CHSM2003",
        "열이 없다",
        "{0} 에 열이 하나도 없다. 열은 getter 만 있는 partial 인스턴스 속성으로 선언한다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>지원하지 않는 속성 형식.</summary>
    public static readonly DiagnosticDescriptor UnsupportedColumnType = new(
        "CHSM2004",
        "지원하지 않는 열 형식",
        "속성 '{0}' 의 형식 {1} 은(는) 열이 될 수 없다. string · int · long · double · bool 만 쓴다 — 그 밖의 형식은 로딩 시점 파싱 규약이 없다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>열 이름이 중복되거나 생성될 멤버 이름과 충돌.</summary>
    public static readonly DiagnosticDescriptor NameConflict = new(
        "CHSM2005",
        "이름 충돌",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>선택 문자열 열인데 속성이 널 허용이 아님.</summary>
    public static readonly DiagnosticDescriptor OptionalStringMustBeNullable = new(
        "CHSM2006",
        "선택 문자열 열은 널 허용이어야 한다",
        "속성 '{0}' 은(는) 선택(Optional) 문자열 열이므로 'string?' 로 선언해야 한다. 빈 칸은 null 로 오는데 'string' 이라고 적으면 그 거짓말이 호출자에게 그대로 전달된다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>범위 제약이 열 종류와 맞지 않거나 뒤집힘.</summary>
    public static readonly DiagnosticDescriptor InvalidRange = new(
        "CHSM2007",
        "범위 제약이 모순이다",
        "속성 '{0}' 의 범위 제약: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>참조 대상이 행 타입이 아니거나 참조 열이 문자열이 아님.</summary>
    public static readonly DiagnosticDescriptor InvalidReference = new(
        "CHSM2008",
        "참조 선언이 잘못됐다",
        "속성 '{0}' 의 참조: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>CSV 헤더에 선언한 열이 없음.</summary>
    /// <remarks>
    /// <para>
    /// <b>기동 시점 검증을 컴파일 타임으로 당긴다.</b> 같은 판정을
    /// <c>CsvStaticTableReader</c> 가 로딩 때 하지만, 그 실패는 <b>서버를 띄워야</b>
    /// 보인다. 열 이름 오타·이름 변경은 밸런스 표에서 가장 흔한 사고이고,
    /// 에디터에서 줄과 함께 보이는 것과 기동 로그에서 보이는 것은 값이 다르다.
    /// </para>
    /// <para>
    /// <b>⚠ 여기서는 헤더만 본다.</b> 값 검증(타입·범위·참조 무결성·키 중복)은 로딩
    /// 시점에 그대로 남는다 — 그것까지 하려면 CSV 파서와 검증기를 제너레이터 쪽에
    /// <b>한 벌 더 구현</b>해야 하고, 두 구현이 갈라지면 "무엇이 유효한 표인가" 의
    /// 정본이 둘이 된다. 헤더 규칙만 30줄쯤 중복하고 그 일치는 테스트가 지킨다.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor CsvMissingColumn = new(
        "CHSM2011",
        "CSV 헤더에 선언한 열이 없다",
        "'{0}' 의 헤더에 열 '{1}' 이 없다. 행 타입 {2} 이 그 열을 선언한다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>CSV 에 헤더 줄이 없음(빈 파일이거나 주석뿐).</summary>
    public static readonly DiagnosticDescriptor CsvNoHeader = new(
        "CHSM2012",
        "CSV 에 헤더 줄이 없다",
        "'{0}' 에 헤더 줄이 없다(비었거나 주석뿐이다). 로딩이 실패한다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>CSV 헤더에 같은 열 이름이 둘 이상.</summary>
    /// <remarks>
    /// 리더는 <b>먼저 나온 열</b>을 쓴다. 조용히 이기는 쪽이 생기므로 선언 단계에서 알린다.
    /// </remarks>
    public static readonly DiagnosticDescriptor CsvDuplicateHeaderColumn = new(
        "CHSM2013",
        "CSV 헤더에 중복된 열 이름이 있다",
        "'{0}' 의 헤더에 '{1}' 이 두 번 이상 있다. 리더는 먼저 나온 열을 쓰므로 나머지는 조용히 무시된다.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>행 타입이 readonly struct 가 아님.</summary>
    /// <remarks>
    /// <b>CHSM2009 는 비워 둔다.</b> 원래 "ChServerM.DataTable 미참조" 자리였는데, 어트리뷰트
    /// 자체가 그 어셈블리에 있으므로 <b>어트리뷰트를 찾았다면 참조는 이미 있다</b> — 도달할 수
    /// 없는 진단이라 만들지 않았다. 번호는 재사용하지 않는다(진단 ID 는 사용자 억제 설정에 박힌다).
    /// </remarks>
    public static readonly DiagnosticDescriptor NotReadOnlyStruct = new(
        "CHSM2010",
        "행 타입은 readonly struct 여야 한다",
        "{0} 은(는) 'readonly struct' 여야 한다. 행은 테이블 참조와 행 번호만 들고 다니는 값이며, 불변이어야 방어 복사가 생기지 않고 여러 파티션 워커가 동시에 들고 다녀도 안전하다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);
}
