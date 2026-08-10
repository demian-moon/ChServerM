using System;
using System.Collections.Generic;
using System.Net;

namespace ChServerM.Cluster;

/// <summary>
/// 클러스터의 노드 하나 — 식별자와 <b>노드 간 통신용</b> 주소.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ 여기 실리는 주소는 내부 주소다.</b> 클라이언트가 접속하는 주소와 노드끼리 말하는
/// 주소는 대개 다르다(로드밸런서 뒤, 별도 NIC, 오버레이 네트워크). 둘을 한 필드로 합치면
/// 어느 쪽이 맞는지 모르는 상태가 되고, 그 혼동은 <b>연결이 되긴 되는데 엉뚱한 경로로
/// 가는</b> 형태로 나타나 진단이 아주 나쁘다.
/// </para>
/// <para>
/// <b>상태(살아 있음/의심/죽음)를 담지 않는다.</b> <see cref="ClusterView.Nodes"/> 에 있다는
/// 것이 곧 "지금 보낼 수 있다" 는 뜻이다 — 장애 판정은 멤버십 제공자의 몫이고
/// (K8s 는 readiness, Consul 은 헬스체크가 이미 한다), 항상 <c>Alive</c> 인 필드를 두는 것은
/// <b>거짓말</b>이다. 판정 근거가 필요해지면 그때 제공자별 진단으로 노출한다.
/// </para>
/// <para><b>스레드 규약.</b> 불변이다.</para>
/// </remarks>
public sealed class ClusterNode
{
    /// <summary>노드를 만든다.</summary>
    /// <param name="id">안정된 식별자.</param>
    /// <param name="endPoint">노드 간 통신용 주소.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> 가 설정되지 않았다.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endPoint"/> 가 <see langword="null"/> 이다.</exception>
    public ClusterNode(NodeId id, EndPoint endPoint)
    {
        if (!id.IsSet)
        {
            throw new ArgumentException("설정되지 않은 노드 식별자다.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(endPoint);

        Id = id;
        EndPoint = endPoint;
    }

    /// <summary>안정된 식별자.</summary>
    public NodeId Id { get; }

    /// <summary>노드 간 통신용 주소.</summary>
    public EndPoint EndPoint { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Id} @ {EndPoint}";
}

/// <summary>
/// 클러스터 구성원의 <b>불변 스냅샷</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ 한 작업은 뷰를 한 번만 읽는다.</b> 라우팅 도중에 뷰를 다시 읽으면 <b>같은 요청의
/// 두 조각이 다른 노드로</b> 갈 수 있다 — 상태를 들고 있는 노드에 키를 보내는 축에서 그것은
/// 곧 상태가 갈라지는 사건이다. <see cref="IClusterMembership.Current"/> 를 한 번 받아
/// 그 작업이 끝날 때까지 쓴다. <c>ReloadableStaticTableSet</c> 과 <b>같은 규약</b>이며,
/// 이유도 같다: 불변 스냅샷은 공짜로 안전하지만 <b>여러 번 읽는 순간</b> 그 이점이 사라진다.
/// </para>
///
/// <para>
/// <b>⭐ 노드는 식별자 사전 순으로 고정된다.</b> 발견 순서(Consul 응답 순서, 설정 파일의
/// 줄 순서)에 의존하면 <b>같은 구성원인데 노드마다 다른 순서</b>를 보게 되고, 순서에 기대는
/// 라우팅(일관 해싱 링 구성, 인덱스 기반 분배)이 노드마다 다른 답을 낸다. 그것은
/// <b>모든 노드가 자기만 옳다고 믿는</b> 형태의 장애다. 순서를 여기서 못 박아 그 경로를 없앤다.
/// </para>
///
/// <para>
/// <b>세대(<see cref="Generation"/>)는 "바뀌었는가" 를 값싸게 묻는 수단이다.</b>
/// 파생 자료구조(해시 링 등)를 들고 있는 쪽은 세대가 바뀌었을 때만 다시 만들면 된다.
/// ⚠ 세대는 <b>이 프로세스가 몇 번째 구성을 보고 있는가</b>이지 클러스터 전체의 합의된
/// 번호가 아니다 — 노드끼리 세대를 비교하지 않는다.
/// </para>
///
/// <para><b>스레드 규약.</b> 불변이며 스레드 안전하다.</para>
/// </remarks>
public sealed class ClusterView
{
    private readonly Dictionary<NodeId, ClusterNode> _byId;

    /// <summary>노드 목록과 세대로 만든다.</summary>
    /// <param name="nodes">구성원. <b>식별자 사전 순으로 정렬된다.</b></param>
    /// <param name="generation">이 스냅샷의 세대. 1 이상.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> 가 <see langword="null"/> 이다.</exception>
    /// <exception cref="ArgumentException">식별자가 중복이거나 노드가 <see langword="null"/> 이다.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generation"/> 이 1 미만이다.</exception>
    public ClusterView(IReadOnlyList<ClusterNode> nodes, int generation)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);

        ClusterNode[] sorted = new ClusterNode[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            sorted[i] = nodes[i] ?? throw new ArgumentException("노드가 null 이다.", nameof(nodes));
        }

        // ⭐ 발견 순서를 지운다. 노드마다 다른 순서를 보면 순서에 기대는 라우팅이 갈라진다.
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Id.Name, b.Id.Name));

        _byId = new Dictionary<NodeId, ClusterNode>(sorted.Length);
        foreach (ClusterNode node in sorted)
        {
            if (!_byId.TryAdd(node.Id, node))
            {
                throw new ArgumentException($"노드 식별자가 중복된다: '{node.Id}'", nameof(nodes));
            }
        }

        Nodes = sorted;
        Generation = generation;
    }

    /// <summary>구성원. <b>식별자 사전 순</b>이며 이 순서는 모든 노드에서 같다.</summary>
    public IReadOnlyList<ClusterNode> Nodes { get; }

    /// <summary>이 스냅샷의 세대. 구성이 바뀔 때마다 는다.</summary>
    public int Generation { get; }

    /// <summary>구성원 수.</summary>
    public int Count => Nodes.Count;

    /// <summary>식별자로 노드를 찾는다.</summary>
    /// <param name="id">노드 식별자.</param>
    /// <param name="node">찾은 노드.</param>
    /// <returns>찾았으면 <see langword="true"/>.</returns>
    public bool TryGetNode(NodeId id, out ClusterNode? node) => _byId.TryGetValue(id, out node);

    /// <summary>그 노드가 지금 구성원인가.</summary>
    /// <param name="id">노드 식별자.</param>
    /// <returns>구성원이면 <see langword="true"/>.</returns>
    public bool Contains(NodeId id) => _byId.ContainsKey(id);
}
