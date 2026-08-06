using System;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Resilience;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 커넥션별 토큰 버킷 <see cref="IRateLimiter"/> — 상태를 <c>Connection.Features</c> 에 둔다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 수용된 커넥션 하나가 메시지로 디스패치 파이프라인을 폭주시키는 것을
/// 막는다(버그·악의 클라이언트). 커넥션당 초당 <c>PermitsPerSecond</c> 속도를 넘으면
/// 프레임을 버린다.
/// </para>
/// <para>
/// <b>⚠ 상태를 <c>Connection.Features</c> 에 둔다 — 이 설계의 핵심.</b>
/// 버킷을 전역 맵(커넥션 ID → 버킷)에 두면 유휴 커넥션 항목의 축출 정책이 필요하고
/// 맵 접근에 동기화가 든다. 대신 커넥션별 버킷을 그 커넥션의 <see cref="IFeatureCollection"/>
/// 에 저장하면: (1) 커넥션이 죽으면 버킷도 함께 GC — 축출 불필요, (2) <b>커넥션당 프레임은
/// 순차 디스패치</b>(ADR-0008)라 그 커넥션의 버킷 접근은 겹치지 않는다 — <b>락이 필요 없다</b>
/// (9.1 파티셔닝: 공유를 없애면 동기화가 사라진다). 수용 제어(다중 게시자라 락 필요)와
/// 다른 점이다.
/// </para>
/// <para>
/// <b>인스턴스는 파라미터만 보유한다(무상태).</b> 실제 버킷 상태는 커넥션마다 따로 산다.
/// 그래서 이 인스턴스는 모든 커넥션이 공유해도 안전하다 — 공유하는 것은 불변 파라미터뿐이다.
/// </para>
/// <para><b>할당.</b> 커넥션당 버킷 1회 할당(첫 프레임). 프레임당 할당 0.</para>
/// </remarks>
public sealed class PerConnectionRateLimiter : IRateLimiter
{
    private readonly double _permitsPerSecond;
    private readonly double _burstCapacity;
    private readonly TimeProvider _timeProvider;

    /// <summary>설정을 검증·복사해 만든다.</summary>
    /// <param name="options">커넥션당 토큰 버킷 파라미터.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다. 생략하면 시스템 시계.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public PerConnectionRateLimiter(
        PerConnectionRateLimitOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _permitsPerSecond = options.PermitsPerSecond;
        _burstCapacity = options.BurstCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool TryAcquire(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IFeatureCollection features = context.Connection.Features;
        Bucket? bucket = features.Get<Bucket>();
        if (bucket is null)
        {
            // 커넥션의 첫 프레임 — 버킷을 가득 채워 만든다(정상 진입 버스트 흡수).
            bucket = new Bucket(_burstCapacity, _timeProvider.GetTimestamp());
            features.Set(bucket);
        }

        // 커넥션당 순차 컨텍스트라 락 없이 안전하다(모듈 문서).
        long now = _timeProvider.GetTimestamp();
        double elapsedSeconds = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, now).TotalSeconds;
        if (elapsedSeconds > 0)
        {
            bucket.Tokens = Math.Min(_burstCapacity, bucket.Tokens + (elapsedSeconds * _permitsPerSecond));
            bucket.LastRefillTimestamp = now;
        }

        if (bucket.Tokens >= 1.0)
        {
            bucket.Tokens -= 1.0;
            return true;
        }

        return false;
    }

    /// <summary>커넥션 하나의 토큰 버킷 상태. 순차 컨텍스트 전용이라 동기화가 없다.</summary>
    private sealed class Bucket
    {
        public Bucket(double tokens, long lastRefillTimestamp)
        {
            Tokens = tokens;
            LastRefillTimestamp = lastRefillTimestamp;
        }

        public double Tokens { get; set; }

        public long LastRefillTimestamp { get; set; }
    }
}
