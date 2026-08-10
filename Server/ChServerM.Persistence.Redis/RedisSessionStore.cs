using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;
using StackExchange.Redis;

namespace ChServerM.Persistence.Redis;

/// <summary>
/// <see cref="ISessionStore"/> 의 Redis 어댑터 — 축의 <b>두 번째 구현</b>이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 두 번째 구현이 나오기 전까지 추상화는 가설이다(CLAUDE.md 3절).
/// 이 어댑터가 <see cref="ISessionStore"/> 를 가설에서 계약으로 만든다 — 인메모리 참조
/// 구현과 <b>같은 적합성 테스트</b>를 통과해야 하며, 통과하지 못하면 축 교체가 성립하지
/// 않는다(ADR-0004).
/// </para>
///
/// <para>
/// <b>⚠ 값 배치 — 버전을 값 안에 넣는다.</b> 키 하나에
/// <c>[8바이트 버전 LE][상태 바이트]</c> 를 담는다. 버전을 별도 키로 두면 상태와 버전이
/// <b>원자적으로 함께 바뀌지 않아</b> 그 사이에 읽은 쪽이 "새 버전 + 옛 상태" 를 볼 수 있다.
/// 한 값 안에 두면 Redis 의 단일 키 연산이 그 원자성을 공짜로 준다.
/// </para>
///
/// <para>
/// <b>⚠ CAS 는 Lua 로 한다.</b> "읽고-비교하고-쓰기" 를 왕복 세 번으로 나누면 그 사이에
/// 남이 끼어든다. <c>WATCH/MULTI/EXEC</c> 는 커넥션에 묶이는데
/// <see cref="ConnectionMultiplexer"/> 는 커넥션을 다중화하므로 맞지 않는다.
/// Lua 스크립트는 <b>서버에서 단일 원자 단위로</b> 실행되므로 이 계약에 정확히 맞는다.
/// </para>
///
/// <para>
/// <b>버전 발급도 서버가 한다.</b> 스크립트 안에서 전역 카운터를 <c>INCR</c> 한다 —
/// 클라이언트가 만들면 여러 노드가 같은 값을 낼 수 있다. 전역 카운터를 쓰는 이유는
/// <b>ABA 방지</b>이며 인메모리 구현과 같은 근거다(<see cref="SessionVersion"/> 계약 2번).
/// </para>
///
/// <para>
/// <b>만료는 Redis 에 맡긴다.</b> <c>SET ... PX</c> 로 TTL 을 걸고 <c>PEXPIRE</c> 로 연장한다.
/// 인메모리 구현이 직접 청소해야 했던 것과 달리 여기서는 서버가 회수하므로 청소 타이머가
/// 없다 — <b>같은 계약을 각 저장소의 네이티브 수단으로 만족시킨다</b>는 것이 축의 요점이다.
/// </para>
///
/// <para>
/// <b>스레드 규약 — 스레드 안전하다.</b> <see cref="IConnectionMultiplexer"/> 자체가
/// 스레드 안전하며 이 타입은 상태를 갖지 않는다(스크립트 해시는 불변이다).
/// </para>
///
/// <para>
/// <b>수명·소유권 규약.</b> 멀티플렉서의 소유권은 <b>호출자에게 있다</b> —
/// 이 타입은 <see cref="IDisposable"/> 이 아니고 멀티플렉서를 닫지 않는다. 멀티플렉서는
/// 애플리케이션당 하나를 공유하는 것이 StackExchange.Redis 의 권장 사용법이므로,
/// 어댑터가 남의 자원을 닫으면 안 된다.
/// </para>
/// </remarks>
public sealed class RedisSessionStore : ISessionStore
{
    /// <summary>값 앞머리에 들어가는 버전의 크기(바이트).</summary>
    private const int VersionPrefixLength = 8;

    /// <summary>
    /// CAS 쓰기. 기대 버전이 맞으면 새 버전을 발급해 값을 교체한다.
    /// </summary>
    /// <remarks>
    /// KEYS[1]=세션 키, KEYS[2]=버전 카운터 / ARGV[1]=기대 버전(8B, None 이면 빈 문자열),
    /// ARGV[2]=상태 바이트, ARGV[3]=TTL(ms, 0 이면 만료 없음).
    /// 반환: 성공이면 새 버전(8B 문자열), 실패면 false.
    /// </remarks>
    private const string WriteScript = """
        local cur = redis.call('GET', KEYS[1])
        local expected = ARGV[1]
        if cur == false then
          if expected ~= '' then return false end
        else
          if expected == '' then return false end
          if string.sub(cur, 1, 8) ~= expected then return false end
        end
        local n = redis.call('INCR', KEYS[2])
        local ver = string.char(
          n % 256,
          math.floor(n / 256) % 256,
          math.floor(n / 65536) % 256,
          math.floor(n / 16777216) % 256,
          math.floor(n / 4294967296) % 256,
          math.floor(n / 1099511627776) % 256,
          math.floor(n / 281474976710656) % 256,
          math.floor(n / 72057594037927936) % 256)
        local value = ver .. ARGV[2]
        if tonumber(ARGV[3]) > 0 then
          redis.call('SET', KEYS[1], value, 'PX', tonumber(ARGV[3]))
        else
          redis.call('SET', KEYS[1], value)
        end
        return ver
        """;

    /// <summary>CAS 삭제. KEYS[1]=세션 키 / ARGV[1]=기대 버전(8B).</summary>
    private const string RemoveScript = """
        local cur = redis.call('GET', KEYS[1])
        if cur == false then return 0 end
        if string.sub(cur, 1, 8) ~= ARGV[1] then return 0 end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    /// <summary>CAS 만료 연장. KEYS[1]=세션 키 / ARGV[1]=기대 버전(8B), ARGV[2]=TTL(ms).</summary>
    /// <remarks>값을 다시 쓰지 않으므로 버전이 바뀌지 않는다(계약).</remarks>
    private const string RenewScript = """
        local cur = redis.call('GET', KEYS[1])
        if cur == false then return 0 end
        if string.sub(cur, 1, 8) ~= ARGV[1] then return 0 end
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))
        return 1
        """;

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisSessionStoreOptions _options;
    private readonly RedisKey _versionCounterKey;

    /// <summary>Redis 세션 저장소를 만든다.</summary>
    /// <param name="multiplexer">
    /// 연결 멀티플렉서. <b>소유권은 호출자에게 있다</b> — 이 타입은 닫지 않는다.
    /// </param>
    /// <param name="options">설정. <see langword="null"/> 이면 기본값.</param>
    /// <exception cref="ArgumentNullException"><paramref name="multiplexer"/> 가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않다.</exception>
    public RedisSessionStore(IConnectionMultiplexer multiplexer, RedisSessionStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);

        _options = options ?? new RedisSessionStoreOptions();
        _options.Validate();

        _multiplexer = multiplexer;
        _versionCounterKey = _options.VersionCounterKey;
    }

    /// <inheritdoc/>
    public async ValueTask<SessionReadResult> TryReadAsync(
        SessionId id,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue value = await Database.StringGetAsync(KeyFor(id)).ConfigureAwait(false);
        if (value.IsNull)
        {
            // ⚠ 찾지 못하면 대상을 건드리지 않는다(계약).
            return SessionReadResult.NotFound;
        }

        ReadOnlyMemory<byte> raw = value;
        if (raw.Length < VersionPrefixLength)
        {
            throw new InvalidOperationException(
                $"세션 값이 버전 접두사보다 짧다({raw.Length}B). 같은 키 공간을 다른 용도가 쓰고 있는지 확인한다.");
        }

        SessionVersion version = new(BinaryPrimitives.ReadUInt64LittleEndian(raw.Span));
        ReadOnlySpan<byte> state = raw.Span[VersionPrefixLength..];
        destination.Write(state);

        return SessionReadResult.Hit(version, state.Length);
    }

    /// <inheritdoc/>
    public async ValueTask<SessionWriteResult> TryWriteAsync(
        SessionId id,
        ReadOnlyMemory<byte> state,
        SessionVersion expectedVersion,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfInvalidTtl(timeToLive);
        cancellationToken.ThrowIfCancellationRequested();

        RedisResult result = await Database.ScriptEvaluateAsync(
            WriteScript,
            [KeyFor(id), _versionCounterKey],
            [
                ExpectedVersionArgument(expectedVersion),
                state,
                (long)(timeToLive?.TotalMilliseconds ?? 0),
            ]).ConfigureAwait(false);

        if (result.IsNull)
        {
            return SessionWriteResult.Conflict;
        }

        byte[]? versionBytes = (byte[]?)result;
        if (versionBytes is not { Length: VersionPrefixLength })
        {
            return SessionWriteResult.Conflict;
        }

        return SessionWriteResult.Ok(new SessionVersion(BinaryPrimitives.ReadUInt64LittleEndian(versionBytes)));
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRemoveAsync(
        SessionId id,
        SessionVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedVersion.IsNone)
        {
            // 없는 항목을 지우라는 요청 — 계약상 false 다(지울 것이 없다).
            return false;
        }

        RedisResult result = await Database.ScriptEvaluateAsync(
            RemoveScript,
            [KeyFor(id)],
            [VersionToBytes(expectedVersion)]).ConfigureAwait(false);

        return (long)result == 1;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRenewAsync(
        SessionId id,
        SessionVersion expectedVersion,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ThrowIfInvalidTtl(timeToLive);
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedVersion.IsNone)
        {
            return false;
        }

        RedisResult result = await Database.ScriptEvaluateAsync(
            RenewScript,
            [KeyFor(id)],
            [VersionToBytes(expectedVersion), (long)timeToLive.TotalMilliseconds]).ConfigureAwait(false);

        return (long)result == 1;
    }

    private IDatabase Database => _multiplexer.GetDatabase();

    private RedisKey KeyFor(SessionId id) =>
        _options.KeyPrefix + id.Value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>기대 버전을 스크립트 인자로 바꾼다. <c>None</c> 은 빈 문자열 = "없어야 한다".</summary>
    private static RedisValue ExpectedVersionArgument(SessionVersion version) =>
        version.IsNone ? RedisValue.EmptyString : VersionToBytes(version);

    private static RedisValue VersionToBytes(SessionVersion version)
    {
        byte[] buffer = new byte[VersionPrefixLength];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, version.Value);
        return buffer;
    }

    private static void ThrowIfInvalidTtl(TimeSpan? timeToLive)
    {
        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive), ttl, "만료 시간은 0 보다 커야 한다(만료 없음은 null).");
        }
    }
}
