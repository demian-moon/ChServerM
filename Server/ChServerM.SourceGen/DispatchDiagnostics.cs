using Microsoft.CodeAnalysis;

namespace ChServerM.SourceGen;

/// <summary>
/// 디스패치 제너레이터의 진단 정의. 대역은 <c>CHSM1xxx</c> 다(문서: docs/DIAGNOSTICS.md).
/// </summary>
/// <remarks>
/// <b>기본 심각도가 Error 인 이유.</b> 여기 걸리는 것들(중복 ID, 계약 미구현, 센티넬 ID)은
/// 전부 런타임이었다면 조립 예외이거나 — 최악의 경우 — 잘못된 핸들러가 조용히 도는
/// 결함이다. 컴파일 타임으로 당길 수 있는 실패를 경고로 낮추면 이 축의 존재 이유가 없다.
/// </remarks>
internal static class DispatchDiagnostics
{
    private const string Category = "ChServerM.Dispatch";
    private const string HelpUri = "https://github.com/demian-moon/ChServerM/blob/main/docs/DIAGNOSTICS.md";

    /// <summary>같은 메시지 ID 에 핸들러가 둘 이상.</summary>
    public static readonly DiagnosticDescriptor DuplicateMessageId = new(
        "CHSM1001",
        "중복 메시지 ID",
        "메시지 ID {0} 에 핸들러가 둘 이상이다({1}). 어느 것이 도는지 알 수 없으므로 빌드를 막는다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>[MessageHandler] 타입이 IMessageHandler&lt;T&gt; 를 구현하지 않음.</summary>
    public static readonly DiagnosticDescriptor NotAHandler = new(
        "CHSM1002",
        "핸들러 계약 미구현",
        "{0} 은(는) [MessageHandler] 가 붙었지만 IMessageHandler<TMessage> 를 구현하지 않는다",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>메시지 ID 0 은 '설정되지 않음' 센티넬.</summary>
    public static readonly DiagnosticDescriptor SentinelMessageId = new(
        "CHSM1003",
        "센티넬 메시지 ID",
        "{0} 의 메시지 ID 0 은 '설정되지 않음'을 뜻하는 센티넬이라 핸들러를 붙일 수 없다",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>IMessageHandler&lt;T&gt; 를 여러 T 로 구현해 대상 메시지가 모호.</summary>
    public static readonly DiagnosticDescriptor AmbiguousMessageType = new(
        "CHSM1004",
        "메시지 타입 모호",
        "{0} 이(가) IMessageHandler<TMessage> 를 {1}개 타입으로 구현한다. [MessageHandler] 는 대상이 하나여야 한다.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>프레임워크 예약 대역(40001~) 사용 — 앱 코드에서는 경고.</summary>
    public static readonly DiagnosticDescriptor FrameworkReservedRange = new(
        "CHSM1005",
        "프레임워크 예약 대역",
        "메시지 ID {0} 은(는) 프레임워크 예약 대역(40001~65535)이다. 앱 메시지는 1~40000 을 쓴다.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>추상 클래스·제네릭 정의에는 붙일 수 없음.</summary>
    public static readonly DiagnosticDescriptor NotInstantiable = new(
        "CHSM1006",
        "인스턴스화 불가 핸들러",
        "{0} 은(는) 추상 클래스이거나 제네릭 정의라 핸들러로 등록할 수 없다",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);

    /// <summary>핸들러는 있는데 Hosting(빌더) 참조가 없어 맵을 생성하지 못함.</summary>
    public static readonly DiagnosticDescriptor HostingNotReferenced = new(
        "CHSM1007",
        "등록 코드 미생성",
        "[MessageHandler] 핸들러를 발견했지만 이 어셈블리가 ChServerM.Hosting 을 참조하지 않아 등록 코드를 생성하지 않았다. 검증 진단만 적용된다.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUri);
}
