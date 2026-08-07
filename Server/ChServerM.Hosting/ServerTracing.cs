using System.Diagnostics;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 프레임워크의 단일 <see cref="ActivitySource"/> 홀더 (Phase 11 관측, ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 커넥션 span(<see cref="TracingConnectionHandler"/>)과 디스패치
/// span(<see cref="Dispatch.TracingMiddleware"/>)이 <b>같은 원본</b>에서 나야 익스포터가
/// 이름(<see cref="DiagnosticNames.ActivitySourceName"/>) 하나로 둘 다 구독한다. 두 타입이
/// 각자 원본을 만들면 이름이 같아도 별개 원본이라 부모-자식이 어긋날 수 있고, 구독 계약이
/// 갈라진다. 그래서 원본을 한 곳에 둔다.
/// </para>
/// <para>
/// <see cref="ActivitySource"/> 는 장수명이 정상이며 스레드 안전하다 — 프로세스당 하나로 둔다.
/// </para>
/// </remarks>
internal static class ServerTracing
{
    /// <summary>프레임워크 추적 원본. 이름이 곧 구독 계약이다.</summary>
    internal static readonly ActivitySource Source = new(DiagnosticNames.ActivitySourceName);
}
