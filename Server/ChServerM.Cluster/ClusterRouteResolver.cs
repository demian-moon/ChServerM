using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;

namespace ChServerM.Cluster;

/// <summary>
/// 멤버십과 라우터를 묶어 <b>키 하나의 목적지를 결정</b>한다.
/// 뷰가 바뀌면 라우터를 <b>자동으로 다시 만든다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 뷰와 라우터는 짝으로만 유효하다.</b> 라우터는 뷰 하나에 묶이는데
/// (ADR-0048), 뷰는 바뀐다. 그러면 소비자마다 "세대를 보고 라우터를 다시 만드는" 코드를
/// 쓰게 되고, <b>그 코드가 반복될수록 어긋난 짝을 쓰는 곳이 생긴다</b> — 예를 들어
/// 뷰는 새것인데 라우터는 옛것이면 <b>사라진 노드로 보내게</b> 된다. 그 재생성을 여기 한 곳에 둔다.
/// </para>
///
/// <para>
/// <b>⭐ 그리고 "그게 나인가" 를 판정한다.</b> 라우터는 소유자까지만 답한다. 소유자가
/// 자기 자신인 경우를 원격과 같은 경로로 흘려보내면 <b>자기에게 네트워크 왕복</b>을 하고,
/// 더 나쁘게는 자기에게 연결하는 커넥션이 생겨 수용 한도와 통계를 오염시킨다.
/// 이 판정은 호출자마다 반복되는 종류라 언젠가 한 곳에서 빠지는데, 여기서 한 번 하면 그 경로가 없다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 여러 결정을 내릴 때는 <see cref="Router"/> 를 한 번 받아 쓴다.</b>
/// <see cref="Resolve(PartitionKey)"/> 를 두 번 부르면 그 사이에 뷰가 바뀔 수 있고, 그러면
/// <b>같은 요청의 두 조각이 다른 구성을 보고</b> 결정된다. 한 작업 = 한 뷰 규약
/// (<see cref="ClusterView"/>)의 실제 적용 지점이 여기다:
/// </para>
/// <code>
///   IClusterRouter router = resolver.Router;          // 한 번 받는다
///   foreach (var key in keys)
///   {
///       ClusterRoute route = resolver.Resolve(router, key);   // 같은 뷰로 전부 결정
///   }
/// </code>
///
/// <para>
/// <b>캐시는 잠그지 않는다.</b> 라우터는 불변이므로 참조 하나를 바꾸는 것으로 교체가 끝난다
/// (핫 리로드·클러스터 뷰와 같은 구조). 경합해서 두 스레드가 같은 뷰의 라우터를 각각
/// 만들어도 <b>결과가 같으므로 무해</b>하고, 진 쪽은 자기가 만든 것을 그대로 쓴다 —
/// 그 뷰에 대해서는 옳기 때문이다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 모든 멤버가 스레드 안전하다. 여러 파티션 워커가 동시에 결정을
/// 내리는 것이 기본 사용 형태다.
/// </para>
/// <para><b>할당.</b> 뷰가 그대로면 결정 경로에 할당이 없다. 뷰가 바뀐 첫 호출만 라우터를 만든다.</para>
/// </remarks>
public sealed class ClusterRouteResolver
{
    private readonly IClusterMembership _membership;
    private readonly Func<ClusterView, IClusterRouter> _factory;
    private IClusterRouter _cached;

    /// <summary>멤버십과 라우터 생성기로 만든다.</summary>
    /// <param name="membership">구성원 원천.</param>
    /// <param name="routerFactory">
    /// 뷰에서 라우터를 만드는 함수. 라우팅 전략을 고르는 지점이다
    /// (기본은 <see cref="RendezvousRouter"/>).
    /// </param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    public ClusterRouteResolver(IClusterMembership membership, Func<ClusterView, IClusterRouter> routerFactory)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(routerFactory);

        _membership = membership;
        _factory = routerFactory;
        _cached = routerFactory(membership.Current)
            ?? throw new ArgumentException("라우터 생성기가 null 을 돌려줬다.", nameof(routerFactory));
    }

    /// <summary>기본 전략(<see cref="RendezvousRouter"/>)으로 만든다.</summary>
    /// <param name="membership">구성원 원천.</param>
    /// <exception cref="ArgumentNullException"><paramref name="membership"/> 가 <see langword="null"/> 이다.</exception>
    public ClusterRouteResolver(IClusterMembership membership)
        : this(membership, static view => new RendezvousRouter(view))
    {
    }

    /// <summary>이 프로세스가 클러스터에서 누구인가.</summary>
    public ClusterNode Self => _membership.Self;

    /// <summary>
    /// 지금 뷰에 묶인 라우터. <b>여러 결정을 내릴 때는 이것을 한 번 받아 쓴다</b>(타입 문서 참조).
    /// </summary>
    public IClusterRouter Router
    {
        get
        {
            ClusterView view = _membership.Current;
            IClusterRouter cached = Volatile.Read(ref _cached);

            if (ReferenceEquals(cached.View, view))
            {
                return cached;
            }

            IClusterRouter fresh = _factory(view)
                ?? throw new InvalidOperationException("라우터 생성기가 null 을 돌려줬다.");

            // ⚠ 한 번만 시도하고 실패해도 다시 돌지 않는다. 진 쪽이 캐시에 더 오래된
            //   라우터를 남기더라도, 다음 호출이 Current 와 다시 대조해 스스로 고친다
            //   (세대는 단조 증가한다). 루프를 돌면 경합이 심할 때 그 자체가 비용이다.
            _ = Interlocked.CompareExchange(ref _cached, fresh, cached);

            // 우리가 읽은 뷰에 대해서는 이 라우터가 옳다. 캐시 경쟁 결과와 무관하게 쓴다.
            return fresh;
        }
    }

    /// <summary>키의 목적지를 결정한다. <b>결정 하나짜리 경로용</b>이다.</summary>
    /// <param name="key">파티션 키.</param>
    /// <returns>로컬·원격·보낼 곳 없음.</returns>
    /// <remarks>
    /// <b>⚠ 여러 번 부르지 않는다.</b> 호출 사이에 뷰가 바뀌면 같은 요청의 조각들이 다른
    /// 구성을 보고 결정된다 — 그때는 <see cref="Router"/> 를 한 번 받아
    /// <see cref="Resolve(IClusterRouter, PartitionKey)"/> 를 쓴다.
    /// </remarks>
    public ClusterRoute Resolve(PartitionKey key) => Resolve(Router, key);

    /// <summary>이미 받은 라우터로 키의 목적지를 결정한다.</summary>
    /// <param name="router">한 뷰에 묶인 라우터.</param>
    /// <param name="key">파티션 키.</param>
    /// <returns>로컬·원격·보낼 곳 없음.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="router"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>
    /// 한 작업의 모든 결정을 <b>같은 뷰</b>로 내리기 위한 것이다. 라우터가 이 리졸버가 준
    /// 것이 아니어도 된다 — 결정은 그 라우터의 뷰를 기준으로 일관되기만 하면 된다.
    /// </remarks>
    public ClusterRoute Resolve(IClusterRouter router, PartitionKey key)
    {
        ArgumentNullException.ThrowIfNull(router);

        if (!router.TryGetOwner(key, out ClusterNode? owner))
        {
            return ClusterRoute.Unavailable;
        }

        // ⭐ 여기가 자기 자신에게 네트워크를 타지 않게 하는 유일한 지점이다.
        return owner!.Id == _membership.Self.Id
            ? ClusterRoute.ToLocal(owner)
            : ClusterRoute.ToRemote(owner);
    }

    /// <summary>
    /// 이 노드가 그 역할의 <b>리더인가</b> — 뷰에서 바로 유도한다. <b>합의도 메시지도 없다.</b>
    /// </summary>
    /// <param name="router">한 뷰에 묶인 라우터.</param>
    /// <param name="role">역할을 가리키는 고정 키. 역할마다 <b>다른 키</b>를 주면 리더가 흩어진다.</param>
    /// <param name="quorum">
    /// 정족수 게이트. <b>기본값이 없다</b> — 게이트를 원하지 않으면
    /// <see cref="ClusterQuorum.None"/> 을 <b>명시</b>한다.
    /// </param>
    /// <returns>리더면 <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="router"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>
    /// <para>
    /// <b>⭐ 선출하지 않는다. 계산한다.</b> 역할 키의 랑데뷰 소유자가 곧 리더이므로,
    /// <b>같은 뷰를 보는 모든 노드가 메시지 한 통 없이 같은 답</b>을 낸다. 노드가 빠지면
    /// 그 역할은 자동으로 다음 순위로 넘어간다 — 이것도 계산일 뿐이다. 리밸런싱에서
    /// "옮길 상태가 없다" 를 확인한 것과 같은 종류의 결론이다(ADR-0052).
    /// </para>
    ///
    /// <para>
    /// <b>⚠⚠ 이것은 상호 배제가 아니다.</b> 두 가지 이유로 <b>두 노드가 동시에
    /// <see langword="true"/> 를 받을 수 있다</b>:
    /// </para>
    /// <list type="number">
    ///   <item><b>뷰가 갈리면</b> 각 무리가 자기 리더를 뽑는다(스플릿 브레인).
    ///     <paramref name="quorum"/> 이 <b>소수파를 물러나게</b> 하지만, 그것은 게이트를
    ///     켰을 때의 이야기다.</item>
    ///   <item><b>뷰 갱신은 즉시가 아니다.</b> 새 리더가 선 뒤에도 옛 리더는 자기가 밀려난
    ///     것을 아직 모를 수 있다 — <b>정족수를 켜도 이 구간은 남는다</b>.</item>
    /// </list>
    /// <para>
    /// 그러므로 리더가 하는 일은 <b>중복 실행돼도 안전</b>해야 한다. 안전하지 않다면
    /// <b>펜싱 토큰이 붙은 리스</b>가 필요하고, 그것은 이 축이 주지 않는다 —
    /// 흉내 내면 "리더니까 나 혼자겠지" 라는 <b>틀린 믿음</b>만 생긴다.
    /// <b>⚠ <see cref="ClusterView.Generation"/> 을 펜싱 토큰으로 쓰지 않는다</b> —
    /// 노드마다 제공자가 매기는 값이라 갈라진 두 무리가 <b>같은 세대 번호</b>를 들 수 있다.
    /// </para>
    ///
    /// <code>
    ///   IClusterRouter router = resolver.Router;             // 한 번 받는다
    ///   ClusterQuorum quorum = ClusterQuorum.MajorityOf(5);  // 설정에서 온 기대 크기
    ///
    ///   if (resolver.IsLeaderFor(router, CompactionRole, quorum))
    ///   {
    ///       await CompactAsync();   // 중복 실행돼도 안전한 일이어야 한다
    ///   }
    /// </code>
    /// </remarks>
    public bool IsLeaderFor(IClusterRouter router, PartitionKey role, ClusterQuorum quorum)
    {
        ArgumentNullException.ThrowIfNull(router);

        // ⚠ 정족수를 소유자 계산보다 **먼저** 본다. 순서가 반대면 소수파도 자기 무리의
        //   소유자를 계산해 잠시 리더로 행동한 뒤 물러나게 된다.
        if (!quorum.IsSatisfiedBy(router.View))
        {
            return false;
        }

        return router.TryGetOwner(role, out ClusterNode? owner) && owner!.Id == _membership.Self.Id;
    }

    /// <summary>이 노드가 그 역할의 리더인가. <b>판정 하나짜리 경로용</b>이다.</summary>
    /// <param name="role">역할을 가리키는 고정 키.</param>
    /// <param name="quorum">정족수 게이트. 원하지 않으면 <see cref="ClusterQuorum.None"/>.</param>
    /// <returns>리더면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>⚠ 리더 판정과 그에 딸린 라우팅 결정을 함께 할 때는 쓰지 않는다.</b> 그 사이에
    /// 뷰가 바뀌면 <b>리더는 옛 뷰로, 라우팅은 새 뷰로</b> 정해진다 — 그때는
    /// <see cref="Router"/> 를 한 번 받아 <see cref="IsLeaderFor(IClusterRouter, PartitionKey, ClusterQuorum)"/>
    /// 를 쓴다.
    /// </remarks>
    public bool IsLeaderFor(PartitionKey role, ClusterQuorum quorum) => IsLeaderFor(Router, role, quorum);

    /// <summary>
    /// 구성이 바뀔 때마다 <b>그 뷰에 묶인 라우터</b>를 하나씩 준다 —
    /// <b>노드가 늘거나 줄었을 때 소유권을 재검토하는 신호</b>다.
    /// </summary>
    /// <param name="cancellationToken">감시를 그만둘 토큰.</param>
    /// <returns>첫 항목은 <b>지금</b> 뷰의 라우터이고, 이후 구성이 바뀔 때마다 하나씩 나온다.</returns>
    /// <remarks>
    /// <para>
    /// <b>존재 이유 — 뷰가 바뀐 것을 앱이 알 방법이 없었다.</b> 라우팅은 "이 키가 누구 것인가"
    /// 를 물으면 답하지만, <b>"내가 무엇을 잃었는가"</b> 는 아무도 알려 주지 않았다. 노드-로컬
    /// 상태(캐시·룸·타이머)를 든 앱은 그 신호가 없으면 <b>남의 키를 계속 붙들고 처리한다</b>.
    /// </para>
    ///
    /// <para>
    /// <b>⚠⚠ "잃은 키 목록" 을 주지 않는다 — 줄 수 없다.</b> 랑데뷰 해싱은 키 → 노드 함수이고
    /// <b>역방향이 없으며</b>, 애초에 프레임워크는 앱이 어떤 키를 들고 있는지 모른다. 그래서
    /// 재검토는 <b>앱이 자기 것을 순회하며</b> 한다. 이것을 감추고 그럴듯한 목록을 만들어
    /// 주는 편이 API 는 예뻐 보이지만, 그 목록은 반드시 앱의 실제 보유분과 어긋난다.
    /// <code>
    ///   await foreach (IClusterRouter router in resolver.WatchAsync(token))
    ///   {
    ///       foreach (PartitionKey key in app.LocallyHeldKeys)   // 앱만 아는 집합
    ///       {
    ///           if (!resolver.Resolve(router, key).IsLocal)
    ///           {
    ///               app.Release(key);   // 이동·폐기 방법은 앱의 몫
    ///           }
    ///       }
    ///   }
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <b>⭐ 뷰가 아니라 라우터를 준다.</b> 이 축의 알려진 함정이 <b>뷰와 라우터가 어긋나
    /// 사라진 노드로 보내는 것</b>(타입 문서 첫 절)인데, 뷰를 주면 받는 쪽이 라우터를 다시
    /// 구해야 하고 그 사이에 또 바뀔 수 있다. <b>짝지어 줘서 어긋날 자리를 없앤다.</b>
    /// </para>
    ///
    /// <para>
    /// <b>⚠ 밀린 세대는 합친다. 큐가 없다.</b> 뷰는 <b>이벤트가 아니라 상태</b>이므로 새 뷰가
    /// 옛 뷰를 <b>대체</b>한다 — 소비가 느린 동안 세 번 바뀌었으면 <b>가장 새것 하나</b>만
    /// 나온다. 옛 뷰로 재검토하는 것은 낭비일 뿐 아니라 <b>이미 틀린 답</b>이다.
    /// 그 결과 쌓을 것이 없으므로 무제한 큐 금지(CLAUDE.md 9.6)를 <b>구조적으로</b> 만족한다.
    /// </para>
    ///
    /// <para>
    /// <b>첫 항목이 "변화" 가 아닌 것은 의도된 것이다.</b> 기동 직후의 배치와 이후의 재검토를
    /// <b>같은 코드</b>로 쓰게 한다 — 루프 밖에 초기화를 따로 두면 그 두 벌이 갈라진다.
    /// 재검토는 멱등하므로 중복 통지가 나와도 무해하다.
    /// </para>
    ///
    /// <para>
    /// <b>⚠ 이동한 상태의 안전성은 여기서 오지 않는다.</b> 옛 소유자가 전환을 늦게 알아채고
    /// 쓰기를 시도하는 경우는 <b>저장소의 단일 키 CAS 가 이미 막는다</b>(CONSISTENCY 5절) —
    /// 다른 노드에 있어도 같은 저장소를 보므로 버전 비교가 그대로 성립한다. 이 신호는
    /// <b>일을 줄이기 위한 것</b>이지 정확성의 최후 방어선이 아니다.
    /// </para>
    ///
    /// <para>
    /// 구성이 절대 바뀌지 않는 제공자(정적 목록)에서는 첫 항목 뒤로 <b>취소될 때까지 아무것도
    /// 나오지 않는다.</b> 그것이 정확한 답이다.
    /// </para>
    ///
    /// <para><b>스레드 규약.</b> 여러 소비자가 각자 돌려도 된다 — 서로를 막지 않는다.</para>
    /// </remarks>
    public async IAsyncEnumerable<IClusterRouter> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IClusterRouter router = Router;
        yield return router;

        int seen = router.View.Generation;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ⚠ 반환값을 **의도적으로 버린다**. 깨우는 신호에 실려 온 뷰는 낡았을 수 있다 —
            //   알림을 큐로 나르는 제공자라면 그 사이에 Current 가 더 앞서간다. 그것을 그대로
            //   쓰면 앱이 **이미 틀린 뷰로 소유권을 재검토**하고, 이 리졸버의 단일 결정 경로
            //   (Resolve(key))가 보는 뷰와도 어긋난다. Current 를 다시 읽어 가장 새것을 준다.
            //   (고의 회귀로 확인: 이 줄을 되돌리면 StaleWakeupSignal 테스트가 깨진다.)
            _ = await _membership.WaitForChangeAsync(seen, cancellationToken).ConfigureAwait(false);

            router = Router;
            seen = router.View.Generation;
            yield return router;
        }
    }
}
