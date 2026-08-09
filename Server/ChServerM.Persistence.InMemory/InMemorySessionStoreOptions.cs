using System;

namespace ChServerM.Persistence.InMemory;

/// <summary>
/// <see cref="InMemorySessionStore"/> 설정.
/// </summary>
/// <remarks>
/// 값은 조립 시점(생성자)에 검증한다 — 잘못된 설정이 부하 중에 드러나면 이미 늦다.
/// </remarks>
public sealed class InMemorySessionStoreOptions
{
    /// <summary>만료 항목 청소 주기의 기본값.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 만료된 항목을 실제로 걷어내는 주기. <see langword="null"/> 이면 청소하지 않는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ 지연 만료만으로는 새는 것을 막지 못한다.</b> 만료를 읽는 시점에만 판정하면
    /// <b>다시 조회되지 않는 세션</b>은 영원히 사전에 남는다 — 끊긴 클라이언트의 상태가
    /// 정확히 그런 항목이고, 그것이 쌓이는 것이 곧 OOM 이다. "만료" 를 계약에 넣은 이상
    /// 실제로 걷어내는 장치가 있어야 계약이 참이 된다.
    /// </para>
    /// <para>
    /// <b>타이머는 저장소당 하나뿐이다</b> — 세션마다 타이머를 만들지 않는다(CLAUDE.md 9.5,
    /// 레거시가 커넥션마다 타이머를 만들어 스레드풀 작업을 늘린 사례).
    /// </para>
    /// <para>
    /// <see langword="null"/> 로 끄는 것은 테스트나 만료를 아예 안 쓰는 조립을 위한 선택지다.
    /// </para>
    /// </remarks>
    public TimeSpan? SweepInterval { get; set; } = DefaultSweepInterval;

    /// <summary>
    /// 예상 세션 수. 내부 사전의 초기 용량으로 쓴다.
    /// </summary>
    /// <remarks>
    /// 성능 힌트일 뿐 상한이 아니다. 크게 잡으면 초기 메모리를, 작게 잡으면 초기 재해싱을 낸다.
    /// </remarks>
    public int InitialCapacity { get; set; } = 1024;

    /// <summary>설정을 검증한다. 위반이면 던진다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났다.</exception>
    public void Validate()
    {
        if (SweepInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(SweepInterval)} 은 0 보다 커야 한다(끄려면 null). 현재: {interval}");
        }

        if (InitialCapacity < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(InitialCapacity)} 은 음수일 수 없다. 현재: {InitialCapacity}");
        }
    }
}
