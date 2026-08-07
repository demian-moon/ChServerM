using System;
using ChServerM.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ChServerM.Logging.Extensions;

/// <summary>
/// MEL 로거를 ChServerM 로깅 축에 붙이는 편의 확장 (Phase 11, ADR-0030).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 호스트는 대개 <see cref="ILoggerFactory"/> 를 이미 갖고 있다(ASP.NET Core
/// 호스팅·Generic Host·직접 구성). 그 자산을 그대로 프레임워크에 넘기는 한 줄을 제공해,
/// 소비자가 어댑터 타입을 직접 알 필요가 없게 한다.
/// </para>
/// <para>
/// <b>범주 이름을 준다.</b> <see cref="ILoggerFactory"/> 로 만들 때는 프레임워크 이름을
/// 범주로 쓴다 — 프로바이더의 필터 규칙(<c>"ChServerM": "Warning"</c>)이 프레임워크 로그만
/// 따로 조절할 수 있어야 운영이 편하다.
/// </para>
/// </remarks>
public static class ServerLoggerExtensions
{
    /// <summary>프레임워크 로그의 기본 범주 이름.</summary>
    /// <remarks>
    /// 프로바이더 설정에서 이 이름으로 프레임워크 로그만 필터링한다.
    /// <see cref="DiagnosticNames.MeterName"/>·<c>ActivitySourceName</c> 과 같은 이름을 써
    /// 메트릭·추적·로그가 한 이름으로 묶이게 한다.
    /// </remarks>
    public const string DefaultCategory = DiagnosticNames.ActivitySourceName;

    /// <summary>MEL 로거를 ChServerM 로깅 축 어댑터로 감싼다.</summary>
    /// <param name="logger">대상 MEL 로거.</param>
    /// <returns>프레임워크에 넘길 수 있는 <see cref="IServerLogger"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/>가 <see langword="null"/>일 때.</exception>
    public static IServerLogger ToServerLogger(this ILogger logger) => new MicrosoftServerLogger(logger);

    /// <summary>팩터리에서 프레임워크 범주의 로거를 만들어 어댑터로 감싼다.</summary>
    /// <param name="factory">호스트가 구성한 로거 팩터리.</param>
    /// <param name="category">범주 이름. 생략하면 <see cref="DefaultCategory"/>.</param>
    /// <returns>프레임워크에 넘길 수 있는 <see cref="IServerLogger"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 표준 사용법이다 — <c>builder.UseLogger(loggerFactory.CreateServerLogger())</c>.
    /// 프로바이더(ZLogger·Serilog·콘솔…)는 호스트가 <paramref name="factory"/> 에 이미 꽂아 뒀다.
    /// </remarks>
    public static IServerLogger CreateServerLogger(this ILoggerFactory factory, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new MicrosoftServerLogger(factory.CreateLogger(category ?? DefaultCategory));
    }
}
