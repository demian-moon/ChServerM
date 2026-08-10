using ChServerM.Identity;
using ChServerM.Sessions;

namespace ChServerM.Features;

/// <summary>
/// 이 커넥션이 어느 세션을 소유하는가 — 커넥션과 세션의 바인딩.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 커넥션과 세션은 <b>수명이 다르다</b>. 세션은 끊긴 뒤에도 살아 있고,
/// 재접속하면 <b>다른 커넥션</b>이 같은 세션을 이어받는다(ADR-0036). 그래서 "이 커넥션이
/// 지금 다루는 세션" 을 커넥션에 붙여 둘 자리가 필요하다.
/// </para>
/// <para>
/// <b>⚠ <see cref="Version"/> 을 함께 두는 이유.</b> 세션 저장소는 낙관적 동시성이므로
/// (ADR-0033) 쓰려면 마지막으로 본 버전이 있어야 한다. 그것을 핸들러가 각자 들고 다니게 하면
/// <b>재개로 밀려났을 때 갱신할 곳이 흩어진다</b> — 한 곳에 두면 재개 핸들러가 그 값을 고쳐
/// 다음 프레임부터 올바른 버전이 쓰인다.
/// </para>
/// <para>
/// <b>버전이 어긋나면 그것이 신호다.</b> 쓰기가 <c>Conflict</c> 로 실패하면 이 커넥션은
/// <b>밀려난 것</b>이다(다른 커넥션이 재개했다). 좀비가 상태를 덮는 것을 CAS 가 막는다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션의 디스패치 순차 컨텍스트 전용이다 — 프레임 디스패치는 커넥션
/// 안에서 순차이므로(ADR-0008) 한 프레임에서 쓴 값을 다음 프레임이 읽는 것이 안전하다.
/// 디스패치 밖(다른 스레드)에서 읽고 쓰면 그 보장이 없다.
/// </para>
/// </remarks>
public interface ISessionFeature
{
    /// <summary>
    /// 이 커넥션이 소유한 세션. 아직 바인딩되지 않았으면 <see cref="SessionId.None"/>.
    /// </summary>
    SessionId SessionId { get; set; }

    /// <summary>
    /// 마지막으로 관측한 세션 버전. 다음 쓰기의 기대 버전으로 쓴다.
    /// </summary>
    SessionVersion Version { get; set; }
}
