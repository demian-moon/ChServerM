using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Cluster;

/// <summary>
/// 클러스터 축 — <b>지금 어떤 노드들이 있고, 나는 그중 누구인가</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 스케일아웃의 모든 결정(어느 노드로 라우팅하는가, 상태를 어디로 옮기는가,
/// 누가 리더인가)은 <b>구성원 목록</b>에서 출발한다. 그 목록을 어디서 얻는지는 배포마다
/// 다르다 — 정적 설정, Consul, etcd, K8s 엔드포인트. 그 차이를 이 축 하나로 가둔다.
/// </para>
///
/// <para>
/// <b>⚠ 이 축은 장애 판정을 하지 않는다.</b> "노드가 살아 있는가" 는 제공자가 이미 답한다 —
/// K8s 는 readiness 프로브로, Consul 은 헬스체크로. 프레임워크가 그 위에 자체 실패 감지를
/// 얹으면 <b>두 판정이 어긋나는</b> 상태가 생기고, 그때 어느 쪽을 믿을지는 아무도 모른다.
/// <see cref="Current"/> 에 있다는 것이 곧 "지금 보낼 수 있다" 는 뜻이다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 한 작업은 <see cref="Current"/> 를 한 번만 읽는다.</b> 라우팅 도중에 다시 읽으면
/// 같은 요청의 두 조각이 다른 노드로 갈 수 있다. <see cref="ClusterView"/> 문서 참조 —
/// 데이터 테이블의 핫 리로드와 <b>같은 규약이고 같은 이유</b>다.
/// </para>
///
/// <para>
/// <b>변화 알림은 밀어내지 않고 기다린다.</b> 이벤트(<c>event</c>)로 밀면 구독 해제를
/// 빠뜨린 쪽이 살아남고(누수), 느린 구독자가 생기면 알림을 어디엔가 쌓아야 한다
/// (무제한 큐 금지, CLAUDE.md 9.6). <see cref="WaitForChangeAsync"/> 는 <b>당기는</b> 형태라
/// 둘 다 없다 — 기다리는 쪽이 자기 속도로 받고, 취소하면 그걸로 끝난다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> <see cref="Self"/> 와 <see cref="Current"/> 는 여러 스레드에서 동시에
/// 읽어도 안전하다. <see cref="WaitForChangeAsync"/> 도 동시 호출을 허용한다 —
/// 여러 소비자(라우터·리밸런서·진단)가 각자 기다릴 수 있어야 한다.
/// </para>
/// </remarks>
public interface IClusterMembership : IAsyncDisposable
{
    /// <summary>이 프로세스가 클러스터에서 누구인가.</summary>
    /// <remarks>
    /// <b>자기 자신은 항상 알려져 있다.</b> 발견에 실패해도 "나는 누구인가" 는 설정에서
    /// 오므로, 이 값이 없는 상태는 조립 오류이지 런타임 상태가 아니다.
    /// </remarks>
    ClusterNode Self { get; }

    /// <summary>지금 유효한 구성원 스냅샷. <b>한 작업에서 한 번만 읽는다</b>.</summary>
    ClusterView Current { get; }

    /// <summary>구성이 바뀔 때까지 기다린다.</summary>
    /// <param name="knownGeneration">
    /// 호출자가 이미 본 세대. <see cref="Current"/> 의 세대가 이보다 크면 <b>즉시</b> 돌아온다 —
    /// 기다리기 시작하는 사이에 일어난 변화를 놓치지 않기 위한 것이다.
    /// </param>
    /// <param name="cancellationToken">기다리기를 그만둘 토큰.</param>
    /// <returns>바뀐 뒤의 스냅샷.</returns>
    /// <exception cref="OperationCanceledException">기다리는 동안 취소됐다.</exception>
    /// <remarks>
    /// <para>
    /// <b>⚠ 세대 인자가 경합을 없앤다.</b> "바뀌면 알려 줘" 만으로는 <b>확인한 직후·기다리기
    /// 직전</b>에 일어난 변화를 영원히 놓친다. 본 세대를 함께 넘기면 그 창이 닫힌다.
    /// </para>
    /// <para>
    /// 구성이 절대 바뀌지 않는 제공자(정적 목록)에서는 <b>취소될 때까지 완료되지 않는다.</b>
    /// 그것이 정확한 답이다 — 바뀌지 않을 것을 "바뀌었다" 고 깨우면 소비자가 헛돈다.
    /// </para>
    /// </remarks>
    ValueTask<ClusterView> WaitForChangeAsync(int knownGeneration, CancellationToken cancellationToken);
}
