using System;

namespace ChServerM.RealTime;

/// <summary>
/// 원격 시계의 추정·외삽. 상대(대개 서버)의 µs 시각 표본을 받아, 표본 사이를 내 단조 시계로 메운다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 서버 틱 패킷은 드문드문 온다(예: 초당 1회). 그 사이의 서버 시각이
/// 필요할 때마다 패킷을 기다릴 수는 없다 — 마지막 표본에 로컬 경과를 더해 <b>외삽</b>한다.
/// 레거시 <c>ClientM.ServerTickCurrent</c>의 외삽 발상을 승계하되, 주파수 환산 없이
/// µs 고정 단위로 한다(ADR-0063 — <see cref="MicrosecondClock"/> 참조).
/// </para>
/// <para>
/// <b>단조 보장.</b> <see cref="TryGetNowMicros"/>가 내는 값은 <b>절대 뒤로 가지 않는다.</b>
/// 새 표본이 시각을 뒤로 당기면(오프셋 재추정) 출력은 제자리에 멈췄다가 실제 시각이
/// 따라잡으면 다시 흐른다 — 레거시 <c>SendServerTick</c>의 단조 증가 보장 승계.
/// 게임 로직이 "시간이 되돌아갔다"를 보게 하는 것보다 낫다.
/// </para>
/// <para>
/// <b>스레드 규약 — 안전하지 않다.</b> 세션 하나의 소유 실행 컨텍스트(파티션 실행 모델의
/// 유저별 직렬 실행) 전용이다. 레거시 <c>NetWorkDelayM</c>은 이 계약이 없어 공유 정렬
/// 버퍼가 경합으로 깨졌다 — 새 구현은 동기화 대신 소유권으로 푼다(CLAUDE.md 9.1).
/// </para>
/// </remarks>
public sealed class RemoteClock
{
    private readonly TimeProvider _timeProvider;
    private readonly long _frequency;

    private long _baseRemoteMicros;
    private long _baseLocalRaw;
    private long _lastReturnedMicros;
    private bool _hasSample;

    /// <summary>추정기를 만든다. 표본을 받기 전에는 시각을 내지 않는다.</summary>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다.</param>
    public RemoteClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _frequency = timeProvider.TimestampFrequency;
        MicrosecondArithmetic.ValidateFrequency(_frequency);
    }

    /// <summary>표본을 받은 적이 있는지 여부.</summary>
    public bool HasSample => _hasSample;

    /// <summary>원격 시각 표본을 반영한다.</summary>
    /// <param name="remoteMicros">
    /// <b>지금 이 순간</b>의 원격 시각 추정(µs). 대개
    /// "패킷의 t₃ + <see cref="TimeSyncExchange.RoundTripMicros"/>/2" 또는
    /// "내 <see cref="MicrosecondClock"/> 시각 + <see cref="TimeSyncExchange.OffsetMicros"/>"다.
    /// </param>
    /// <remarks>
    /// 이전 표본보다 이른 원격 시각은 무시한다 — 순서가 뒤바뀐 패킷이 기준점을 과거로
    /// 되돌리는 것을 막는다(레거시의 <c>curSendTick &gt; lastSendServerTick</c> 검사 승계).
    /// </remarks>
    public void Update(long remoteMicros)
    {
        if (_hasSample && remoteMicros <= _baseRemoteMicros)
        {
            return;
        }

        _baseRemoteMicros = remoteMicros;
        _baseLocalRaw = _timeProvider.GetTimestamp();
        _hasSample = true;
    }

    /// <summary>현재 원격 시각의 외삽값을 구한다. 반환값은 호출 간에 절대 감소하지 않는다.</summary>
    /// <param name="nowMicros">외삽된 원격 시각(µs). 표본이 없으면 0.</param>
    /// <returns>표본이 하나라도 반영됐으면 <see langword="true"/>.</returns>
    public bool TryGetNowMicros(out long nowMicros)
    {
        if (!_hasSample)
        {
            nowMicros = 0;
            return false;
        }

        long elapsedMicros = MicrosecondArithmetic.ToMicros(
            _timeProvider.GetTimestamp() - _baseLocalRaw, _frequency);
        long computed = _baseRemoteMicros + elapsedMicros;

        // 단조 클램프: 오프셋 재추정으로 시각이 뒤로 당겨져도 출력은 멈출 뿐 되돌아가지 않는다.
        if (computed < _lastReturnedMicros)
        {
            computed = _lastReturnedMicros;
        }
        else
        {
            _lastReturnedMicros = computed;
        }

        nowMicros = computed;
        return true;
    }
}
