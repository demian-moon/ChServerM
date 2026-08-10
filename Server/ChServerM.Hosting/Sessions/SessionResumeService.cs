using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;

namespace ChServerM.Hosting.Sessions;

/// <summary>
/// 세션 복구·재접속 — 끊긴 클라이언트가 <b>상태를 잃지 않고 돌아오는</b> 경로.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 모바일·불안정 네트워크에서 커넥션은 수시로 끊긴다. 끊길 때마다 상태를
/// 잃으면 사용자는 처음부터 다시 시작해야 한다. <c>realtime-stateful</c> 프로필의 필수
/// 항목이며(ADR-0004), 이 타입이 그 메커니즘이다.
/// </para>
///
/// <para>
/// <b>⚠ 저장 값의 봉투 형식 — 프레임워크가 앞머리를 소유한다.</b>
/// </para>
/// <code>
///   [1B 버전][32B 재개 토큰 해시][앱 상태 바이트...]
/// </code>
/// <para>
/// 세션 저장소 계약은 <b>불투명한 바이트</b>이므로(ADR-0033) 재개 토큰을 둘 별도 자리가
/// 없다. 세션마다 키를 두 개 쓰면 <b>토큰 회전과 상태 갱신이 원자적으로 함께 일어나지 않아</b>
/// 그 사이에 재접속한 쪽이 어긋난 조합을 본다. 한 값에 담으면 저장소의 CAS 가 그 원자성을
/// 그대로 준다.
/// </para>
/// <para>
/// 앱은 자기 상태만 주고받는다 — 봉투는 이 타입이 붙이고 뗀다.
/// </para>
///
/// <para>
/// <b>⚠ 좀비 커넥션 차단은 CAS 에 얹는다 — 새 개념이 없다.</b> 재개는 토큰 회전이라는
/// <b>쓰기</b>를 유발하므로 세션 버전이 올라가고, <b>옛 커넥션이 들고 있던 버전은 자동으로
/// 무효</b>가 된다. 옛 커넥션이 상태를 쓰려 하면 <c>Conflict</c> 를 받아 자신이 밀려났음을
/// 알게 된다. ADR-0033 이 CAS 를 v1 에 넣은 이유가 바로 이 경로다.
/// </para>
///
/// <para>
/// <b>보안 규약</b>
/// </para>
/// <list type="number">
///   <item><b>토큰은 저장하지 않는다</b> — 해시만 둔다. 저장소가 유출돼도 재접속할 수 없다</item>
///   <item><b>사용할 때마다 회전한다</b> — 탈취된 토큰은 1회용이고, 진짜 주인과 탈취자 중
///   늦게 온 쪽이 실패하므로 탈취가 드러난다</item>
///   <item><b>실패 사유를 구분해 주지 않는다</b> — "세션 없음" 과 "토큰 불일치" 를 나누면
///   공격자가 실재하는 SessionId 를 열거할 수 있다</item>
///   <item><b>비교는 상수 시간</b>(<see cref="SessionResumeToken.MatchesHash"/>)</item>
/// </list>
///
/// <para>
/// <b>스레드 규약.</b> 내부 저장소가 스레드 안전한 만큼 안전하다. 이 타입은 상태를 갖지 않는다.
/// </para>
/// </remarks>
public sealed class SessionResumeService
{
    /// <summary>봉투 형식 버전. 형식이 바뀌면 올린다 — 옛 값을 만나면 거부할 수 있어야 한다.</summary>
    private const byte EnvelopeVersion = 1;

    private const int VersionOffset = 0;
    private const int TokenHashOffset = 1;

    /// <summary>봉투 앞머리 길이 — 형식 버전 1바이트 + 토큰 해시.</summary>
    public const int EnvelopeHeaderLength = 1 + SessionResumeToken.HashLength;

    private readonly ISessionStore _store;
    private readonly TimeSpan? _timeToLive;

    /// <summary>재개 서비스를 만든다.</summary>
    /// <param name="store">세션 저장소. <b>소유권은 호출자에게 있다.</b></param>
    /// <param name="timeToLive">
    /// 세션 만료 시간. <see langword="null"/> 이면 만료하지 않는다.
    /// <b>끊긴 클라이언트가 돌아올 수 있는 시간의 상한</b>이 곧 이 값이다.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> 가 <see langword="null"/> 이다.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeToLive"/> 가 0 이하다.</exception>
    public SessionResumeService(ISessionStore store, TimeSpan? timeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive), ttl, "만료 시간은 0 보다 커야 한다(만료 없음은 null).");
        }

        _store = store;
        _timeToLive = timeToLive;
    }

    /// <summary>새 세션을 만들고 최초 재개 토큰을 발급한다.</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="initialState">앱의 초기 상태.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>
    /// 성공하면 버전과 토큰. <b>이미 그 식별자의 세션이 있으면 실패한다</b>(<see langword="null"/>) —
    /// 남의 세션을 덮어쓰지 않기 위해서다.
    /// </returns>
    public async ValueTask<SessionBinding?> TryCreateAsync(
        SessionId id,
        ReadOnlyMemory<byte> initialState,
        CancellationToken cancellationToken = default)
    {
        SessionResumeToken token = SessionResumeToken.Create();

        byte[] envelope = new byte[EnvelopeHeaderLength + initialState.Length];
        WriteHeader(envelope, token);
        initialState.Span.CopyTo(envelope.AsSpan(EnvelopeHeaderLength));

        SessionWriteResult write = await _store
            .TryWriteAsync(id, envelope, SessionVersion.None, _timeToLive, cancellationToken)
            .ConfigureAwait(false);

        return write.Succeeded ? new SessionBinding(write.Version, token) : null;
    }

    /// <summary>재개 토큰을 제시받아 세션을 이어받는다.</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="presentedToken">클라이언트가 제시한 토큰.</param>
    /// <param name="stateDestination">앱 상태를 받을 대상. 실패하면 건드리지 않는다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>성공 시 새 버전·회전된 토큰·상태 길이. 실패는 사유를 구분하지 않는다.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stateDestination"/> 가 <see langword="null"/> 이다.</exception>
    public async ValueTask<SessionResumeResult> TryResumeAsync(
        SessionId id,
        SessionResumeToken presentedToken,
        IBufferWriter<byte> stateDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateDestination);

        // 봉투를 통째로 읽는다 — 대상에 바로 쓰면 실패 시 헤더가 남아 계약을 어긴다.
        ArrayBufferWriter<byte> raw = new();
        SessionReadResult read = await _store.TryReadAsync(id, raw, cancellationToken).ConfigureAwait(false);

        if (!read.Found || read.Length < EnvelopeHeaderLength)
        {
            return SessionResumeResult.Failed;
        }

        ReadOnlySpan<byte> envelope = raw.WrittenSpan;
        if (envelope[VersionOffset] != EnvelopeVersion)
        {
            return SessionResumeResult.Failed;
        }

        // ⚠ 상수 시간 비교. 여기서 일찍 빠져나가면 접두사가 타이밍으로 샌다.
        if (!presentedToken.MatchesHash(envelope.Slice(TokenHashOffset, SessionResumeToken.HashLength)))
        {
            return SessionResumeResult.Failed;
        }

        // ⚠ 회전 — 이 쓰기가 버전을 올려 옛 커넥션을 밀어낸다(좀비 차단은 CAS 에 얹는다).
        //
        // ⚠ 스팬은 await 를 넘길 수 없다(CS4007). 아래 배열이 그 역할을 대신하며,
        // 성공 후 대상에 쓸 때도 이 배열의 슬라이스를 쓴다.
        int stateLength = envelope.Length - EnvelopeHeaderLength;
        SessionResumeToken rotated = SessionResumeToken.Create();
        byte[] rewritten = new byte[EnvelopeHeaderLength + stateLength];
        WriteHeader(rewritten, rotated);
        envelope[EnvelopeHeaderLength..].CopyTo(rewritten.AsSpan(EnvelopeHeaderLength));

        SessionWriteResult write = await _store
            .TryWriteAsync(id, rewritten, read.Version, _timeToLive, cancellationToken)
            .ConfigureAwait(false);

        if (!write.Succeeded)
        {
            // 그 사이 남이 먼저 재개했다 — 늦게 온 쪽이 진다. 탈취자든 진짜 주인이든 같다.
            return SessionResumeResult.Failed;
        }

        stateDestination.Write(rewritten.AsSpan(EnvelopeHeaderLength));
        return SessionResumeResult.Ok(write.Version, rotated, stateLength);
    }

    /// <summary>앱 상태만 갱신한다. 재개 토큰은 그대로 둔다.</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="state">새 앱 상태.</param>
    /// <param name="expectedVersion">마지막으로 읽은 버전.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>CAS 결과. <b>충돌은 "내가 밀려났다" 는 신호다</b> — 재개가 일어난 것일 수 있다.</returns>
    /// <remarks>
    /// 토큰을 보존하려면 기존 봉투를 읽어야 한다. 읽기-쓰기 사이의 경쟁은 CAS 가 잡는다.
    /// </remarks>
    public async ValueTask<SessionWriteResult> TryWriteStateAsync(
        SessionId id,
        ReadOnlyMemory<byte> state,
        SessionVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte> raw = new();
        SessionReadResult read = await _store.TryReadAsync(id, raw, cancellationToken).ConfigureAwait(false);

        if (!read.Found || read.Length < EnvelopeHeaderLength || read.Version != expectedVersion)
        {
            // 없거나, 형식이 아니거나, 이미 밀려났다.
            return SessionWriteResult.Conflict;
        }

        byte[] envelope = new byte[EnvelopeHeaderLength + state.Length];
        raw.WrittenSpan[..EnvelopeHeaderLength].CopyTo(envelope);
        state.Span.CopyTo(envelope.AsSpan(EnvelopeHeaderLength));

        return await _store
            .TryWriteAsync(id, envelope, expectedVersion, _timeToLive, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>앱 상태만 읽는다(봉투를 뗀다).</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="stateDestination">앱 상태를 받을 대상.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>찾음 여부·버전·앱 상태 길이. <b>봉투 길이는 포함하지 않는다.</b></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stateDestination"/> 가 <see langword="null"/> 이다.</exception>
    public async ValueTask<SessionReadResult> TryReadStateAsync(
        SessionId id,
        IBufferWriter<byte> stateDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateDestination);

        ArrayBufferWriter<byte> raw = new();
        SessionReadResult read = await _store.TryReadAsync(id, raw, cancellationToken).ConfigureAwait(false);

        if (!read.Found || read.Length < EnvelopeHeaderLength || raw.WrittenSpan[VersionOffset] != EnvelopeVersion)
        {
            return SessionReadResult.NotFound;
        }

        ReadOnlySpan<byte> appState = raw.WrittenSpan[EnvelopeHeaderLength..];
        stateDestination.Write(appState);
        return SessionReadResult.Hit(read.Version, appState.Length);
    }

    private static void WriteHeader(Span<byte> envelope, SessionResumeToken token)
    {
        envelope[VersionOffset] = EnvelopeVersion;
        token.Hash(envelope.Slice(TokenHashOffset, SessionResumeToken.HashLength));
    }
}
