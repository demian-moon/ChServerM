using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ChServerM.Identity;

namespace ChServerM.Cluster;

/// <summary>
/// 랑데뷰(HRW) 해싱 라우터 — <b>노드마다 점수를 매기고 가장 높은 노드가 소유한다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 클러스터 라우팅에 필요한 성질은 두 가지다: <b>모든 노드가 같은 답</b>을
/// 내고, 구성이 바뀔 때 <b>움직이는 키가 최소</b>여야 한다. 노드 수로 나누는 방식
/// (<c>ToIndex(노드 수)</c>)은 두 번째를 완전히 어긴다 — 노드 하나가 늘면
/// <b>키의 절반이 재배치</b>되고(측정값, ADR-0048), 살아남은 노드끼리도 키를 주고받는다.
/// </para>
///
/// <para>
/// <b>⭐ 왜 일관 해싱(링)이 아니라 랑데뷰인가.</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>튜닝 손잡이가 없다.</b> 링은 가상 노드 수를 사람이 정하는데, 적게 잡으면 분포가
///     크게 치우친다 — 한 노드가 몇 배의 부하를 받는데 <b>아무 오류도 나지 않는</b>
///     조용한 실패다. 랑데뷰는 설정 없이 균등하다
///   </item>
///   <item>
///     <b>후보 k개가 공짜로 맞다.</b> 링에서 상위 k개를 뽑으려면 "같은 물리 노드를 건너뛰기"
///     를 직접 해야 하고 그것이 고전적인 버그 자리다. 랑데뷰는 <b>점수 순위 그대로</b>가 답이다
///   </item>
///   <item>
///     <b>상태가 없다.</b> 링 배열을 만들 필요가 없어 뷰가 바뀔 때 재구성 비용이 노드 수에
///     비례한다(가상 노드 수만큼 곱해지지 않는다)
///   </item>
/// </list>
/// <para>
/// <b>대가는 조회가 O(노드 수)</b> 라는 것이다. 링은 O(log(노드×가상노드)) 다. 노드 수가
/// 수백을 넘으면 링이 유리해지고, 그때는 이 축에 두 번째 구현을 넣으면 된다 —
/// <b>지금 필요하지도 않은 복잡도를 미리 사지 않는다</b>(측정 없는 최적화 금지).
/// </para>
///
/// <para>
/// <b>⚠ 정체성(노드 번호)을 해싱한다. 이름이 아니다.</b> 이름으로 라우팅하면 이름을 바꾸는
/// 순간 모든 키가 재배치된다. 그리고 <c>string.GetHashCode</c> 는 <b>프로세스마다 시드가
/// 다르므로</b> 애초에 쓸 수 없다 — 노드마다 다른 답이 나오고, 그것은 <b>모든 노드가 서로
/// 다른 소유자를 믿는</b> 상태다. 번호는 <c>splitmix64</c> 마무리 함수로 흩뜨리며,
/// 그 결과는 프로세스·플랫폼과 무관하게 같다.
/// </para>
///
/// <para>
/// <b>⚠ 동점도 결정적으로 깬다.</b> 점수가 같으면 <b>번호가 작은 노드</b>가 이긴다.
/// 뷰가 이미 번호 순으로 정렬돼 있고(<see cref="ClusterView"/>) 갱신에 엄격한 부등호를
/// 쓰므로 그 규칙이 저절로 성립한다. 동점은 사실상 일어나지 않지만, <b>사실상</b> 은
/// 분산 시스템에서 충분한 근거가 아니다.
/// </para>
///
/// <para><b>스레드 규약.</b> 만든 뒤 불변이며 스레드 안전하다.</para>
/// <para><b>할당.</b> 조회 경로에 힙 할당이 없다. 후보 채우기도 호출자의 스팬에 쓴다.</para>
/// </remarks>
public sealed class RendezvousRouter : IClusterRouter
{
    private readonly ulong[] _nodeHashes;
    private readonly ClusterNode[] _nodes;

    /// <summary>뷰에 묶인 라우터를 만든다.</summary>
    /// <param name="view">구성원 스냅샷.</param>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>
    /// 노드 해시를 <b>여기서 한 번</b> 끝낸다 — 조회마다 다시 계산하면 그것이 곧
    /// 핫패스의 비용이 된다.
    /// </remarks>
    public RendezvousRouter(ClusterView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        View = view;
        _nodes = new ClusterNode[view.Count];
        _nodeHashes = new ulong[view.Count];

        for (int i = 0; i < view.Count; i++)
        {
            ClusterNode node = view.Nodes[i];
            _nodes[i] = node;
            _nodeHashes[i] = Mix(node.Id.Value);
        }
    }

    /// <inheritdoc/>
    public ClusterView View { get; }

    /// <inheritdoc/>
    public bool TryGetOwner(PartitionKey key, [NotNullWhen(true)] out ClusterNode? owner)
    {
        if (_nodes.Length == 0)
        {
            owner = null;
            return false;
        }

        ulong keyHash = key.Value;
        int best = 0;
        ulong bestScore = Score(_nodeHashes[0], keyHash);

        // 뷰가 번호 순이고 갱신이 엄격한 부등호라, 동점이면 번호가 작은 노드가 남는다.
        for (int i = 1; i < _nodes.Length; i++)
        {
            ulong score = Score(_nodeHashes[i], keyHash);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        owner = _nodes[best];
        return true;
    }

    /// <inheritdoc/>
    public int GetOwners(PartitionKey key, Span<ClusterNode?> destination)
    {
        int wanted = Math.Min(destination.Length, _nodes.Length);
        if (wanted == 0)
        {
            return 0;
        }

        ulong keyHash = key.Value;

        // 상위 k개만 필요하므로 전체 정렬을 하지 않는다. k 는 대개 2~3 이라
        // 삽입 정렬이 힙보다 빠르고, 무엇보다 할당이 없다.
        Span<ulong> scores = wanted <= 8 ? stackalloc ulong[8] : new ulong[wanted];
        scores = scores[..wanted];

        int filled = 0;

        for (int i = 0; i < _nodes.Length; i++)
        {
            ulong score = Score(_nodeHashes[i], keyHash);

            // 이미 다 찼고 최하위보다 낮으면 볼 것도 없다.
            if (filled == wanted && score <= scores[filled - 1])
            {
                continue;
            }

            int position = filled < wanted ? filled : wanted - 1;

            // ⚠ 엄격한 부등호다. 동점이면 먼저 들어온(= 번호가 작은) 노드가 위에 남는다.
            while (position > 0 && score > scores[position - 1])
            {
                scores[position] = scores[position - 1];
                destination[position] = destination[position - 1];
                position--;
            }

            scores[position] = score;
            destination[position] = _nodes[i];

            if (filled < wanted)
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>노드와 키의 결합 점수.</summary>
    /// <remarks>
    /// <para>
    /// <c>splitmix64</c> 의 마무리 함수다. <b>전단사(bijection)</b> 이므로 입력이 서로 다르면
    /// 출력도 다르고, 비트가 고르게 섞인다 — 고정된 키에 대해 노드별 점수가 서로 독립인 것처럼
    /// 보여야 분포가 균등해진다.
    /// </para>
    /// <para>
    /// 곱셈 둘과 시프트 셋이라 조회 하나가 노드당 몇 나노초다. 노드 해시는 뷰를 묶을 때
    /// 이미 계산해 뒀으므로 여기서는 정수 하나를 섞을 뿐이다.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Score(ulong nodeHash, ulong keyHash) => Mix(nodeHash ^ keyHash);

    /// <summary><c>splitmix64</c> 의 마무리 함수. 전단사이며 비트를 고르게 섞는다.</summary>
    /// <remarks>
    /// 노드 번호처럼 <b>작고 순차적인 값</b>을 흩뜨리라고 만들어진 함수다 —
    /// 0·1·2 가 서로 아주 다른 값이 되어야 노드별 점수가 독립인 것처럼 보인다.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong value)
    {
        ulong z = unchecked(value + 0x9E37_79B9_7F4A_7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL);
        return z ^ (z >> 31);
    }
}
