using System;

namespace ChServerM.Diagnostics;

/// <summary>
/// 프레임워크가 쓰는 최소 로깅 계약.
/// </summary>
/// <remarks>
/// <para>
/// 모양을 <c>Microsoft.Extensions.Logging.ILogger</c>와 <b>일부러 맞췄다.</b>
/// 어댑터가 위임 한 줄로 끝나야 하기 때문이다. Core는 그 패키지를 참조하지 않는다
/// (무의존 하드 룰) — 로깅 축도 교체 가능해야 한다는 뜻이기도 하다.
/// </para>
/// <para>
/// <b>무할당 규약.</b> 상태 타입이 구조체면 제네릭 특수화로 박싱이 없다.
/// 포맷터는 <b>정적 람다를 캐시해서</b> 넘긴다. 그러면 로그 호출당
/// 힙 할당이 0이 되고, 레벨이 꺼져 있으면 문자열이 아예 만들어지지 않는다.
/// </para>
/// <para>
/// 핫패스에서는 반드시 <see cref="IsEnabled"/>로 먼저 거른다. 인자 계산 자체가 비용이다.
/// </para>
/// <para>
/// 레거시는 <c>Debug.WriteLine</c>과 무레벨 파일 로거가 섞여 있었고, Release 빌드에서
/// 진단이 통째로 사라졌다. 설정 파일이 없으면 로깅이 <b>조용히</b> 꺼졌다.
/// </para>
/// </remarks>
public interface IServerLogger
{
    /// <summary>해당 심각도가 기록되는지 검사한다.</summary>
    /// <param name="level">검사할 심각도.</param>
    /// <returns>기록된다면 <see langword="true"/>.</returns>
    /// <remarks>핫패스에서는 로그 인자를 만들기 <b>전에</b> 이것을 호출한다.</remarks>
    bool IsEnabled(LogLevel level);

    /// <summary>로그 항목을 기록한다.</summary>
    /// <typeparam name="TState">로그에 실을 상태. 구조체면 박싱이 없다.</typeparam>
    /// <param name="level">심각도.</param>
    /// <param name="eventId">이벤트 식별자.</param>
    /// <param name="state">구조화 로깅에 실릴 상태.</param>
    /// <param name="exception">관련 예외. 없으면 <see langword="null"/>.</param>
    /// <param name="formatter">
    /// <paramref name="state"/>를 사람이 읽는 문구로 바꾸는 함수.
    /// <b>정적 필드에 캐시한 람다</b>를 넘긴다. 호출 지점에서 새로 만들면 할당이 생긴다.
    /// </param>
    void Log<TState>(
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter);
}

/// <summary>
/// 아무것도 기록하지 않는 <see cref="IServerLogger"/>.
/// </summary>
/// <remarks>
/// 로거를 주입받지 못한 경로가 <see langword="null"/> 검사로 지저분해지지 않게 한다.
/// <see cref="IsEnabled"/>가 항상 <see langword="false"/>이므로 호출자가
/// 규약대로 거르면 인자 계산 비용도 들지 않는다.
/// </remarks>
public sealed class NullServerLogger : IServerLogger
{
    /// <summary>공유 인스턴스.</summary>
    public static NullServerLogger Instance { get; } = new();

    private NullServerLogger()
    {
    }

    /// <inheritdoc />
    /// <returns>언제나 <see langword="false"/>.</returns>
    public bool IsEnabled(LogLevel level) => false;

    /// <inheritdoc />
    /// <remarks>아무것도 하지 않는다.</remarks>
    public void Log<TState>(
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // 의도적으로 비어 있다.
    }
}
