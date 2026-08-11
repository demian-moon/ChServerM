using System;

namespace ChServerM.RealTime;

/// <summary>
/// 시간 동기화 왕복 한 번의 계산 결과 — NTP 식 4-타임스탬프 방식.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시 <c>NetWorkDelayM</c>은 "(수신 − 송신) / 2"라는 2-타임스탬프
/// 방식이라 <b>상대의 처리 지연이 통째로 네트워크 지연에 섞였다.</b> 4-타임스탬프는 상대가
/// 수신·응답 시각을 함께 돌려줘 처리 지연을 빼고 계산한다 — 레거시 분석의 개선안
/// (Phase 17 NTP 검토)을 채택한 것이다(ADR-0063).
/// </para>
/// <para>
/// 왕복 한 번의 흐름 (t₁·t₄ 는 요청자 시계, t₂·t₃ 는 응답자 시계 — 각자
/// <see cref="MicrosecondClock"/>):
/// </para>
/// <code>
/// t₁ 요청 송신 → t₂ 상대 수신 → (상대 처리) → t₃ 응답 송신 → t₄ 응답 수신
/// 왕복 = (t₄−t₁) − (t₃−t₂)          — 상대 처리 시간이 빠진 순수 네트워크 왕복
/// 오프셋 = ((t₂−t₁) + (t₃−t₄)) / 2  — 상대 시계 − 내 시계 (경로 대칭 가정)
/// </code>
/// <para>
/// <b>한계 명시.</b> 오프셋은 왕복 경로가 대칭이라는 가정 위에 있다. 비대칭 경로에서는
/// 편도 차이의 절반만큼 오차가 생긴다 — 알고리즘의 한계이며 감출 수 없다(레거시 분석 #6,
/// 문서화 의무 항목).
/// </para>
/// </remarks>
public readonly struct TimeSyncExchange : IEquatable<TimeSyncExchange>
{
    private TimeSyncExchange(long offsetMicros, long roundTripMicros)
    {
        OffsetMicros = offsetMicros;
        RoundTripMicros = roundTripMicros;
    }

    /// <summary>상대 시계 − 내 시계 (µs). 내 시각에 더하면 상대 시각의 추정값이 된다.</summary>
    public long OffsetMicros { get; }

    /// <summary>상대 처리 시간을 뺀 순수 네트워크 왕복 (µs).</summary>
    public long RoundTripMicros { get; }

    /// <summary>왕복 한 번의 타임스탬프 4개로 오프셋·왕복을 계산한다.</summary>
    /// <param name="requestSentMicros">t₁ — 요청 송신 시각(내 시계).</param>
    /// <param name="peerReceivedMicros">t₂ — 상대 수신 시각(상대 시계).</param>
    /// <param name="peerRepliedMicros">t₃ — 응답 송신 시각(상대 시계).</param>
    /// <param name="responseReceivedMicros">t₄ — 응답 수신 시각(내 시계).</param>
    /// <exception cref="ArgumentException">
    /// 같은 시계의 시각이 역행할 때(t₄&lt;t₁ 또는 t₃&lt;t₂). 단조 시계에서 역행은 호출자
    /// 버그의 신호다 — 0으로 뭉개지 않는다.
    /// </exception>
    public static TimeSyncExchange Compute(
        long requestSentMicros,
        long peerReceivedMicros,
        long peerRepliedMicros,
        long responseReceivedMicros)
    {
        if (responseReceivedMicros < requestSentMicros)
        {
            throw new ArgumentException(
                $"t₄({responseReceivedMicros}) < t₁({requestSentMicros}) — 요청자 시계가 역행했다. " +
                "두 값은 같은 시계에서 나와야 한다.");
        }

        if (peerRepliedMicros < peerReceivedMicros)
        {
            throw new ArgumentException(
                $"t₃({peerRepliedMicros}) < t₂({peerReceivedMicros}) — 응답자 시계가 역행했다. " +
                "두 값은 같은 시계에서 나와야 한다.");
        }

        long roundTrip = (responseReceivedMicros - requestSentMicros) - (peerRepliedMicros - peerReceivedMicros);
        long offset = ((peerReceivedMicros - requestSentMicros) + (peerRepliedMicros - responseReceivedMicros)) / 2;
        return new TimeSyncExchange(offset, roundTrip);
    }

    /// <inheritdoc />
    public bool Equals(TimeSyncExchange other) =>
        OffsetMicros == other.OffsetMicros && RoundTripMicros == other.RoundTripMicros;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TimeSyncExchange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(OffsetMicros, RoundTripMicros);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(TimeSyncExchange left, TimeSyncExchange right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(TimeSyncExchange left, TimeSyncExchange right) => !left.Equals(right);
}
