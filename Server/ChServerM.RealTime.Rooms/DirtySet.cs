using System;
using System.Collections.Generic;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 더티 집합 — "이번 틱에 변한 것"을 중복 없이 모았다가 한 번에 비운다. 델타 전송의 수집 단이다.
/// </summary>
/// <typeparam name="T">추적 키(대개 <c>ObjectId</c>·<c>ConnectionId</c> 같은 강타입 ID).</typeparam>
/// <remarks>
/// <para>
/// <b>존재 이유 — "변경분만 전송"의 전반부.</b> 매 틱 전체 상태를 브로드캐스트하면 대역폭이
/// 인원 × 엔티티에 비례해 터진다. 델타 전송의 첫 단계는 "무엇이 변했는가"를 <b>중복 없이</b>
/// 모으는 것이다 — 같은 엔티티가 한 틱에 열 번 움직여도 스냅샷은 한 번이면 된다.
/// 레거시의 두 자산을 합친 승계다: <c>NeedPkSendM</c>(변경 플래그를 세워 필요할 때만 전송)와
/// <c>UniqueBufferBlock</c>(이미 예약된 항목을 다시 예약하지 않는 더티 큐 발상 — 단
/// 원본의 <c>Console.WriteLine</c> 병목 없이).
/// </para>
/// <para>
/// <b>무엇이 변했는지의 종류</b>(레거시 5종 bool 프로퍼티)는 앱의 <c>[Flags]</c> enum 몫이다 —
/// 이 타입은 "누가 변했는가"만 안다. 변경 내용의 인코딩(필드 수준 델타)은 직렬화 축의
/// 영역이라 여기 없다.
/// </para>
/// <para>
/// <b>스레드 규약 — 안전하지 않다.</b> 존 하나의 소유 실행 컨텍스트(대개 틱 루프) 전용.
/// </para>
/// <para>
/// <b>수명 규약.</b> <see cref="Drain"/>이 돌려주는 스팬은 다음 <see cref="Mark"/>·
/// <see cref="Drain"/>까지만 유효하다. 내부 버퍼는 재사용된다 — 정상 상태 할당 0.
/// </para>
/// </remarks>
public sealed class DirtySet<T> where T : notnull, IEquatable<T>
{
    private readonly HashSet<T> _set = [];
    private T[] _drainBuffer = new T[16];

    /// <summary>현재 더티로 표시된 항목 수.</summary>
    public int Count => _set.Count;

    /// <summary>항목을 더티로 표시한다.</summary>
    /// <returns>새로 표시됐으면 <see langword="true"/>, 이미 더티였으면 <see langword="false"/>.</returns>
    public bool Mark(T item) => _set.Add(item);

    /// <summary>더티 여부를 조회한다.</summary>
    public bool IsMarked(T item) => _set.Contains(item);

    /// <summary>표시 하나를 지운다(전송 전에 제거된 엔티티 등).</summary>
    public bool Unmark(T item) => _set.Remove(item);

    /// <summary>더티 항목 전부를 비우고 돌려준다. 순서는 보장하지 않는다.</summary>
    /// <returns>비워진 항목들. 다음 <see cref="Mark"/>·<see cref="Drain"/>까지만 유효하다.</returns>
    public ReadOnlySpan<T> Drain()
    {
        int count = _set.Count;
        if (count == 0)
        {
            return [];
        }

        if (_drainBuffer.Length < count)
        {
            _drainBuffer = new T[Math.Max(count, _drainBuffer.Length * 2)];
        }

        _set.CopyTo(_drainBuffer);
        _set.Clear();
        return _drainBuffer.AsSpan(0, count);
    }
}
