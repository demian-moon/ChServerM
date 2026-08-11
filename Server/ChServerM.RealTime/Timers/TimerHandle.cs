using System;

namespace ChServerM.RealTime;

/// <summary>
/// 예약된 타이머의 핸들. 취소에 쓴다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시는 타이머 ID 가 문자열이라 추가·제거마다 해싱했고, ID 생성 버그
/// (<c>StringBuilder(int)</c> 용량 생성자 오용)로 모든 ID 가 <c>""</c>가 되어 두 번째 이후
/// 예약이 조용히 사라졌다. 이 핸들은 노드 참조 + 세대라 해싱이 없고, 낡은 핸들(이미 발화·
/// 취소되어 노드가 재사용된 뒤)의 취소는 세대 불일치로 <b>구조적으로 실패</b>한다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="TryCancel"/>은 아무 스레드에서나 안전하고, 여러 번·여러
/// 스레드가 불러도 정확히 한 번만 성공한다.
/// </para>
/// </remarks>
public readonly struct TimerHandle : IEquatable<TimerHandle>
{
    private readonly TimerWheel? _wheel;
    private readonly TimerWheel.TimerNode? _node;
    private readonly uint _generation;

    internal TimerHandle(TimerWheel wheel, TimerWheel.TimerNode node, uint generation)
    {
        _wheel = wheel;
        _node = node;
        _generation = generation;
    }

    /// <summary>빈 핸들. 거부된 예약이 이것을 받는다.</summary>
    public static TimerHandle None => default;

    /// <summary>유효한 예약을 가리키는지 여부. 발화·취소 뒤에도 <see langword="true"/>일 수 있다 — 취소 가능 여부는 <see cref="TryCancel"/>의 반환값이 정본이다.</summary>
    public bool IsNone => _node is null;

    /// <summary>타이머 취소를 시도한다.</summary>
    /// <returns>
    /// 이 호출이 취소를 확정했으면 <see langword="true"/> — 이 경우
    /// <see cref="ITimerJob.OnTimerCanceled"/>가 이 스레드에서 호출된 뒤 반환된다.
    /// 이미 발화·취소됐거나 빈 핸들이면 <see langword="false"/>.
    /// </returns>
    public bool TryCancel() =>
        _wheel is not null && _node is not null && _wheel.TryCancelNode(_node, _generation);

    /// <inheritdoc />
    public bool Equals(TimerHandle other) =>
        ReferenceEquals(_wheel, other._wheel) &&
        ReferenceEquals(_node, other._node) &&
        _generation == other._generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TimerHandle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_wheel, _node, _generation);

    /// <summary>두 핸들이 같은 예약을 가리키는지 비교한다.</summary>
    public static bool operator ==(TimerHandle left, TimerHandle right) => left.Equals(right);

    /// <summary>두 핸들이 다른 예약을 가리키는지 비교한다.</summary>
    public static bool operator !=(TimerHandle left, TimerHandle right) => !left.Equals(right);
}
