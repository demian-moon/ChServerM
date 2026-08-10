using System;
using System.Net;
using ChServerM.Identity;

namespace ChServerM.Cluster.Consul;

/// <summary>Consul 멤버십 어댑터의 설정.</summary>
/// <remarks>
/// <para>
/// <b>검증은 조립 시점에 전부 한다</b>(정적 목록과 같은 원칙). 잘못된 설정으로 기동하면
/// 증상이 "일부 키가 아무 데도 가지 않는다" 로 나타나고, 그때는 원인이 설정에서 아주 멀다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 전용. 만들고 나면 멤버십이 값을 복사해 간다.</para>
/// </remarks>
public sealed class ConsulClusterMembershipOptions
{
    /// <summary>Consul 에이전트 주소. 기본 <c>http://127.0.0.1:8500</c>.</summary>
    /// <remarks>
    /// <b>보통 로컬 에이전트를 가리킨다.</b> 서버에 직접 붙이면 블로킹 쿼리가 서버로 몰려
    /// Consul 자체가 병목이 된다 — 에이전트가 그 부하를 흡수하라고 있는 것이다.
    /// </remarks>
    public Uri Address { get; set; } = new("http://127.0.0.1:8500");

    /// <summary>구성원을 찾을 Consul 서비스 이름.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>이 프로세스의 노드 번호.</summary>
    /// <remarks>
    /// <b>자기 자신은 설정에서 온다</b>(ADR-0047) — 발견에 실패해도 "나는 누구인가" 는
    /// 알려져 있어야 하고, Consul 이 아직 우리를 보여 주지 않는 기동 순간에도 그렇다.
    /// </remarks>
    public NodeId SelfId { get; set; }

    /// <summary>이 프로세스의 이름.</summary>
    public string SelfName { get; set; } = string.Empty;

    /// <summary>이 프로세스의 <b>노드 간 통신용</b> 주소.</summary>
    /// <remarks><b>⚠ 클라이언트 접속 주소가 아니다</b>(<see cref="ClusterNode"/> 문서).</remarks>
    public EndPoint SelfEndPoint { get; set; } = null!;

    /// <summary>노드 번호가 담긴 서비스 메타 키. 기본 <c>chserverm-node-id</c>.</summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ 번호를 Consul 서비스 ID 에서 파싱하지 않는다.</b> 그것은 배포마다 형식이 다르고
    /// (K8s 파드 이름·EC2 인스턴스 ID·손으로 적은 문자열), 파싱은 조용히 틀린 번호를 만든다.
    /// <b>번호는 명시적 메타 필드</b>여야 하고, 없으면 그 노드는 <b>구성원에서 제외</b>된다 —
    /// 짐작한 번호로 라우팅하는 것보다 낫다.
    /// </para>
    /// </remarks>
    public string NodeIdMetaKey { get; set; } = "chserverm-node-id";

    /// <summary>
    /// 노드 간 통신 포트가 담긴 서비스 메타 키. 비우면 서비스 포트를 그대로 쓴다.
    /// </summary>
    /// <remarks>
    /// <b>클라이언트 포트와 노드 간 포트가 다른 배포가 대부분이다.</b> 같은 서비스에
    /// 등록하면서 내부 포트를 따로 알리는 자리다. 비워 두면 둘이 같다는 뜻이 된다 —
    /// 그것이 틀리면 "연결은 되는데 엉뚱한 경로" 가 되므로 기본값을 두되 문서로 경고한다.
    /// </remarks>
    public string? PeerPortMetaKey { get; set; } = "chserverm-peer-port";

    /// <summary>블로킹 쿼리의 최대 대기 시간. 기본 5분.</summary>
    /// <remarks>
    /// <b>이것은 폴링 주기가 아니다.</b> Consul 은 변화가 있으면 <b>즉시</b> 응답하고,
    /// 없으면 이 시간까지 붙들고 있다가 같은 인덱스로 돌려준다. 짧게 잡으면 헛도는
    /// 왕복이 늘 뿐 반응이 빨라지지 않는다.
    /// </remarks>
    public TimeSpan WaitTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>블로킹 쿼리가 실패했을 때 다시 시도하기까지의 간격. 기본 1초.</summary>
    /// <remarks>
    /// <b>⚠ 이 지연이 없으면 Consul 이 죽었을 때 재시도 루프가 CPU 를 태운다.</b>
    /// 에이전트 재시작 구간에서 초당 수천 번 연결을 시도하는 것이 실제 증상이다.
    /// </remarks>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>설정이 앞뒤가 맞는지 확인한다.</summary>
    /// <exception cref="InvalidOperationException">구성이 성립하지 않는다.</exception>
    public void Validate()
    {
        if (Address is null)
        {
            throw new InvalidOperationException($"{nameof(Address)} 가 null 이다.");
        }

        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException($"{nameof(ServiceName)} 이 비어 있다.");
        }

        if (string.IsNullOrWhiteSpace(SelfName))
        {
            throw new InvalidOperationException($"{nameof(SelfName)} 이 비어 있다.");
        }

        if (SelfEndPoint is null)
        {
            throw new InvalidOperationException($"{nameof(SelfEndPoint)} 이 null 이다.");
        }

        if (string.IsNullOrWhiteSpace(NodeIdMetaKey))
        {
            throw new InvalidOperationException($"{nameof(NodeIdMetaKey)} 가 비어 있다.");
        }

        if (WaitTime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(WaitTime)} 은 0 보다 커야 한다.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(RetryDelay)} 는 음수일 수 없다.");
        }
    }
}
