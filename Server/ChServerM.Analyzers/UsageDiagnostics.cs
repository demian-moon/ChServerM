using Microsoft.CodeAnalysis;

namespace ChServerM.Analyzers;

/// <summary>
/// 사용 규약 분석기의 진단 정의. 대역은 <c>CHSM3xxx</c> 다(문서: docs/DIAGNOSTICS.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> CHSM1xxx(디스패치)·CHSM2xxx(데이터 테이블)는 프레임워크 <b>선언</b>의
/// 오류를 잡는다. 이 대역은 프레임워크를 <b>사용하는 코드</b>의 오류 — 레거시에서 실제로
/// 서버를 멈추거나 데이터를 오염시킨 패턴 — 를 잡는다. 프레임워크 품질의 체감 차이는
/// 좋은 API 보다 "실수가 컴파일 타임에 걸리는가"에서 난다(ROADMAP Phase 20 ⚠).
/// </para>
/// <para>
/// <b>기본 심각도가 Warning 인 이유.</b> CHSM1xxx 와 달리 여기 걸리는 것들은 구문 분석의
/// 한계 안에서 판정하는 휴리스틱이다. 오탐 가능성이 0 이 아닌 진단을 Error 로 두면
/// 사용자는 진단을 끄는 법부터 배우게 된다 — 그것이 최악의 결과다.
/// </para>
/// </remarks>
internal static class UsageDiagnostics
{
    private const string Category = "ChServerM.Usage";
    private const string HelpUri = "https://github.com/demian-moon/ChServerM/blob/main/docs/DIAGNOSTICS.md";

    /// <summary>async void — 예외가 관측 불가능하게 스레드풀로 새고, 완료를 기다릴 수 없다.</summary>
    public static readonly DiagnosticDescriptor AsyncVoid = new(
        "CHSM3001",
        "async void",
        "'{0}' 은(는) async void 다. 예외가 핸들러 밖으로 새서 프로세스를 죽이고 완료를 기다릴 방법도 없다. async ValueTask 또는 async Task 로 바꾼다.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>async 메서드 안의 블로킹 호출 — 스레드풀 고갈(starvation)로 전체가 멈춘다.</summary>
    public static readonly DiagnosticDescriptor BlockingCallInAsync = new(
        "CHSM3002",
        "async 경로의 블로킹 호출",
        "async 메서드 안에서 {0} 은(는) 스레드를 블로킹한다 — await 로 바꾼다. 블로킹된 스레드가 쌓이면 스레드풀 고갈로 서버 전체가 멈춘다.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>MessageContext.Payload 를 핸들러 수명 밖(필드·속성)으로 저장 — 반환 후 무효가 되는 버퍼다.</summary>
    public static readonly DiagnosticDescriptor PayloadEscapesHandler = new(
        "CHSM3003",
        "Payload 수명 위반",
        "MessageContext.Payload 를 '{0}' 에 저장한다. 페이로드 버퍼는 핸들러가 반환하면 무효가 된다(풀로 반납) — 붙들려면 복사(ToArray)하거나 반환 전에 역직렬화한다.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);
}
