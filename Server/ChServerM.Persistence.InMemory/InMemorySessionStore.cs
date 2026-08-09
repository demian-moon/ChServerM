using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;

namespace ChServerM.Persistence.InMemory;

/// <summary>
/// <see cref="ISessionStore"/> 의 인메모리 참조 구현 — 낙관적 동시성(CAS)과 만료를 지원한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 세션 축의 <b>참조 구현</b>이다(CLAUDE.md 3절: Core 인터페이스 → 참조 구현
/// 1개 → 벤치마크 → 두 번째 구현). 외부 저장소 없이 <c>realtime-stateful</c> 프로필을 세울 수
/// 있게 하고, 동시에 Redis 어댑터가 지켜야 할 <b>의미의 기준</b> 역할을 한다 — 두 구현이
/// 같은 테스트를 통과해야 축이 성립한다.
/// </para>
///
/// <para>
/// <b>⚠ 값 의미를 지킨다.</b> 쓰기는 넘어온 바이트를 <b>복사해</b> 보관하고, 읽기는 보관본을
/// 대상에 <b>복사해</b> 준다. 호출자에게 내부 배열을 노출하지 않는다. 이것이 Redis 어댑터와
/// 동작을 같게 만드는 유일한 방법이며, 계약이 바이트인 이유이기도 하다
/// (<see cref="ISessionStore"/> 문서).
/// </para>
///
/// <para>
/// <b>스레드 규약 — 스레드 안전하다.</b> <see cref="ConcurrentDictionary{TKey,TValue}"/> 위에
/// 세우고, 항목 교체는 <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> 의 원자적
/// 비교-교체로 한다. <b>직접 샤딩하지 않는 이유</b>: 이 사전은 이미 내부적으로 스트라이핑돼
/// 있어 그 위에 우리 샤딩을 얹으면 같은 일을 두 번 하는 것이다(9.1 의 파티셔닝은 <i>공유를
/// 없앨 수 있을 때</i> 쓰는 전략인데, 세션 저장소는 임의 실행 컨텍스트에서 조회되므로 공유를
/// 없앨 수 없다).
/// </para>
///
/// <para>
/// <b>수명·소유권 규약.</b> 저장 항목은 <see cref="ArrayPool{T}"/> 대여가 아니라 <b>정확한
/// 크기의 배열</b>이다. 세션 항목은 <b>오래 산다</b> — 대여 버퍼를 장기 보유하면 풀이 고갈되고,
/// 반납 시점(덮어쓰기·삭제·만료·청소)이 넷으로 흩어져 누락이 반드시 생긴다. 풀은 단명 버퍼를
/// 위한 도구다(레거시 반납 누수의 부류를 피한다).
/// </para>
///
/// <para>
/// <b>버전 규약.</b> 저장소 전역 단조 증가 카운터를 쓴다. 키별 카운터가 아니라 전역인 이유는
/// <b>ABA 방지</b>다 — 항목이 만료·삭제된 뒤 다시 만들어져도 이전 버전이 재사용되지 않는다
/// (<see cref="SessionVersion"/> 계약 2번).
/// </para>
///
/// <para>
/// <b>만료 규약.</b> 읽기·쓰기 시점의 <b>지연 판정</b>과 주기적 <b>청소</b>를 함께 쓴다.
/// 지연 판정만으로는 다시 조회되지 않는 세션이 영원히 남는다
/// (<see cref="InMemorySessionStoreOptions.SweepInterval"/> 문서).
/// </para>
/// </remarks>
public sealed class InMemorySessionStore : ISessionStore, IDisposable
{
    private readonly ConcurrentDictionary<SessionId, Entry> _entries;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _sweepTimer;

    /// <summary>전역 단조 버전 카운터. 0 은 <see cref="SessionVersion.None"/> 이 쓰므로 1 부터 나간다.</summary>
    private ulong _versionCounter;

    private volatile bool _disposed;

    /// <summary>세션 저장소를 만든다.</summary>
    /// <param name="options">설정. <see langword="null"/> 이면 기본값.</param>
    /// <param name="timeProvider">시간 원천. <see langword="null"/> 이면 <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않다.</exception>
    public InMemorySessionStore(
        InMemorySessionStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        InMemorySessionStoreOptions effective = options ?? new InMemorySessionStoreOptions();
        effective.Validate();

        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = new ConcurrentDictionary<SessionId, Entry>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: effective.InitialCapacity);

        if (effective.SweepInterval is { } interval)
        {
            // 저장소당 타이머 하나. 세션마다 만들지 않는다(9.5).
            _sweepTimer = _timeProvider.CreateTimer(
                static state => ((InMemorySessionStore)state!).Sweep(),
                this,
                interval,
                interval);
        }
    }

    /// <summary>현재 보관 중인 항목 수(만료됐지만 아직 청소되지 않은 것을 포함).</summary>
    /// <remarks>진단·테스트용이다. 만료 판정을 하지 않으므로 "살아 있는 세션 수" 가 아니다.</remarks>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public ValueTask<SessionReadResult> TryReadAsync(
        SessionId id,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<SessionReadResult>(cancellationToken);
        }

        if (!_entries.TryGetValue(id, out Entry? entry) || IsExpired(entry))
        {
            // ⚠ 찾지 못하면 대상을 건드리지 않는다(계약).
            return ValueTask.FromResult(SessionReadResult.NotFound);
        }

        // 항목은 불변이다 — State 배열은 만들어진 뒤 바뀌지 않으므로 락 없이 읽어도 안전하다.
        // (덮어쓰기는 새 Entry 로 교체한다. 제자리 수정이 아니다.)
        byte[] state = entry.State;
        destination.Write(state);

        return ValueTask.FromResult(SessionReadResult.Hit(entry.Version, state.Length));
    }

    /// <inheritdoc/>
    public ValueTask<SessionWriteResult> TryWriteAsync(
        SessionId id,
        ReadOnlyMemory<byte> state,
        SessionVersion expectedVersion,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfInvalidTtl(timeToLive);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<SessionWriteResult>(cancellationToken);
        }

        long expiresAt = ComputeExpiry(timeToLive);

        // ⚠ 복사는 **버전 검사를 통과한 뒤에** 한다.
        //
        // 처음에는 루프 앞에서 무조건 복사했는데, 그러면 충돌로 거부되는 호출도 상태 전체를
        // 복사한 뒤 버렸다(1KB 상태에서 1,048 B). **거부 경로가 성공 경로만큼 비싸면 안 된다** —
        // 경합이 심할수록 충돌이 늘어나므로 정확히 부하가 높을 때 GC 압력이 커진다
        // (열화 거부 경로를 무할당으로 고정한 것과 같은 논리).
        // 재시도 루프에서는 만들어 둔 사본을 재사용한다 — 우리 것이므로 안전하다.
        byte[]? copy = null;

        while (true)
        {
            if (_entries.TryGetValue(id, out Entry? existing) && !IsExpired(existing))
            {
                if (existing.Version != expectedVersion)
                {
                    return ValueTask.FromResult(SessionWriteResult.Conflict);
                }

                copy ??= state.ToArray();
                SessionVersion next = NextVersion();
                Entry replacement = new(copy, next, expiresAt);

                if (_entries.TryUpdate(id, replacement, existing))
                {
                    return ValueTask.FromResult(SessionWriteResult.Ok(next));
                }

                // 그 사이 다른 스레드가 바꿨다. 다시 읽고 판정한다 — 재시도할 때만 스핀한다(9.3).
                continue;
            }

            // 없거나 만료됐다. 이때의 기대 버전은 None 이어야 한다("아직 없을 때만 만들어라").
            if (expectedVersion != SessionVersion.None)
            {
                return ValueTask.FromResult(SessionWriteResult.Conflict);
            }

            copy ??= state.ToArray();
            SessionVersion created = NextVersion();
            Entry fresh = new(copy, created, expiresAt);

            if (existing is null)
            {
                if (_entries.TryAdd(id, fresh))
                {
                    return ValueTask.FromResult(SessionWriteResult.Ok(created));
                }
            }
            else if (_entries.TryUpdate(id, fresh, existing))
            {
                // 만료된 항목을 새 항목으로 교체했다.
                return ValueTask.FromResult(SessionWriteResult.Ok(created));
            }

            // 경합 — 다시 판정한다. 소비한 버전은 버린다(단조 증가만 지키면 되므로 문제없다).
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryRemoveAsync(
        SessionId id,
        SessionVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        if (!_entries.TryGetValue(id, out Entry? existing) || IsExpired(existing))
        {
            return ValueTask.FromResult(false);
        }

        if (existing.Version != expectedVersion)
        {
            return ValueTask.FromResult(false);
        }

        // 키-값 쌍으로 지운다 — 그 사이 값이 바뀌었으면 지우지 않는다(원자적 CAS 삭제).
        return ValueTask.FromResult(
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<SessionId, Entry>>)_entries)
                .Remove(new System.Collections.Generic.KeyValuePair<SessionId, Entry>(id, existing)));
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryRenewAsync(
        SessionId id,
        SessionVersion expectedVersion,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfInvalidTtl(timeToLive);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        long expiresAt = ComputeExpiry(timeToLive);

        while (true)
        {
            if (!_entries.TryGetValue(id, out Entry? existing) || IsExpired(existing))
            {
                return ValueTask.FromResult(false);
            }

            if (existing.Version != expectedVersion)
            {
                return ValueTask.FromResult(false);
            }

            // ⚠ 버전을 올리지 않는다 — 상태가 바뀌지 않았으므로 다른 주체의 CAS 를 깨면 안 된다(계약).
            //
            // ⚠ 항목을 교체하지 않고 **만료 시각만 제자리 갱신**한다. 처음에는 새 Entry 를
            // 만들어 TryUpdate 했는데 하트비트마다 40 B 를 할당했다 — 만료를 미루자고 객체를
            // 만드는 것은 이 메서드의 존재 이유(상태를 다시 안 보내려고 만들었다)와 모순이다.
            //
            // 경쟁 상황은 전부 무해하다: 그 사이 다른 쓰기가 항목을 교체했다면 우리는 버려진
            // 객체를 갱신한 것이고, 새 항목은 그 쓰기가 정한 만료를 갖는다. 삭제됐다면 역시
            // 버려진 객체다. 어느 쪽도 살아 있는 상태를 오염시키지 않는다.
            existing.Renew(expiresAt);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>만료된 항목을 실제로 걷어낸다. 타이머가 호출한다.</summary>
    /// <remarks>
    /// <b>항목별로 원자적 삭제</b>를 쓴다 — 순회 중에 다른 스레드가 같은 키를 갱신했다면
    /// 지우지 않는다. 순회 자체는 <see cref="ConcurrentDictionary{TKey,TValue}"/> 의 스냅샷
    /// 열거이므로 락이 필요 없다.
    /// </remarks>
    private void Sweep()
    {
        if (_disposed)
        {
            return;
        }

        long now = _timeProvider.GetUtcNow().UtcTicks;

        foreach (System.Collections.Generic.KeyValuePair<SessionId, Entry> pair in _entries)
        {
            if (pair.Value.ExpiresAtTicks != NeverExpires && pair.Value.ExpiresAtTicks <= now)
            {
                ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<SessionId, Entry>>)_entries)
                    .Remove(pair);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sweepTimer?.Dispose();
        _entries.Clear();
    }

    private const long NeverExpires = long.MaxValue;

    private SessionVersion NextVersion() => new(Interlocked.Increment(ref _versionCounter));

    private bool IsExpired(Entry entry) =>
        entry.ExpiresAtTicks != NeverExpires && entry.ExpiresAtTicks <= _timeProvider.GetUtcNow().UtcTicks;

    private long ComputeExpiry(TimeSpan? timeToLive) =>
        timeToLive is { } ttl ? _timeProvider.GetUtcNow().UtcTicks + ttl.Ticks : NeverExpires;

    private static void ThrowIfInvalidTtl(TimeSpan? timeToLive)
    {
        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive), ttl, "만료 시간은 0 보다 커야 한다(만료 없음은 null).");
        }
    }

    /// <summary>
    /// 보관 항목. <b>상태와 버전은 불변</b>이고 교체로만 갱신된다 — 그래서 읽기가 락 없이 안전하다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 참조 타입인 이유: <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> 의 비교가
    /// 참조 동등성으로 동작해야 <b>"내가 본 그 항목일 때만 교체"</b> 가 성립한다. 구조체면
    /// 값 비교가 되어 내용이 같은 다른 세대와 구분되지 않는다.
    /// </para>
    /// <para>
    /// <b>⚠ 만료 시각만 가변이다.</b> 연장(<c>TryRenewAsync</c>)이 객체를 만들지 않게 하기
    /// 위해서다. <b>불변인 것은 <see cref="State"/> 와 <see cref="Version"/></b> 이고, 락 없는
    /// 읽기와 CAS 가 의존하는 것도 그 둘뿐이다 — 만료는 판정에만 쓰이므로 원자적 읽기/쓰기로
    /// 충분하다(9.3: 여러 스레드가 보는 필드는 <see cref="Volatile"/> 을 일관 적용한다).
    /// </para>
    /// </remarks>
    private sealed class Entry(byte[] state, SessionVersion version, long expiresAtTicks)
    {
        private long _expiresAtTicks = expiresAtTicks;

        public byte[] State { get; } = state;

        public SessionVersion Version { get; } = version;

        public long ExpiresAtTicks => Volatile.Read(ref _expiresAtTicks);

        /// <summary>만료 시각을 제자리에서 갱신한다. 상태·버전은 건드리지 않는다.</summary>
        public void Renew(long expiresAtTicks) => Volatile.Write(ref _expiresAtTicks, expiresAtTicks);
    }
}
