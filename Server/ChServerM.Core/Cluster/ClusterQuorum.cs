using System;
using System.Diagnostics;

namespace ChServerM.Cluster;

/// <summary>
/// 뷰가 <b>충분히 큰가</b> — 스플릿 브레인에서 <b>소수파가 스스로 물러나게</b> 하는 게이트.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 뷰-유도 리더(<c>ClusterRouteResolver.IsLeaderFor</c>)는 <b>같은 뷰를
/// 볼 때만</b> 한 명이다. 네트워크가 갈라져 두 무리가 서로를 못 보면 <b>각 무리가 자기
/// 리더를 뽑고</b>, 둘 다 자기가 유일하다고 믿는다. 이 타입은 그 상황에서
/// <b>과반을 못 보는 쪽이 리더 행세를 포기</b>하게 만든다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 이것은 분할을 <i>감지</i>하지 않는다.</b> 이 축은 장애를 판정하지 않으며
/// (ADR-0047), 여기서도 하지 않는다 — 하는 일은 <b>내 뷰의 크기를 세어 문턱과 비교하는
/// 것</b>뿐이다. "저쪽이 살아 있는가" 는 묻지 않고 물을 수도 없다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 그리고 이것으로도 상호 배제는 얻지 못한다.</b> 과반을 보는 무리는 하나뿐이므로
/// <b>동시에 두 리더가 서는 것</b>은 막지만, <b>옛 리더가 자기가 밀려난 것을 아직 모르는
/// 구간</b>은 남는다 — 뷰 갱신은 즉시가 아니다. 진짜 상호 배제가 필요하면
/// <b>펜싱 토큰이 붙은 리스</b>를 쓴다. 프레임워크는 그것을 흉내 내지 않는다.
/// </para>
///
/// <para>
/// <b>⚠ 문턱은 설정이지 발견이 아니다.</b> "클러스터가 원래 몇 대인가" 는 멤버십 제공자가
/// 답해 주지 않는다 — 제공자의 뷰는 <b>지금 보이는 것</b>이고, 그것을 문턱의 근거로 쓰면
/// <b>분할된 뒤에도 "내 뷰 전부가 살아 있으니 과반" 이라는 순환</b>에 빠진다.
/// 그래서 기대 노드 수를 밖에서 받는다.
/// </para>
///
/// <para>
/// <b>기본값이 없다.</b> <see cref="None"/> 은 값이 아니라 <b>명시적 선택</b>이다 —
/// 게이트를 원하지 않는다고 적어야 게이트가 없다. 조용한 기본값이 하필 실패 지점과
/// 겹치는 것을 이 프로젝트는 반복해서 거부해 왔다(CLAUDE.md 8.1).
/// </para>
///
/// <para><b>스레드 규약.</b> 불변 값 타입이다. 어디서든 안전하다.</para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ClusterQuorum : IEquatable<ClusterQuorum>
{
    private ClusterQuorum(int requiredNodes) => RequiredNodes = requiredNodes;

    /// <summary>
    /// 게이트를 두지 않는다 — <b>분할되면 양쪽 모두 리더를 세운다</b>.
    /// </summary>
    /// <remarks>
    /// 리더가 하는 일이 <b>중복 실행돼도 무해</b>할 때(멱등한 정리 작업, 캐시 예열)만
    /// 고른다. 무해한지는 <b>도메인 질문</b>이므로 프레임워크가 대신 답하지 않는다.
    /// </remarks>
    public static ClusterQuorum None => new(0);

    /// <summary>기대 노드 수의 <b>과반</b>을 요구한다.</summary>
    /// <param name="expectedNodeCount">분할이 없을 때의 클러스터 크기. <b>설정에서 온다</b>.</param>
    /// <returns>과반 문턱을 가진 정족수.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedNodeCount"/> 가 1 미만이다.</exception>
    /// <remarks>
    /// <b>과반은 <c>n/2 + 1</c> 이다.</b> 5 대면 3, 6 대면 4 —
    /// <b>짝수는 손해다</b>(6 대에서 3:3 으로 갈리면 <b>양쪽 다 물러난다</b>).
    /// 정족수를 쓸 거라면 홀수로 배치하는 편이 낫다.
    /// </remarks>
    public static ClusterQuorum MajorityOf(int expectedNodeCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedNodeCount, 1);

        return new ClusterQuorum((expectedNodeCount / 2) + 1);
    }

    /// <summary>뷰에 최소 몇 대가 보여야 하는가. <see cref="None"/> 이면 0.</summary>
    public int RequiredNodes { get; }

    /// <summary>게이트가 켜져 있는가.</summary>
    public bool IsEnabled => RequiredNodes > 0;

    /// <summary>이 뷰가 문턱을 넘는가.</summary>
    /// <param name="view">지금 보고 있는 구성원 스냅샷.</param>
    /// <returns>넘으면 <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> 가 <see langword="null"/> 이다.</exception>
    public bool IsSatisfiedBy(ClusterView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.Count >= RequiredNodes;
    }

    /// <inheritdoc/>
    public bool Equals(ClusterQuorum other) => RequiredNodes == other.RequiredNodes;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ClusterQuorum other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => RequiredNodes;

    /// <inheritdoc/>
    public override string ToString() =>
        IsEnabled ? $"Quorum(>={RequiredNodes})" : "Quorum(none)";

    /// <summary>두 정족수가 같은가.</summary>
    /// <param name="left">왼쪽.</param>
    /// <param name="right">오른쪽.</param>
    /// <returns>같으면 <see langword="true"/>.</returns>
    public static bool operator ==(ClusterQuorum left, ClusterQuorum right) => left.Equals(right);

    /// <summary>두 정족수가 다른가.</summary>
    /// <param name="left">왼쪽.</param>
    /// <param name="right">오른쪽.</param>
    /// <returns>다르면 <see langword="true"/>.</returns>
    public static bool operator !=(ClusterQuorum left, ClusterQuorum right) => !left.Equals(right);
}
