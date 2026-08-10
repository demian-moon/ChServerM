using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Cluster;

/// <summary>
/// 정적 목록 멤버십의 설정 — <b>구성원과 나 자신</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>검증은 조립 시점에 전부 한다.</b> 구성원 목록이 잘못된 채로 기동하면 그 결과는
/// "일부 키가 아무 데도 가지 않는" 형태로 나타나고, 그때는 원인이 설정에서 아주 멀어져 있다
/// (Phase 2 옵션 검증과 같은 원칙).
/// </para>
/// <para><b>스레드 규약.</b> 조립 전용. 만들고 나면 멤버십이 값을 복사해 간다.</para>
/// </remarks>
public sealed class StaticClusterMembershipOptions
{
    /// <summary>이 프로세스의 노드 이름. <see cref="Nodes"/> 안에 있어야 한다.</summary>
    public string SelfName { get; set; } = string.Empty;

    /// <summary>구성원. 이름과 <b>노드 간 통신용</b> 주소의 쌍.</summary>
    /// <remarks>
    /// <b>⚠ 클라이언트 접속 주소가 아니다.</b> 둘이 다른 배포가 대부분이며, 섞으면
    /// 연결은 되는데 엉뚱한 경로로 가는 형태로 나타나 진단이 아주 나쁘다
    /// (<see cref="ClusterNode"/> 문서 참조).
    /// </remarks>
    public IList<(string Name, EndPoint EndPoint)> Nodes { get; } = [];

    /// <summary>설정이 앞뒤가 맞는지 확인한다.</summary>
    /// <exception cref="InvalidOperationException">구성이 성립하지 않는다.</exception>
    /// <remarks>
    /// <b>자기 자신이 목록에 있어야 한다.</b> 없으면 이 노드는 <b>자기에게는 아무것도
    /// 라우팅되지 않는다고 믿으면서</b> 다른 노드들은 자기에게 보내는 상태가 된다 —
    /// 구성 실수 중 가장 진단이 어려운 축에 든다.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SelfName))
        {
            throw new InvalidOperationException(
                $"{nameof(SelfName)} 이 비어 있다. 이 프로세스가 클러스터에서 누구인지 반드시 정해야 한다.");
        }

        if (Nodes.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(Nodes)} 가 비어 있다. 최소한 자기 자신은 있어야 한다.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        bool selfFound = false;

        foreach ((string name, EndPoint endPoint) in Nodes)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("노드 이름이 비어 있다.");
            }

            if (endPoint is null)
            {
                throw new InvalidOperationException($"노드 '{name}' 의 주소가 null 이다.");
            }

            if (!names.Add(name))
            {
                throw new InvalidOperationException($"노드 이름이 중복된다: '{name}'");
            }

            selfFound |= string.Equals(name, SelfName, StringComparison.Ordinal);
        }

        if (!selfFound)
        {
            throw new InvalidOperationException(
                $"{nameof(SelfName)} '{SelfName}' 이 {nameof(Nodes)} 에 없다. "
                + "자기를 목록에서 빠뜨리면 이 노드만 자기에게 라우팅되지 않는다고 믿게 된다.");
        }
    }
}

/// <summary>
/// 설정에 적힌 목록을 그대로 구성원으로 쓰는 <see cref="IClusterMembership"/> 참조 구현.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 축을 가설에서 꺼낸다.</b> 두 번째 구현(Consul·etcd·K8s)이 나오기 전까지
/// 추상화는 가설이다(CLAUDE.md 3절). 이 구현이 그 가설을 <b>실행 가능한 형태</b>로 고정하고,
/// 라우팅·리밸런싱 같은 위층이 실제 멤버십 없이도 만들어질 수 있게 한다.
/// </para>
///
/// <para>
/// <b>쓸모없는 구현이 아니다.</b> 노드 집합이 배포로만 바뀌는 운영(고정 크기 샤드, 정해진
/// 수의 게임 월드 서버)에서는 이것이 <b>정답</b>이다 — 서비스 디스커버리를 들이는 순간
/// 그 자체가 장애 지점이 되고, 바뀌지 않을 목록을 위해 그 비용을 낼 이유가 없다.
/// </para>
///
/// <para>
/// <b>⚠ <see cref="WaitForChangeAsync"/> 는 취소될 때까지 완료되지 않는다.</b>
/// 구성이 바뀌지 않으므로 그것이 정확한 답이다. 바뀌지 않을 것을 주기적으로 "바뀌었다" 고
/// 깨우면 소비자가 헛돌고, 그 헛도는 비용은 노드 수만큼 곱해진다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 만든 뒤 불변이며 모든 멤버가 스레드 안전하다. 뷰가 바뀌지 않으므로
/// <see cref="Current"/> 는 동기화조차 필요 없다 — 불변이 동시성 문제를 미리 없앤 또 한 사례다.
/// </para>
/// </remarks>
public sealed class StaticClusterMembership : IClusterMembership
{
    private readonly ClusterView _view;

    /// <summary>설정에서 만든다.</summary>
    /// <param name="options">구성원과 자기 이름.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">설정이 성립하지 않는다.</exception>
    public StaticClusterMembership(StaticClusterMembershipOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        List<ClusterNode> nodes = new(options.Nodes.Count);
        ClusterNode? self = null;

        foreach ((string name, EndPoint endPoint) in options.Nodes)
        {
            ClusterNode node = new(new NodeId(name), endPoint);
            nodes.Add(node);

            if (string.Equals(name, options.SelfName, StringComparison.Ordinal))
            {
                self = node;
            }
        }

        // 정적 목록은 세대가 늘지 않는다. 1 로 고정하는 것이 "한 번도 바뀌지 않았다" 의 표현이다.
        _view = new ClusterView(nodes, generation: 1);

        // Validate 가 이미 자기 존재를 보장한다. 여기 도달했는데 null 이면 그것은 이 타입의 버그다.
        Self = self!;
    }

    /// <inheritdoc/>
    public ClusterNode Self { get; }

    /// <inheritdoc/>
    public ClusterView Current => _view;

    /// <inheritdoc/>
    public ValueTask<ClusterView> WaitForChangeAsync(int knownGeneration, CancellationToken cancellationToken)
    {
        // 호출자가 아직 이 구성을 못 봤다면 즉시 준다 — "확인 직후·대기 직전" 창을 닫는 규약.
        if (knownGeneration < _view.Generation)
        {
            return new ValueTask<ClusterView>(_view);
        }

        // 바뀔 일이 없다. 취소될 때까지 완료하지 않는 것이 정확한 답이다.
        // ⚠ Task.Delay 대신 TCS + 등록을 쓴다 — 타이머를 잡지 않고, 취소되면 등록이
        //   함께 풀려 대기하는 소비자 수만큼 자원이 늘지 않는다.
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ClusterView>(cancellationToken);
        }

        return new ValueTask<ClusterView>(NeverAsync(cancellationToken));
    }

    /// <inheritdoc/>
    /// <remarks>보유한 자원이 없다. 축 계약을 맞추기 위해 존재한다.</remarks>
    public ValueTask DisposeAsync() => default;

    private static async Task<ClusterView> NeverAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<ClusterView> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // ⚠ 등록을 반드시 푼다. 풀지 않으면 취소 토큰이 살아 있는 동안 콜백이 쌓여,
        //   기다렸다 그만두기를 반복하는 소비자가 곧 누수가 된다(9.2 와 같은 종류의 실수).
        await using (cancellationToken.Register(
            static state => ((TaskCompletionSource<ClusterView>)state!).TrySetCanceled(), completion))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }
}
