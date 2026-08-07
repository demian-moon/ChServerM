using System;

namespace ChServerM.Diagnostics;

/// <summary>
/// 헬스 체크가 어느 프로브에 속하는지 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 계약(<see cref="IHealthCheck"/>)이 아니라 등록에 두는가.</b> liveness 와 readiness 는
/// <b>같은 체크를 다르게 쓰는 것</b>이지 다른 체크가 아니다 — 프로세스 존재 여부는 둘 다에
/// 기여할 수 있다. 그래서 프로브 소속은 체크 자신이 아니라 등록이 정한다. <see cref="IHealthCheck"/>
/// 계약을 최소로 유지하는 선택이다.
/// </para>
/// <para>
/// <b>이 값 타입은 Core 에 둔다.</b> <see cref="HealthStatus"/>·<see cref="HealthReport"/> 와 같은
/// 헬스 어휘의 값 타입이라, HTTP 노출 어댑터처럼 Hosting 을 참조하지 않는 소비자도 이것으로
/// 프로브를 지정할 수 있어야 한다(일방 의존 규칙 유지).
/// </para>
/// <para>
/// <b>두 프로브의 의미가 다르다(오케스트레이터 계약).</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b><see cref="Liveness"/></b> — "프로세스가 살아 동작하는가". 실패하면 오케스트레이터가
///     <b>재시작</b>한다. 그래서 보수적이어야 한다 — 일시적 의존성 문제로 실패하면 멀쩡한
///     프로세스가 재시작 루프에 빠진다. 실행 모델 스레드 생존처럼 <b>확정적 고장</b>만 본다.
///   </description></item>
///   <item><description>
///     <b><see cref="Readiness"/></b> — "지금 트래픽을 받을 준비가 됐는가". 실패하면
///     오케스트레이터가 <b>트래픽에서 제외</b>(디레지스터)한다. 드레이닝·의존성 미준비가 여기다.
///   </description></item>
/// </list>
/// </remarks>
[Flags]
public enum HealthProbe
{
    /// <summary>어느 프로브에도 속하지 않는다.</summary>
    None = 0,

    /// <summary>생존 프로브 — 실패 시 재시작 대상.</summary>
    Liveness = 1,

    /// <summary>준비 프로브 — 실패 시 트래픽 제외 대상.</summary>
    Readiness = 2,

    /// <summary>두 프로브 모두.</summary>
    All = Liveness | Readiness,
}
