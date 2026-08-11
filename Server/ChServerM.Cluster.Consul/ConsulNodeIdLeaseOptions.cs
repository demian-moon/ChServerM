using System;
using ChServerM.Identity;

namespace ChServerM.Cluster.Consul;

/// <summary>노드 번호 임차의 설정.</summary>
/// <remarks>
/// <b>번호를 <i>배정</i>하지 않는다. 이미 정해진 번호가 지금 나만의 것인지 확인한다.</b>
/// 번호를 어디서 얻는지는 배포가 정한다(K8s StatefulSet 서수·Nomad 할당 인덱스·설정 파일).
/// 여기가 하는 일은 그 번호가 <b>겹쳤을 때 조용히 지나가지 않게</b> 만드는 것이다.
/// </remarks>
public sealed class ConsulNodeIdLeaseOptions
{
    /// <summary>Consul 에이전트 주소.</summary>
    public Uri Address { get; set; } = new("http://127.0.0.1:8500");

    /// <summary>임차할 노드 번호. <b>배포가 준 값이다</b>.</summary>
    public NodeId NodeId { get; set; }

    /// <summary>임차자를 알아볼 이름. 충돌했을 때 <b>누가 들고 있는지</b>를 여기서 읽는다.</summary>
    /// <remarks>
    /// <b>진단이 이 필드의 존재 이유다.</b> "번호 3 이 이미 쓰이고 있다" 만으로는 어느
    /// 프로세스인지 찾을 수 없고, 그 상태에서 운영자가 할 수 있는 일이 없다.
    /// </remarks>
    public string HolderName { get; set; } = string.Empty;

    /// <summary>번호 키가 놓일 KV 접두사. 기본 <c>chserverm/nodes</c>.</summary>
    /// <remarks>
    /// <b>클러스터마다 달라야 한다.</b> 같은 Consul 에 두 클러스터가 있는데 접두사가
    /// 같으면 서로의 번호를 빼앗는다 — 증상은 "가끔 기동이 실패한다" 로 나타난다.
    /// </remarks>
    public string KeyPrefix { get; set; } = "chserverm/nodes";

    /// <summary>Consul 세션의 TTL. 기본 30초.</summary>
    /// <remarks>
    /// <b>이 값이 곧 "죽은 노드의 번호가 풀리기까지의 시간" 이다.</b> 짧게 잡으면 잠깐의
    /// 멈춤(GC·네트워크 흔들림)에도 임차를 잃고, 길게 잡으면 재기동한 노드가 자기 번호를
    /// 되찾기까지 기다린다. <b>Consul 은 1분 미만을 허용하지 않을 수 있으므로</b>
    /// 실제 만료는 이 값의 최대 2배까지 늦어질 수 있다(Consul 문서).
    /// </remarks>
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>세션 갱신 주기. 비우면 <see cref="SessionTtl"/> 의 절반.</summary>
    /// <remarks>
    /// <b>⚠ TTL 에 가깝게 잡지 않는다.</b> 갱신 한 번이 늦으면 곧바로 만료되고, 그러면
    /// <b>살아 있는 노드가 번호를 잃는다</b>. 절반이 관례적인 안전 여유다.
    /// </remarks>
    public TimeSpan? RenewInterval { get; set; }

    /// <summary>세션이 무효화된 뒤 그 잠금을 다시 잡을 수 없는 시간. 기본 15초.</summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ 이 지연은 안전장치다. 0 으로 두지 않는다.</b> 우리 세션이 만료됐는데
    /// <b>우리는 아직 돌고 있는</b> 구간이 존재하고(만료 판정은 Consul 이 한다),
    /// 그 순간 다른 노드가 즉시 우리 번호를 가져가면 <b>둘이 같은 번호로 동작</b>한다.
    /// 이 지연이 그 창을 좁힌다 — <b>없애지는 못한다</b>.
    /// </para>
    /// <para>
    /// <b>명시적 반납(<see cref="ConsulNodeIdLease.DisposeAsync"/>)에는 적용되지 않는다</b> —
    /// 정상 종료한 노드의 번호는 즉시 풀린다.
    /// </para>
    /// </remarks>
    public TimeSpan LockDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 성립하지 않는다.</exception>
    public void Validate()
    {
        if (Address is null)
        {
            throw new InvalidOperationException(
                $"{nameof(Address)} 가 null 이다. Consul HTTP API 주소를 준다 — 로컬 에이전트 기본값은 http://localhost:8500 이다.");
        }

        if (string.IsNullOrWhiteSpace(HolderName))
        {
            throw new InvalidOperationException(
                $"{nameof(HolderName)} 이 비어 있다. 충돌 시 누가 들고 있는지 알 수 없게 된다.");
        }

        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new InvalidOperationException(
                $"{nameof(KeyPrefix)} 가 비어 있다. Consul KV 에서 번호 슬롯이 이 접두사 아래 놓인다 — "
                + "같은 클러스터의 모든 노드가 같은 접두사를 써야 같은 번호 공간을 본다.");
        }

        if (SessionTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(SessionTtl)}({SessionTtl}) 이 0 이하다. Consul 세션 TTL 은 10초~24시간만 받는다(기본 30초). "
                + "이 값이 곧 노드가 죽고 나서 번호가 풀리기까지의 지연이다 — 짧을수록 회수는 빠르고 갱신 트래픽은 는다.");
        }

        if (LockDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(LockDelay)}({LockDelay}) 가 음수다. 세션 무효화 후 다른 노드가 같은 번호를 잡기까지의 "
                + "유예다(기본 15초) — 0 이면 죽은 줄 알았던 노드가 아직 살아 있을 때 번호가 겹칠 수 있다.");
        }

        if (RenewInterval is { } interval)
        {
            if (interval <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"{nameof(RenewInterval)}({interval}) 이 0 이하다. 지정하지 않으면 SessionTtl 의 절반을 쓴다 — "
                    + "직접 줄 이유가 없다면 null 로 두는 것이 맞다.");
            }

            if (interval >= SessionTtl)
            {
                // 갱신이 TTL 보다 느리면 임차는 반드시 끊긴다. 조용히 두면
                // "가끔 노드가 번호를 잃는다" 로 나타난다.
                throw new InvalidOperationException(
                    $"{nameof(RenewInterval)}({interval}) 이 {nameof(SessionTtl)}({SessionTtl}) 이상이다. "
                    + "갱신이 만료보다 느리면 살아 있는 노드가 번호를 잃는다.");
            }
        }
    }

    /// <summary>실제로 쓸 갱신 주기.</summary>
    internal TimeSpan EffectiveRenewInterval => RenewInterval ?? (SessionTtl / 2);
}
