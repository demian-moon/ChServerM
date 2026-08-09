using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;

namespace ChServerM.Sessions;

/// <summary>
/// 세션 상태 저장소 축의 Core 계약 — 세션 상태를 <b>불투명한 바이트</b>로 보관한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 세션 상태를 어디에 두는가는 교체 가능한 축이다(CLAUDE.md 3절):
/// 인메모리 · Redis · Garnet · 로컬 KV. 이 인터페이스가 그 축의 경계이며, 구현을 바꿔도
/// <b>핸들러 코드는 그대로여야</b> 한다 — 그것이 ADR-0004 가 요구하는 조립 가능성의 합격
/// 기준이다.
/// </para>
///
/// <para>
/// <b>⚠ 왜 바이트인가 — 타입이 아니라.</b> 제네릭 <c>ISessionStore&lt;TState&gt;</c> 로 객체를
/// 주고받으면 인메모리는 <b>살아 있는 참조</b>를 돌려주고 Redis 는 <b>역직렬화된 사본</b>을
/// 돌려준다. 그러면 반환 객체를 고쳤을 때 인메모리에서는 저장소가 바뀌고 Redis 에서는
/// 무시된다 — <b>같은 핸들러 코드가 저장소마다 다르게 동작</b>하므로 축 교체가 성립하지
/// 않는다. 바이트 계약은 <b>양쪽 모두 값 의미</b>를 갖게 해 그 함정을 없앤다.
/// 대가는 인메모리에서도 직렬화 비용을 낸다는 것이며, 그것을 알고 고른 값이다.
/// 직렬화기 선택은 호출자의 몫이다 — Core 는 직렬화 축을 알지 않는다.
/// </para>
///
/// <para>
/// <b>⚠ 왜 버전(CAS)이 v1 에 있는가.</b> 세션은 읽고-고치고-쓰는 자원이라 낙관적 동시성이
/// 없으면 재접속·다중 노드에서 <b>조용한 덮어쓰기</b>가 난다. 그리고 이 계약은
/// <c>PublicAPI.Shipped.txt</c> 로 굳으므로 나중에 버전을 끼워 넣는 것은 파괴적 변경이다.
/// 지금 넣는 것이 유일하게 싼 시점이다. → <see cref="SessionVersion"/>
/// </para>
///
/// <para>
/// <b>⚠ 왜 만료(TTL)가 v1 에 있는가.</b> 세션은 반드시 만료된다 — 없으면 끊긴 클라이언트의
/// 상태가 영원히 쌓여 저장소가 OOM 벡터가 된다. 인메모리·Redis 모두 만료를 네이티브로
/// 지원하므로 나중에 앱마다 재발명하게 두는 것보다 계약에 두는 편이 정직하다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 구현은 <b>스레드 안전해야 한다.</b> 세션은 임의의 실행 컨텍스트에서
/// 조회된다 — 파티션 워커, 수락 루프, 관리 엔드포인트가 모두 같은 저장소를 본다.
/// 파티션 소유권에 기대어 동기화를 생략하지 않는다(9.7: 스레드 안전성은 이름과 타입으로
/// 드러낸다 — 여기서는 이 문장이 계약이다).
/// </para>
///
/// <para>
/// <b>수명·소유권 규약.</b>
/// </para>
/// <list type="bullet">
///   <item><b>읽기</b>: 대상 <see cref="IBufferWriter{T}"/> 는 <b>호출자 소유</b>다.
///   저장소는 쓰기만 하고 반납하지 않는다. 찾지 못하면 대상을 <b>건드리지 않는다</b></item>
///   <item><b>쓰기</b>: 넘긴 <see cref="ReadOnlyMemory{T}"/> 는 <b>호출이 끝나면 재사용해도
///   된다</b> — 저장소가 필요한 만큼 복사한다. 대여 버퍼를 그대로 넘기고 즉시 반납해도 안전하다</item>
/// </list>
///
/// <para>
/// <b>구현이 지켜야 할 것.</b>
/// </para>
/// <list type="number">
///   <item>버전은 쓰기마다 바뀌고 같은 키에 재사용되지 않는다(ABA 방지, <see cref="SessionVersion"/>)</item>
///   <item>만료된 항목은 <b>없는 것과 같다</b> — 읽기는 실패하고, 그 키의 첫 쓰기는
///   <see cref="SessionVersion.None"/> 을 기대 버전으로 받아 성공한다</item>
///   <item>버전 충돌은 예외가 아니라 <see cref="SessionWriteResult.Conflict"/> 다</item>
/// </list>
/// </remarks>
public interface ISessionStore
{
    /// <summary>세션 상태를 읽어 대상에 쓴다.</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="destination">상태 바이트를 받을 대상. <b>호출자 소유</b>이며, 찾지 못하면 건드리지 않는다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>찾음 여부·버전·쓴 길이.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> 가 <see langword="null"/> 이다.</exception>
    ValueTask<SessionReadResult> TryReadAsync(
        SessionId id,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken = default);

    /// <summary>기대 버전이 맞을 때만 세션 상태를 쓴다(낙관적 동시성).</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="state">저장할 상태. 호출이 끝나면 재사용해도 된다 — 저장소가 복사한다.</param>
    /// <param name="expectedVersion">
    /// 마지막으로 읽은 버전. 새로 만들 때는 <see cref="SessionVersion.None"/> 을 넘긴다
    /// (= "아직 없을 때만 만들어라").
    /// </param>
    /// <param name="timeToLive">
    /// 이 쓰기 이후의 만료 시간. <see langword="null"/> 이면 만료하지 않는다.
    /// 쓰기가 성공하면 만료 시각이 <b>다시 설정</b>된다.
    /// </param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>성공 여부와 새 버전. 기대 버전이 다르면 <see cref="SessionWriteResult.Conflict"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeToLive"/> 가 0 이하다.</exception>
    ValueTask<SessionWriteResult> TryWriteAsync(
        SessionId id,
        ReadOnlyMemory<byte> state,
        SessionVersion expectedVersion,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default);

    /// <summary>기대 버전이 맞을 때만 세션을 삭제한다.</summary>
    /// <param name="id">세션 식별자.</param>
    /// <param name="expectedVersion">마지막으로 읽은 버전.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>삭제했으면 <see langword="true"/>. 없거나 버전이 다르면 <see langword="false"/>.</returns>
    ValueTask<bool> TryRemoveAsync(
        SessionId id,
        SessionVersion expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>상태를 다시 쓰지 않고 만료 시각만 연장한다.</summary>
    /// <remarks>
    /// <b>존재 이유.</b> 하트비트로 세션을 살려 두는 것은 흔한 경로인데, 이 메서드가 없으면
    /// <b>만료를 미루려고 상태 전체를 다시 직렬화해 전송</b>해야 한다. 상태가 클수록,
    /// 하트비트가 잦을수록 낭비가 커진다.
    /// </remarks>
    /// <param name="id">세션 식별자.</param>
    /// <param name="expectedVersion">마지막으로 읽은 버전.</param>
    /// <param name="timeToLive">새 만료 시간.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>
    /// 연장했으면 <see langword="true"/>. 없거나 버전이 다르면 <see langword="false"/>.
    /// <b>버전은 바뀌지 않는다</b> — 상태가 바뀌지 않았으므로 다른 주체의 CAS 를 깨지 않는다.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeToLive"/> 가 0 이하다.</exception>
    ValueTask<bool> TryRenewAsync(
        SessionId id,
        SessionVersion expectedVersion,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);
}
