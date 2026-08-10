using System;
using ChServerM.Identity;

namespace ChServerM.Cluster;

/// <summary>
/// 키를 <b>어느 노드가 소유하는가</b>를 정하는 축. <b>하나의 뷰에 묶여 있다.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 프로세스 안의 파티셔닝을 그대로 클러스터에 쓸 수 없다.</b>
/// <see cref="PartitionKey.ToIndex(int)"/> 는 파티션 <b>개수가 고정</b>이라 성립한다.
/// 클러스터는 노드가 들어오고 나가는데, 그때 <c>ToIndex(노드 수)</c> 를 쓰면
/// <b>거의 모든 키가 재배치된다</b> — 노드 하나가 늘었을 뿐인데 상태를 들고 있는
/// 모든 노드가 거의 모든 상태를 옮겨야 하는 사건이 된다.
/// </para>
///
/// <para>
/// <b>⚠ 라우터는 뷰 하나에 묶인다.</b> 뷰가 바뀌면 <b>새 라우터를 만든다</b>.
/// 이것이 "한 작업은 뷰를 한 번만 읽는다"(<see cref="ClusterView"/>) 를 <b>타입으로</b>
/// 표현한 것이다 — 라우터를 한 번 받아 그 작업이 끝날 때까지 쓰면, 요청 도중에 소유자가
/// 바뀌어 같은 요청의 두 조각이 다른 노드로 가는 일이 <b>구조적으로</b> 일어나지 않는다.
/// 규약을 주석에만 적으면 반드시 샌다(CLAUDE.md 9.7).
/// </para>
///
/// <para>
/// <b>⚠ 모든 노드가 같은 답을 내야 한다.</b> 같은 뷰·같은 키면 어느 노드에서 계산하든
/// 결과가 같아야 한다. 그래서 구현은 <b>프로세스를 가로질러 안정된 해시</b>만 쓰고
/// (<c>string.GetHashCode</c> 는 프로세스마다 시드가 달라 <b>쓸 수 없다</b>),
/// 점수가 같을 때의 우선순위까지 결정적이어야 한다.
/// </para>
///
/// <para>
/// <b>후보를 여럿 주는 이유</b>(<see cref="GetOwners"/>) — 복제본을 어디에 둘지,
/// 소유자가 사라졌을 때 다음이 누구인지, 리밸런싱에서 상태를 어디로 옮길지가 전부
/// "이 키의 노드 순위" 하나로 풀린다. 1순위만 주는 API 는 그 질문들에 답하지 못한다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 만든 뒤 불변이며 스레드 안전하다. 여러 파티션 워커가 같은 라우터를
/// 동시에 쓰는 것이 기본 사용 형태다.
/// </para>
/// <para><b>할당.</b> 조회 경로는 힙 할당이 없어야 한다 — 메시지마다 불릴 수 있다.</para>
/// </remarks>
public interface IClusterRouter
{
    /// <summary>이 라우터가 묶인 뷰.</summary>
    ClusterView View { get; }

    /// <summary>키의 소유 노드를 구한다.</summary>
    /// <param name="key">파티션 키.</param>
    /// <param name="owner">소유 노드.</param>
    /// <returns>뷰에 노드가 하나라도 있으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>예외를 쓰지 않는다.</b> 뷰가 비는 것(모든 노드가 사라짐)은 운영 중 실제로
    /// 일어날 수 있는 상태이고, 그것을 핫패스의 예외로 만들면 장애가 예외 폭풍이 된다
    /// (CLAUDE.md 8: 핫패스 제어 흐름에 예외를 쓰지 않는다).
    /// </remarks>
    bool TryGetOwner(PartitionKey key, out ClusterNode? owner);

    /// <summary>키의 소유 후보를 <b>순위대로</b> 채운다.</summary>
    /// <param name="key">파티션 키.</param>
    /// <param name="destination">받을 자리. 길이가 곧 원하는 후보 수다.</param>
    /// <returns>실제로 채운 개수. 노드 수보다 많이 요청하면 노드 수만큼만 채운다.</returns>
    /// <remarks>
    /// <para>
    /// 1순위는 <see cref="TryGetOwner"/> 와 <b>항상 같다</b>. 두 경로가 다른 답을 내면
    /// 복제본이 소유자와 어긋나므로, 구현은 같은 순위 계산을 공유해야 한다.
    /// </para>
    /// <para>
    /// <b>구현은 할당하지 않는다.</b> ⚠ 다만 <see cref="ClusterNode"/> 가 참조 타입이라
    /// <c>stackalloc</c> 으로는 이 자리를 만들 수 없다 — 호출자는 <b>재사용하는 배열</b>
    /// (필드나 풀 대여물)을 넘긴다. 매번 새 배열을 만들면 무할당은 호출자 쪽에서 깨진다.
    /// </para>
    /// </remarks>
    int GetOwners(PartitionKey key, Span<ClusterNode?> destination);
}
