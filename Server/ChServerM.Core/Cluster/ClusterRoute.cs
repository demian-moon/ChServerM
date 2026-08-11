using System;
using System.Diagnostics;

namespace ChServerM.Cluster;

/// <summary>라우팅 결정의 갈래.</summary>
/// <remarks>
/// <b>⭐ <see cref="Local"/> 이 따로 있는 것이 요점이다.</b> "소유자가 나" 인 경우를 원격과
/// 같은 경로로 흘려보내면 자기 자신에게 네트워크 왕복을 하게 되고, 더 나쁘게는
/// <b>자기에게 연결하는 커넥션</b>이 생겨 커넥션 수용 한도와 통계를 오염시킨다.
/// 갈래를 타입으로 드러내면 호출자가 그 분기를 <b>빠뜨릴 수 없다</b>.
/// </remarks>
public enum ClusterRouteKind
{
    /// <summary>쓰이지 않는 값. 0 을 유효 결정으로 두지 않아 초기화되지 않은 구조체를 오독하지 않는다.</summary>
    Unspecified = 0,

    /// <summary>이 노드가 소유한다. <b>네트워크를 타지 않는다.</b></summary>
    Local = 1,

    /// <summary>다른 노드가 소유한다. 그 노드로 보낸다.</summary>
    Remote = 2,

    /// <summary>보낼 곳이 없다. 뷰가 비었다.</summary>
    /// <remarks>
    /// <b>거부가 붕괴보다 낫다</b>(CLAUDE.md 9.6). 모든 노드가 사라진 상태에서 요청을
    /// 어딘가에 쌓아 두면 그것이 곧 OOM 이다 — 호출자가 즉시 거절할 수 있어야 한다.
    /// </remarks>
    Unavailable = 3,
}

/// <summary>
/// 키 하나에 대한 라우팅 결정 — <b>어디로 보내는가, 아니면 내가 처리하는가</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 라우터는 "누가 소유자인가" 까지만 답한다. 거기서 <b>"그게 나인가"</b> 를
/// 판정하는 것은 호출자마다 반복되는 일이고, 반복되는 판정은 언젠가 한 곳에서 빠진다 —
/// 그 한 곳이 <b>자기 자신에게 네트워크로 보내는</b> 코드가 된다.
/// </para>
/// <para><b>스레드 규약.</b> 불변 값 타입이다.</para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ClusterRoute : IEquatable<ClusterRoute>
{
    private ClusterRoute(ClusterRouteKind kind, ClusterNode? target)
    {
        Kind = kind;
        Target = target;
    }

    /// <summary>결정의 갈래.</summary>
    public ClusterRouteKind Kind { get; }

    /// <summary>
    /// 소유 노드. <see cref="ClusterRouteKind.Unavailable"/> 이면 <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ClusterRouteKind.Local"/> 일 때도 채워진다 — 로그와 진단이 "누가 처리했는가"
    /// 를 같은 방식으로 적을 수 있어야 한다.
    /// </remarks>
    public ClusterNode? Target { get; }

    /// <summary>이 노드가 처리하는가.</summary>
    public bool IsLocal => Kind == ClusterRouteKind.Local;

    /// <summary>보낼 곳이 있는가(로컬 포함).</summary>
    public bool HasTarget => Kind is ClusterRouteKind.Local or ClusterRouteKind.Remote;

    /// <summary>이 노드가 소유하는 결정을 만든다.</summary>
    /// <param name="self">이 노드.</param>
    /// <returns>로컬 결정.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="self"/> 가 <see langword="null"/> 이다.</exception>
    public static ClusterRoute ToLocal(ClusterNode self)
    {
        ArgumentNullException.ThrowIfNull(self);
        return new ClusterRoute(ClusterRouteKind.Local, self);
    }

    /// <summary>다른 노드가 소유하는 결정을 만든다.</summary>
    /// <param name="target">소유 노드.</param>
    /// <returns>원격 결정.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> 가 <see langword="null"/> 이다.</exception>
    public static ClusterRoute ToRemote(ClusterNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ClusterRoute(ClusterRouteKind.Remote, target);
    }

    /// <summary>보낼 곳이 없는 결정.</summary>
    public static ClusterRoute Unavailable => new(ClusterRouteKind.Unavailable, null);

    /// <inheritdoc/>
    public bool Equals(ClusterRoute other) =>
        Kind == other.Kind && ReferenceEquals(Target, other.Target);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ClusterRoute other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Target);

    /// <inheritdoc/>
    public override string ToString() =>
        Kind == ClusterRouteKind.Unavailable ? "route:unavailable" : $"route:{Kind}→{Target}";

    /// <summary>두 결정이 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>같으면 <see langword="true"/>.</returns>
    public static bool operator ==(ClusterRoute left, ClusterRoute right) => left.Equals(right);

    /// <summary>두 결정이 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>다르면 <see langword="true"/>.</returns>
    public static bool operator !=(ClusterRoute left, ClusterRoute right) => !left.Equals(right);
}
