using System;

namespace ChServerM.RealTime;

/// <summary>
/// RTT(왕복 지연) 추정기. 최근 표본 창에서 IQR 이상치를 걷어낸 평균을 낸다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 네트워크 지연은 스파이크가 흔해 단순 평균을 쓸 수 없다. 레거시
/// <c>NetWorkDelayM</c>의 <b>IQR 이상치 제거 발상은 정확했고 그대로 승계한다</b> —
/// 문제는 구현이었다: 공유 정렬 버퍼를 락 없이 써 동시 호출에서 쓰레기 값이 나왔고,
/// <c>_locker</c>를 선언만 하고 쓰지 않았다.
/// </para>
/// <para>
/// <b>스레드 규약 — 안전하지 않다.</b> 새 구현은 동기화가 아니라 <b>소유권</b>으로 푼다
/// (CLAUDE.md 9.1): 세션 하나의 소유 실행 컨텍스트 전용이며, 파티션 실행 모델의 유저별
/// 직렬 실행이 그 보장을 제공한다. 그래서 내부 버퍼에 락도 <c>Concurrent*</c>도 없다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 버퍼는 생성 시 한 번 할당하고 재사용한다 — 표본 추가·계산 모두
/// 정상 상태 무할당이다.
/// </para>
/// <para>
/// <b>한계 명시.</b> <see cref="TryGetOneWayDelay"/>의 편도 = 왕복/2 는 경로 대칭 가정이다.
/// 비대칭 경로에서는 부정확하다(레거시 분석 #6 — 감추지 않고 문서화한다).
/// </para>
/// </remarks>
public sealed class RttEstimator
{
    /// <summary>기본 표본 창 크기. 32.</summary>
    public const int DefaultWindowSize = 32;

    // IQR 판정이 의미를 갖는 최소 표본 수. 미만이면 단순 평균을 쓴다.
    private const int MinSamplesForIqr = 4;

    private readonly long[] _windowMicros;
    private readonly long[] _scratch;
    private int _count;
    private int _nextIndex;

    /// <summary>추정기를 만든다. 버퍼는 여기서 한 번만 할당된다.</summary>
    /// <param name="windowSize">유지할 최근 표본 수. 4~4096.</param>
    /// <exception cref="ArgumentOutOfRangeException">창 크기가 범위 밖일 때.</exception>
    public RttEstimator(int windowSize = DefaultWindowSize)
    {
        if (windowSize is < MinSamplesForIqr or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSize), windowSize, $"창 크기는 {MinSamplesForIqr}~4096 이어야 한다.");
        }

        _windowMicros = new long[windowSize];
        _scratch = new long[windowSize];
    }

    /// <summary>지금까지 반영된 표본 수. 창 크기에서 멈춘다.</summary>
    public int SampleCount => _count;

    /// <summary>왕복 지연 표본을 추가한다. 창이 차면 가장 오래된 표본을 밀어낸다.</summary>
    /// <param name="roundTrip">측정된 왕복 지연.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 음수일 때. 단조 시계에서 음수 왕복은 측정 버그의 신호다 — 0으로 뭉개지 않는다.
    /// </exception>
    public void AddSample(TimeSpan roundTrip)
    {
        if (roundTrip < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundTrip), roundTrip, "왕복 지연은 음수일 수 없다. 측정 경로를 의심하라.");
        }

        _windowMicros[_nextIndex] = roundTrip.Ticks / TimeSpan.TicksPerMicrosecond;
        _nextIndex = (_nextIndex + 1) % _windowMicros.Length;
        if (_count < _windowMicros.Length)
        {
            _count++;
        }
    }

    /// <summary>IQR 이상치를 제거한 평균 왕복 지연을 구한다.</summary>
    /// <param name="smoothed">평활 왕복 지연. 표본이 없으면 <see cref="TimeSpan.Zero"/>.</param>
    /// <returns>표본이 하나라도 있으면 <see langword="true"/>.</returns>
    public bool TryGetSmoothedRtt(out TimeSpan smoothed)
    {
        if (_count == 0)
        {
            smoothed = TimeSpan.Zero;
            return false;
        }

        Array.Copy(_windowMicros, _scratch, _count);

        long sum = 0;
        int used = 0;

        if (_count < MinSamplesForIqr)
        {
            for (int i = 0; i < _count; i++)
            {
                sum += _scratch[i];
            }

            used = _count;
        }
        else
        {
            Array.Sort(_scratch, 0, _count);
            long q1 = _scratch[_count / 4];
            long q3 = _scratch[(_count * 3) / 4];
            long iqr = q3 - q1;
            // 통상 계수 1.5 — 정수 연산으로 iqr + iqr/2.
            long lower = q1 - (iqr + (iqr / 2));
            long upper = q3 + (iqr + (iqr / 2));

            for (int i = 0; i < _count; i++)
            {
                long sample = _scratch[i];
                if (sample >= lower && sample <= upper)
                {
                    sum += sample;
                    used++;
                }
            }
        }

        // used 는 0이 될 수 없다 — Q1~Q3 구간의 표본은 항상 경계 안이다.
        smoothed = TimeSpan.FromTicks((sum / used) * TimeSpan.TicksPerMicrosecond);
        return true;
    }

    /// <summary>편도 지연 추정(왕복/2)을 구한다. 경로 대칭 가정 — 비대칭 경로에서는 부정확하다.</summary>
    /// <param name="delay">편도 지연 추정. 표본이 없으면 <see cref="TimeSpan.Zero"/>.</param>
    /// <returns>표본이 하나라도 있으면 <see langword="true"/>.</returns>
    public bool TryGetOneWayDelay(out TimeSpan delay)
    {
        if (!TryGetSmoothedRtt(out TimeSpan smoothed))
        {
            delay = TimeSpan.Zero;
            return false;
        }

        delay = TimeSpan.FromTicks(smoothed.Ticks / 2);
        return true;
    }
}
