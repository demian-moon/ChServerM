using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ChServerM.Identity;

namespace ChServerM.RealTime.Spatial;

/// <summary>
/// 관찰자 하나의 관심 집합 — 프레임 간 집합 차분으로 Enter/Leave 를 만든다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "시야에 새로 들어온 것(Enter)에는 등장 패킷, 나간 것(Leave)에는 소멸
/// 패킷"은 AOI 와 충돌 이벤트 양쪽의 공통 골격이다. 이전 프레임 집합과 현재 프레임 집합의
/// 차집합으로 Leave 를, 여집합으로 Enter 를 구하는 <b>집합 차분 알고리즘은 레거시
/// <c>CollisionEventGenerate</c>에서 정석으로 확인된 승계 자산</b>이다.
/// </para>
/// <para>
/// <b>막는 레거시 결함.</b> 원본은 프레임마다 <c>new HashSet&lt;Entity&gt;()</c>를 만들어
/// 엔티티 수만큼 할당이 터졌다. 여기서는 집합 두 개를 <b>스왑 + Clear 로 재사용</b>한다 —
/// 정상 상태 할당 0. Stay 는 별도 목록을 만들지 않는다: "현재 집합에 있고 Enter 가 아닌
/// 것"이 곧 Stay 이고, 그 스로틀(레거시 0.1초 딜레이 승계)은 호출자가 틱 수·
/// <c>IntervalGate</c>로 건다 — 이 타입은 집합만 안다.
/// </para>
/// <para>
/// <b>스레드 규약 — 안전하지 않다.</b> 관찰자 하나의 소유 실행 컨텍스트 전용
/// (<see cref="InterestGrid"/>와 같은 규약, CLAUDE.md 9.1).
/// </para>
/// <para>
/// <b>사용 규약.</b> <see cref="BeginUpdate"/> → <see cref="Observe"/> × N →
/// <see cref="EndUpdate"/> 순서. <see cref="Entered"/>/<see cref="Left"/> 스팬은 다음
/// <see cref="BeginUpdate"/>까지만 유효하다.
/// </para>
/// </remarks>
public sealed class InterestSet
{
    private HashSet<ObjectId> _building = [];
    private HashSet<ObjectId> _active = [];
    private readonly List<ObjectId> _entered = [];
    private readonly List<ObjectId> _left = [];
    private bool _updating;

    /// <summary>마지막으로 확정된 관심 집합의 크기.</summary>
    public int Count => _active.Count;

    /// <summary>마지막으로 확정된 관심 집합에 있는지 여부.</summary>
    public bool Contains(ObjectId id) => _active.Contains(id);

    /// <summary>이번 갱신에서 새로 들어온 것들. 다음 <see cref="BeginUpdate"/>까지 유효하다.</summary>
    public ReadOnlySpan<ObjectId> Entered => CollectionsMarshal.AsSpan(_entered);

    /// <summary>이번 갱신에서 나간 것들. 다음 <see cref="BeginUpdate"/>까지 유효하다.</summary>
    public ReadOnlySpan<ObjectId> Left => CollectionsMarshal.AsSpan(_left);

    /// <summary>프레임 갱신을 시작한다.</summary>
    /// <exception cref="InvalidOperationException">이미 갱신 중일 때 — 짝이 안 맞는 호출은 버그다.</exception>
    public void BeginUpdate()
    {
        if (_updating)
        {
            throw new InvalidOperationException($"{nameof(EndUpdate)} 없이 {nameof(BeginUpdate)}가 다시 불렸다.");
        }

        _updating = true;
        _building.Clear(); // 두 프레임 전의 집합을 재사용한다 — 프레임당 할당 0 의 핵심.
        _entered.Clear();
        _left.Clear();
    }

    /// <summary>이번 프레임에 관측된 대상을 등록한다. 대개 <see cref="InterestGrid"/> 질의 결과를 넣는다.</summary>
    /// <returns>새로 들어온 것(Enter)이면 <see langword="true"/>.</returns>
    /// <exception cref="InvalidOperationException">갱신 구간 밖에서 불렸을 때.</exception>
    public bool Observe(ObjectId id)
    {
        if (!_updating)
        {
            throw new InvalidOperationException($"{nameof(BeginUpdate)} 없이 {nameof(Observe)}가 불렸다.");
        }

        if (!_building.Add(id))
        {
            return false; // 같은 프레임의 중복 관측 — 셀 경계에 걸친 질의에서 정상이다.
        }

        if (!_active.Contains(id))
        {
            _entered.Add(id);
            return true;
        }

        return false;
    }

    /// <summary>프레임 갱신을 확정한다. Leave 를 계산하고 집합을 스왑한다.</summary>
    /// <exception cref="InvalidOperationException">갱신 구간 밖에서 불렸을 때.</exception>
    public void EndUpdate()
    {
        if (!_updating)
        {
            throw new InvalidOperationException($"{nameof(BeginUpdate)} 없이 {nameof(EndUpdate)}가 불렸다.");
        }

        foreach (ObjectId id in _active)
        {
            if (!_building.Contains(id))
            {
                _left.Add(id);
            }
        }

        (_active, _building) = (_building, _active);
        _updating = false;
    }
}
