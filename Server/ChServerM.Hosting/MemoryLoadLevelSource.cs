using System;
using System.Threading;
using ChServerM.Resilience;

namespace ChServerM.Hosting;

/// <summary>
/// GC 가 보고하는 메모리 압박을 <see cref="LoadLevel"/> 로 바꾸는 부하 소스 (Phase 10, ADR-0029).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 서버가 힘들다는 가장 보편적인 신호는 메모리 압박이다 — 큐 깊이·지연은
/// 워크로드마다 의미가 다르지만, "쓸 수 있는 메모리가 얼마 안 남았다" 는 어느 조립에서나 같은
/// 뜻이다. ROADMAP 의 "전체 메모리 워터마크" 항목이 곧 이것이며, 열화의 첫 입력이 된다.
/// </para>
/// <para>
/// <b>측정은 캐시한다 — 프레임마다 GC 정보를 묻지 않는다.</b>
/// <see cref="GC.GetGCMemoryInfo(GCKind)"/> 는 공짜가 아닌데 <see cref="Current"/> 는 프레임마다
/// 읽힌다. 그래서 <see cref="RefreshInterval"/> 마다 한 번만 갱신하고 그 사이에는 캐시된 값을
/// 돌려준다 — 계약이 "지금 계산하라" 가 아니라 "지금 값을 알려달라" 인 이유다
/// (<see cref="ILoadLevelSource"/> 핫패스 규약).
/// </para>
/// <para>
/// <b>무엇을 재는가.</b> <c>MemoryLoadBytes / HighMemoryLoadThresholdBytes</c> 비율을 쓴다.
/// 분모는 <b>런타임이 "높은 메모리 부하" 로 보는 지점</b>(보통 물리 메모리의 90%, 컨테이너면
/// cgroup 한도 기준)이라, <b>컨테이너 메모리 제한을 자동으로 따른다</b> — 절대 바이트 수를
/// 설정으로 박으면 배포 환경이 바뀔 때마다 틀린다.
/// </para>
/// <para>
/// <b>⚠ 이 신호는 느리고 계단식이다.</b> GC 가 정보를 갱신하는 시점에 의존하므로 급격한 할당
/// 폭주를 즉시 잡지 못한다(ADR-0021 이 워터마크를 미룰 때 지적한 약점 그대로다). 그래서 이것은
/// <b>유일한 방어가 아니라</b> 수용 제어·속도 제한 뒤에 서는 마지막 완충이며, 임계는 여유 있게
/// (기본 Elevated 0.75 / Critical 0.90) 잡는다.
/// </para>
/// <para>
/// <b>테스트 결정성은 계약 분리로 얻는다.</b> ADR-0021 이 워터마크를 미룬 이유 중 하나가 GC
/// 시점 의존이었는데, 열화 로직은 <see cref="ILoadLevelSource"/> 를 받으므로 가짜 소스로
/// 결정적으로 검증된다 — 측정과 정책이 분리돼 있어 가능한 일이다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다. 갱신이 겹치면 둘 다 같은 값을 쓸 뿐이라 무해하다.</para>
/// </remarks>
public sealed class MemoryLoadLevelSource : ILoadLevelSource
{
    /// <summary>기본 갱신 주기.</summary>
    /// <remarks>1초면 GC 정보 조회 비용이 무시할 수준이면서 부하 변화를 놓치지 않는다.</remarks>
    public static TimeSpan DefaultRefreshInterval => TimeSpan.FromSeconds(1);

    private readonly double _elevatedRatio;
    private readonly double _criticalRatio;
    private readonly long _refreshIntervalTicks;
    private readonly TimeProvider _timeProvider;

    private long _lastSampleTimestamp;
    private volatile int _cached; // LoadLevel 을 int 로 — volatile 은 enum 에 못 붙는다.

    /// <summary>임계값으로 부하 소스를 만들고 즉시 한 번 측정한다.</summary>
    /// <param name="elevatedRatio">이 비율을 넘으면 <see cref="LoadLevel.Elevated"/>. 기본 0.75.</param>
    /// <param name="criticalRatio">이 비율을 넘으면 <see cref="LoadLevel.Critical"/>. 기본 0.90.</param>
    /// <param name="refreshInterval">갱신 주기. <see langword="null"/>이면 <see cref="DefaultRefreshInterval"/>.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다.</param>
    /// <exception cref="InvalidOperationException">임계값이 유효하지 않을 때.</exception>
    public MemoryLoadLevelSource(
        double elevatedRatio = 0.75,
        double criticalRatio = 0.90,
        TimeSpan? refreshInterval = null,
        TimeProvider? timeProvider = null)
    {
        if (elevatedRatio is <= 0 or >= 1 || !double.IsFinite(elevatedRatio))
        {
            throw new InvalidOperationException(
                $"{nameof(elevatedRatio)} 는 0 과 1 사이여야 한다. 현재 값: {elevatedRatio}");
        }

        if (criticalRatio <= elevatedRatio || criticalRatio >= 1 || !double.IsFinite(criticalRatio))
        {
            throw new InvalidOperationException(
                $"{nameof(criticalRatio)} 는 {nameof(elevatedRatio)}({elevatedRatio}) 보다 크고 1 미만이어야 한다. " +
                $"현재 값: {criticalRatio}");
        }

        TimeSpan interval = refreshInterval ?? DefaultRefreshInterval;
        if (interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(refreshInterval)} 는 0보다 커야 한다. 현재 값: {interval}");
        }

        _elevatedRatio = elevatedRatio;
        _criticalRatio = criticalRatio;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshIntervalTicks = (long)(interval.TotalSeconds * _timeProvider.TimestampFrequency);

        // 첫 값을 즉시 채운다 — 시작 직후 Normal 로 오인하지 않게.
        _lastSampleTimestamp = _timeProvider.GetTimestamp();
        _cached = (int)Sample();
    }

    /// <summary>갱신 주기.</summary>
    public TimeSpan RefreshInterval =>
        TimeSpan.FromSeconds((double)_refreshIntervalTicks / _timeProvider.TimestampFrequency);

    /// <inheritdoc />
    public LoadLevel Current
    {
        get
        {
            long now = _timeProvider.GetTimestamp();
            long last = Interlocked.Read(ref _lastSampleTimestamp);

            if (now - last >= _refreshIntervalTicks)
            {
                // 갱신 권한을 CAS 로 한 스레드만 가져간다 — 나머지는 캐시된 값을 쓴다.
                // 겹쳐 들어와도 결과가 같으므로 실패한 쪽이 재시도할 이유가 없다.
                if (Interlocked.CompareExchange(ref _lastSampleTimestamp, now, last) == last)
                {
                    _cached = (int)Sample();
                }
            }

            return (LoadLevel)_cached;
        }
    }

    private LoadLevel Sample()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();

        // 런타임이 아직 임계를 모르면(첫 GC 전) 판단하지 않는다 — 측정 실패로 정상 트래픽을
        // 버리는 것이 더 나쁘다(ILoadLevelSource 의 "실패는 낙관" 규약).
        if (info.HighMemoryLoadThresholdBytes <= 0)
        {
            return LoadLevel.Normal;
        }

        double ratio = (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes;

        if (ratio >= _criticalRatio)
        {
            return LoadLevel.Critical;
        }

        return ratio >= _elevatedRatio ? LoadLevel.Elevated : LoadLevel.Normal;
    }
}
