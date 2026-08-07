using System.Threading;

namespace ChServerM.Hosting;

/// <summary>서버의 생명주기 단계 (Phase 11 관측 — readiness 판정의 근원).</summary>
public enum ServerState : byte
{
    /// <summary>아직 시작하지 않았다. 수용 전이라 준비되지 않았다.</summary>
    Created = 0,

    /// <summary>수용 중이다 — 트래픽을 받을 준비가 됐다.</summary>
    Accepting = 1,

    /// <summary>신규 수용을 멈추고 기존 커넥션을 드레인 중이다 — 트래픽에서 빠져야 한다.</summary>
    Draining = 2,

    /// <summary>멈췄다.</summary>
    Stopped = 3,
}

/// <summary>
/// 서버의 생명주기 단계를 담아 readiness 체크와 공유하는 홀더 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> readiness 는 "지금 트래픽을 받을 준비가 됐는가"이고, 그 답은
/// <see cref="ChServerMServer"/> 의 생명주기(<see cref="ChServerMServer.StartAsync"/> →
/// <see cref="ChServerMServer.UnbindAsync"/> → <see cref="ChServerMServer.StopAsync"/>)에
/// 이미 있다 — <c>Unbind</c> 후에는 로드밸런서가 트래픽을 빼야 한다. 서버가 이 홀더를
/// 갱신하고 <see cref="AcceptanceReadinessCheck"/> 가 읽어, 생명주기와 readiness 가
/// <b>한 진실</b>을 공유하게 한다(두 곳에서 상태를 따로 관리하면 어긋난다).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 서버가 생명주기 스레드에서 쓰고, 헬스 프로브가 다른 스레드에서
/// 읽는다. <see cref="Volatile"/> 접근으로 가시성을 보장한다 — 단일 필드라 락이 필요 없다.
/// </para>
/// </remarks>
public sealed class ServerLifecycleState
{
    private volatile ServerState _state = ServerState.Created;

    /// <summary>현재 생명주기 단계.</summary>
    public ServerState State => _state;

    /// <summary>단계를 갱신한다. 서버 생명주기 전이에서만 호출한다.</summary>
    /// <param name="state">새 단계.</param>
    internal void Set(ServerState state) => _state = state;
}
