using System;

namespace ChServerM.Cluster;

/// <summary>
/// 클러스터 노드의 <b>안정된</b> 식별자.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 주소는 식별자가 아니다.</b> 노드를 IP·포트로 가리키면 재시작·재배치로
/// 주소가 바뀔 때 <b>같은 노드가 다른 노드로 보인다</b>. 상태를 들고 있는 노드에 키를
/// 라우팅하는 축(Phase 15)에서 그것은 곧 <b>모든 키가 재배치되는 사건</b>이다.
/// 그래서 식별자는 주소와 분리하고, 배포가 안정적으로 부여한다
/// (K8s 의 StatefulSet 서수, 정적 목록의 이름 등).
/// </para>
/// <para>
/// <b>문자열인 이유.</b> 로그·설정·운영 대화에 그대로 나온다. 숫자 ID 는 사람이 읽을 때
/// 한 단계를 더 거치게 하고, 그 단계에서 오해가 생긴다. 비교는 <b>서수(ordinal)</b> 다 —
/// 컬처 의존 비교가 클러스터 구성원 판정에 끼어들면 배포 지역에 따라 결과가 달라진다.
/// </para>
/// <para>
/// <b>⚠ 라우팅 해시를 여기서 만들지 않는다.</b> 노드 이름을 어떤 해시로 링에 올릴지는
/// 라우팅 전략의 몫이고(일관 해싱·랑데뷰 등 선택이 갈린다), Core 는 서드파티 의존이
/// 없어 좋은 해시 구현을 들일 수도 없다. 여기서는 <b>이름과 동등성</b>만 준다.
/// </para>
/// <para><b>스레드 규약.</b> 불변 값 타입이다.</para>
/// </remarks>
public readonly struct NodeId : IEquatable<NodeId>
{
    private readonly string? _name;

    /// <summary>이름으로 만든다.</summary>
    /// <param name="name">비어 있지 않은 노드 이름.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 이 비었다.</exception>
    public NodeId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>노드 이름. 설정되지 않았으면 빈 문자열.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>실제 식별자가 설정됐는가.</summary>
    /// <remarks>
    /// <c>default</c> 를 유효한 노드로 취급하지 않는다 — 초기화되지 않은 필드가
    /// <b>우연히 같은 노드</b>가 되는 것을 막는다.
    /// </remarks>
    public bool IsSet => !string.IsNullOrEmpty(_name);

    /// <inheritdoc/>
    public bool Equals(NodeId other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc/>
    public override string ToString() => IsSet ? Name : "(unset)";

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>같으면 <see langword="true"/>.</returns>
    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>다르면 <see langword="true"/>.</returns>
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
}
