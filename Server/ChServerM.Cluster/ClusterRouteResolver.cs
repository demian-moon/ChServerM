using System;
using System.Threading;
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
}
