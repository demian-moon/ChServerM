using ChServerM.Dispatch;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// <see cref="DispatchStatus"/> 이름의 정적 캐시 — 실패 경로의 <c>enum.ToString()</c> 할당 제거.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 거부·실패 태그가 필요한 곳은 정확히 속도 제한·열화 거부가 <b>과부하 시
/// 프레임마다</b> 발생하는 경로다. <c>Enum.ToString()</c> 은 매 호출 힙 할당이라, 부하가 가장
/// 높을 때 GC 압력을 더하는 구조였다 — "핫패스 무할당" 하드 룰과 상충한다
/// (감사 2026-08-18 H-4/O-9). 상태값은 유한 enum 이므로 이름을 정적으로 한 번만 만들어 두고
/// 항상 <b>같은 참조</b>를 돌려준다. <see cref="MetricsMiddleware"/> 와
/// <see cref="TracingMiddleware"/> 가 공유한다.
/// </para>
/// <para>
/// <b>배열 인덱스 = enum 값.</b> <see cref="DispatchStatus"/> 는 <c>None(0)</c> 부터 연속이다
/// (감사 C-1 로 0 이 센티넬로 추가됐다 — 인덱스 0 을 비우면 전 항목이 밀린다).
/// <c>nameof</c> 로 컴파일러가 이름을 검증하므로 리플렉션(<c>Enum.GetNames</c>) 없이 AOT 안전
/// 하다. 새 멤버가 추가되면 이 배열에도 추가해야 한다 — 누락은 회귀 테스트(모든 enum 값에
/// 대해 이름 일치 + 참조 동일성)가 잡고, 범위를 벗어난 값은 <c>ToString()</c> 폴백이다
/// (할당되지만, 정의되지 않은 값이 오는 것 자체가 버그다).
/// </para>
/// <para><b>스레드 규약.</b> 불변 정적 데이터라 어디서든 안전하다.</para>
/// </remarks>
internal static class DispatchStatusNames
{
    private static readonly string[] Names =
    [
        nameof(DispatchStatus.None),
        nameof(DispatchStatus.Handled),
        nameof(DispatchStatus.HandlerNotFound),
        nameof(DispatchStatus.RejectedByState),
        nameof(DispatchStatus.RejectedByPolicy),
        nameof(DispatchStatus.DeserializationFailed),
        nameof(DispatchStatus.RejectedByBackpressure),
        nameof(DispatchStatus.Faulted),
        nameof(DispatchStatus.Canceled),
        nameof(DispatchStatus.RejectedByAuthentication),
        nameof(DispatchStatus.RejectedByRateLimit),
        nameof(DispatchStatus.RejectedByLoadShedding),
    ];

    /// <summary>상태의 이름을 할당 없이 돌려준다. 같은 상태는 항상 같은 문자열 참조다.</summary>
    /// <param name="status">디스패치 결과.</param>
    /// <returns>enum 멤버 이름. 정의되지 않은 값이면 <c>ToString()</c> 폴백.</returns>
    internal static string Get(DispatchStatus status)
    {
        string[] names = Names;
        return (uint)status < (uint)names.Length ? names[(int)status] : status.ToString();
    }
}
