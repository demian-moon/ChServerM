using System;

namespace ChServerM.Hosting;

/// <summary>
/// <see cref="CircuitBreaker"/> 설정.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>연속 실패 임계의 기본값.</summary>
    public const int DefaultFailureThreshold = 5;

    /// <summary>차단 유지 시간의 기본값.</summary>
    public static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromSeconds(10);

    /// <summary>진단에 쓰는 이름.</summary>
    public string Name { get; set; } = "default";

    /// <summary>
    /// 회로를 여는 <b>연속</b> 실패 횟수.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ 비율이 아니라 연속 횟수를 쓴다.</b> 실패율 기반은 관측 창과 최소 표본 수를 함께
    /// 정해야 하고, 트래픽이 적을 때 표본 부족으로 판정이 흔들린다. 연속 실패는 <b>상태 하나
    /// (카운터)</b>로 판정되어 무할당·무락으로 구현되고, "대상이 죽었다" 는 신호로는 충분히
    /// 정확하다 — 죽은 대상은 연속으로 실패한다.
    /// </para>
    /// <para>
    /// 대가: 간헐적 실패(전체의 10%)는 잡지 못한다. 그것은 회로를 열 일이 아니라 재시도와
    /// 관측의 영역이라고 본다.
    /// </para>
    /// </remarks>
    public int FailureThreshold { get; set; } = DefaultFailureThreshold;

    /// <summary>
    /// 회로를 연 뒤 시험(반열림)까지 기다리는 시간.
    /// </summary>
    /// <remarks>
    /// 너무 짧으면 아직 아픈 대상을 계속 두드리고, 너무 길면 회복된 뒤에도 서비스가 막힌다.
    /// </remarks>
    public TimeSpan BreakDuration { get; set; } = DefaultBreakDuration;

    /// <summary>
    /// 반열림에서 회로를 닫기 위해 연속으로 성공해야 하는 호출 수.
    /// </summary>
    /// <remarks>
    /// <b>1 이면 요행 한 번으로 전량 재개된다.</b> 대상이 막 살아나는 중이면 그 부하가
    /// 다시 쓰러뜨린다. 기본값 2 는 "우연이 아니다" 를 확인하는 최소치다.
    /// </remarks>
    public int HalfOpenSuccessThreshold { get; set; } = 2;

    /// <summary>
    /// 반열림에서 동시에 통과시킬 시험 호출 수.
    /// </summary>
    /// <remarks>
    /// 기본값 1 — <b>시험은 한 번에 하나만</b> 보낸다. 여럿을 동시에 보내면 아직 아픈 대상에
    /// 순간 부하를 주는 것이고, 그것이 회복을 방해한다.
    /// </remarks>
    public int HalfOpenConcurrentProbes { get; set; } = 1;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났다.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Name))
        {
            throw new InvalidOperationException($"{nameof(Name)} 은 비어 있을 수 없다.");
        }

        if (FailureThreshold < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(FailureThreshold)} 은 1 이상이어야 한다. 현재: {FailureThreshold}");
        }

        if (BreakDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(BreakDuration)} 은 0 보다 커야 한다. 현재: {BreakDuration}");
        }

        if (HalfOpenSuccessThreshold < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(HalfOpenSuccessThreshold)} 은 1 이상이어야 한다. 현재: {HalfOpenSuccessThreshold}");
        }

        if (HalfOpenConcurrentProbes < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(HalfOpenConcurrentProbes)} 은 1 이상이어야 한다. 현재: {HalfOpenConcurrentProbes}");
        }
    }
}
