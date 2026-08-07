using System;
using ChServerM.Diagnostics;
using MelEventId = Microsoft.Extensions.Logging.EventId;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ChServerM.Logging.Extensions;

/// <summary>
/// <see cref="IServerLogger"/> 를 <see cref="Microsoft.Extensions.Logging.ILogger"/> 로 잇는
/// 어댑터 (Phase 11, ADR-0030).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이 하나로 로깅 생태계 전체가 열린다.</b> ZLogger·Serilog·콘솔·파일·Seq·
/// Application Insights 는 모두 <b>MEL 프로바이더</b>다. 벤더별 어댑터를 하나씩 만드는 대신
/// 표준 추상화 하나에 붙이면, 프레임워크는 어느 벤더도 알지 않으면서 전부와 호환된다
/// (ADR-0030 — ZLogger 를 직접 물지 않은 근거).
/// </para>
/// <para>
/// <b>왜 이렇게 얇은가 — 시그니처가 같다.</b> <see cref="IServerLogger.Log{TState}"/> 는
/// <c>ILogger.Log&lt;TState&gt;</c> 와 인자 구성이 동일하다(심각도·이벤트 ID·상태·예외·포매터).
/// 그래서 이 어댑터는 <b>열거형과 이벤트 ID 두 개만 옮기고</b> 나머지는 그대로 넘긴다 —
/// 상태를 재포장하지 않으므로 <c>TState</c> 가 구조체면 박싱이 없고, 포매터도 그대로 전달된다.
/// </para>
/// <para>
/// <b>무할당은 어디서 오는가.</b> 이 프레임워크는 <b>프레임당 로깅을 하지 않는다</b> —
/// 로그 지점은 전부 오류·희소 경로이고 모두 <see cref="IServerLogger.IsEnabled"/> 로 걸린다.
/// 따라서 정상 처리 경로의 로깅 비용은 <b>0</b>이며, 실제 방출 시의 문자열 한 번은 희소하다.
/// "방출 시점까지 무할당"(ZLogger 의 강점)은 초당 수만 건을 로깅할 때 의미가 있는데, 그런
/// 설계를 애초에 하지 않았다(ADR-0030).
/// </para>
/// <para>
/// <b>심각도 매핑은 값이 일치한다.</b> Core 의 <see cref="LogLevel"/> 은 MEL 과 같은 순서·값
/// (Trace=0 … Critical=5, None=6)을 쓰므로 캐스팅 한 번이면 된다. 이는 우연이 아니라
/// <b>의도된 정렬</b>이다 — 어긋나면 이 어댑터가 매 호출 분기를 돌아야 한다.
/// </para>
/// <para><b>스레드 규약.</b> 대상 <c>ILogger</c> 가 스레드 안전하면 이 어댑터도 안전하다(MEL 계약).</para>
/// </remarks>
public sealed class MicrosoftServerLogger : IServerLogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    /// <summary>대상 <c>ILogger</c> 로 어댑터를 만든다.</summary>
    /// <param name="logger">기록을 위임할 MEL 로거.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/>가 <see langword="null"/>일 때.</exception>
    public MicrosoftServerLogger(Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level) => _logger.IsEnabled((MelLogLevel)level);

    /// <inheritdoc />
    /// <remarks>
    /// 상태를 재포장하지 않고 그대로 넘긴다 — 구조체 상태의 박싱을 피하는 유일한 방법이다.
    /// </remarks>
    public void Log<TState>(
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _logger.Log(
            (MelLogLevel)level,
            new MelEventId(eventId.Id, eventId.Name),
            state,
            exception,
            formatter);
}
