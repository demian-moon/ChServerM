using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ChServerM.Diagnostics;
using ChServerM.Security;

namespace ChServerM.Hosting;

/// <summary>
/// <see cref="ITokenReplayGuard"/>의 단일 노드 인메모리 구현 (T-05).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 캡처한 로그인 토큰의 크로스 커넥션 재사용(ADR-0017 결정 4가 인증
/// 계층에 배정한 잔여 위협)을 단일 노드 범위에서 막는 참조 구현이다. 스케일아웃에서는
/// 외부 저장소 어댑터(Phase 13+)가 같은 계약 뒤에 끼워진다.
/// </para>
/// <para>
/// <b>유계다(9.6).</b> 등록부가 <see cref="TokenReplayGuardOptions.MaxEntries"/> 에 차면
/// 만료 항목을 정리하고, 그래도 차 있으면 <b>신규 클레임을 거부한다</b> — 거부가 붕괴보다
/// 낫다. 포화 거부는 재사용 거부와 로그로 구분된다(조용한 거부 금지). 계약의 순서 규약
/// (검증 통과 후에만 클레임)이 지켜지는 한, 포화를 만들려면 유효 토큰이 대량으로 필요하다.
/// </para>
/// <para>
/// <b>TTL 은 만료 검증의 대체가 아니다.</b> 등록부 메모리를 유계로 만드는 장치다.
/// 토큰 수명보다 짧게 설정하면 TTL 축출 후 아직 유효한 토큰이 재사용 가능해진다 —
/// 반드시 토큰 수명 이상으로 둔다(<see cref="ITokenReplayGuard"/> 계약 문서).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다. 같은 토큰의 동시 클레임 경쟁은
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 의 원자적 <c>TryAdd</c>/<c>TryUpdate</c> 로
/// 정확히 하나만 이긴다. 정리(sweep)는 전용 타이머 없이(9.5) 포화 시점에만 돌며,
/// <see cref="Interlocked"/> 게이트로 한 번에 하나만 돈다 — 게이트 해제는 <c>finally</c> 다
/// (9.2: 이것을 빠뜨리면 예외 하나가 정리를 영구 정지시킨다).
/// </para>
/// <para>
/// <b>할당.</b> 클레임당 토큰 복사 1회(키 보관용). 인증은 커넥션당 1회라 핫패스가 아니다 —
/// 해시 라이브러리 의존을 추가하는 것보다 복사가 싸다.
/// </para>
/// </remarks>
public sealed class InMemoryTokenReplayGuard : ITokenReplayGuard
{
    private static readonly EventId ReplayRejectedEvent = new(6000, "TokenReplayRejected");
    private static readonly EventId GuardSaturatedEvent = new(6003, "TokenReplayGuardSaturated");

    private readonly ConcurrentDictionary<byte[], long> _entries;
    private readonly int _maxEntries;
    private readonly long _ttlTimestampTicks;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;

    // 0 = 유휴, 1 = 정리 중. 한 스레드만 정리한다 — 나머지는 결과만 재확인한다.
    private int _sweeping;

    /// <summary>설정을 검증·복사해 등록부를 만든다.</summary>
    /// <param name="options">유계·TTL 설정. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다. 생략하면 시스템 시계.</param>
    /// <param name="logger">진단 로거. 생략하면 기록하지 않는다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public InMemoryTokenReplayGuard(
        TokenReplayGuardOptions options,
        TimeProvider? timeProvider = null,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxEntries = options.MaxEntries;
        _ttlTimestampTicks = (long)(options.Ttl.TotalSeconds * _timeProvider.TimestampFrequency);
        _logger = logger ?? NullServerLogger.Instance;
        _entries = new ConcurrentDictionary<byte[], long>(TokenComparer.Instance);
    }

    /// <summary>현재 등록된 항목 수. 관측·테스트용.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public bool TryClaim(ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty)
        {
            // 빈 토큰은 형식 위반이다 — "모든 빈 토큰은 같은 토큰"이 되어 두 번째부터
            // 전부 거부되는 혼란보다, 첫 번째부터 일관되게 거부하는 편이 진단 가능하다.
            return false;
        }

        long now = _timeProvider.GetTimestamp();

        if (_entries.Count >= _maxEntries)
        {
            SweepExpired(now);

            if (_entries.Count >= _maxEntries)
            {
                LogSaturated();
                return false;
            }
        }

        byte[] key = token.ToArray();
        long expiry = now + _ttlTimestampTicks;

        if (_entries.TryAdd(key, expiry))
        {
            return true;
        }

        // 이미 있는 항목 — TTL 이 지났으면 죽은 항목이므로 첫 사용으로 되살린다.
        // TryUpdate 의 비교 값 덕분에 동시 경쟁에서도 정확히 하나만 이긴다.
        if (_entries.TryGetValue(key, out long existing)
            && existing < now
            && _entries.TryUpdate(key, expiry, existing))
        {
            return true;
        }

        LogReplayRejected();
        return false;
    }

    /// <summary>만료 항목을 정리한다. 포화 시점에만 호출된다 — 전용 타이머를 두지 않는다(9.5).</summary>
    private void SweepExpired(long now)
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
        {
            // 다른 스레드가 정리 중이다. 기다리지 않는다 — 호출자가 Count 를 재확인한다.
            return;
        }

        try
        {
            foreach (KeyValuePair<byte[], long> entry in _entries)
            {
                if (entry.Value < now)
                {
                    // 값까지 일치할 때만 제거 — 방금 되살아난 항목을 지우지 않는다.
                    ((ICollection<KeyValuePair<byte[], long>>)_entries).Remove(entry);
                }
            }
        }
        finally
        {
            // 9.2 — 해제를 finally 에 두지 않으면 열거 중 예외 하나가 정리를 영구 정지시킨다.
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private void LogReplayRejected()
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                ReplayRejectedEvent,
                0,
                null,
                static (_, _) => "토큰 재사용을 거부했다 — 크로스 커넥션 리플레이 시도이거나 클라이언트 결함이다(T-05).");
        }
    }

    private void LogSaturated()
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                GuardSaturatedEvent,
                _maxEntries,
                null,
                static (max, _) =>
                    $"리플레이 등록부가 포화({max})라 신규 클레임을 거부했다. " +
                    "정상 부하라면 MaxEntries 를 올리고, 아니라면 유효 토큰 대량 발급 경로를 의심한다.");
        }
    }

    /// <summary>토큰 바이트의 구조적 동등성 비교자.</summary>
    private sealed class TokenComparer : IEqualityComparer<byte[]>
    {
        public static TokenComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            HashCode hash = default;
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
