using System;
using System.Collections.Generic;
using System.Numerics;
using ChServerM.Identity;

namespace ChServerM.RealTime.Spatial;

/// <summary>
/// 관심 영역(AOI) 균일 그리드. 엔티티 위치를 셀에 색인해 반경·영역 질의를 셀 단위로 줄인다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "내 주변의 엔티티"를 전수 순회로 찾으면 O(전체)이고, 브로드캐스트
/// 대상 선정이 곧 서버의 틱 예산이 된다. 균일 그리드는 삽입·이동·제거 O(1), 질의는
/// 겹치는 셀만 본다. 레거시에는 <b>승계할 구현이 없다</b> — QuadGrid/LQuadTree 는 주석과
/// 빈 필드뿐이었고, 유일한 생존 자산인 모튼 코드(<see cref="MortonCode"/>)를 셀 키로 쓴다
/// (ADR-0065 — 쿼드트리 대신 그리드를 고른 이유 포함).
/// </para>
/// <para>
/// <b>막는 레거시 결함.</b> 원본 <c>MortonIndex2</c>는 0 나누기와 범위 밖 좌표의 조용한
/// 오매핑이 있었다. 여기서는 ① 셀 크기·개수를 생성 시점에 검증하고 ② 범위 밖 좌표는
/// <b>가장자리 셀로 클램프</b>한다 — 진짜 위치는 엔트리에 따로 보관하므로 질의의 정밀
/// 필터가 정확한 결과를 유지한다(셀은 후보 축소 수단이지 정답이 아니다).
/// </para>
/// <para>
/// <b>스레드 규약 — 안전하지 않다.</b> 존(맵) 하나의 소유 실행 컨텍스트(대개 틱 루프) 전용.
/// 존 단위가 곧 파티션이다(CLAUDE.md 9.1) — 그래서 내부에 락도 <c>Concurrent*</c>도 없다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 셀 리스트는 내부 풀로 재사용한다 — 이동·질의의 정상 상태 할당 0.
/// 질의 결과는 호출자의 리스트에 덧붙인다(호출자 소유).
/// </para>
/// </remarks>
public sealed class InterestGrid
{
    private readonly Vector2 _worldMin;
    private readonly float _inverseCellSize;
    private readonly int _maxCellIndex;

    private readonly Dictionary<ObjectId, Entry> _entries = [];
    private readonly Dictionary<uint, List<ObjectId>> _cells = [];
    private readonly Stack<List<ObjectId>> _cellPool = new();

    private readonly struct Entry
    {
        internal Entry(Vector2 position, uint cellKey)
        {
            Position = position;
            CellKey = cellKey;
        }

        internal Vector2 Position { get; }

        internal uint CellKey { get; }
    }

    /// <summary>그리드를 만든다.</summary>
    /// <param name="worldMin">그리드가 덮는 영역의 최소 모서리.</param>
    /// <param name="cellSize">셀 한 변의 길이. 관심 반경과 같은 자릿수로 잡는 것이 보통이다.</param>
    /// <param name="cellsPerAxis">축당 셀 수. 2의 거듭제곱, 2~65,536 (모튼 키가 축당 16비트다).</param>
    /// <exception cref="ArgumentException">셀 크기·개수가 유효하지 않을 때.</exception>
    public InterestGrid(Vector2 worldMin, float cellSize, int cellsPerAxis)
    {
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
        {
            throw new ArgumentException($"셀 크기({cellSize})는 유한한 양수여야 한다. 0 나누기의 자리다.", nameof(cellSize));
        }

        if (cellsPerAxis < 2 || cellsPerAxis > 65_536 || (cellsPerAxis & (cellsPerAxis - 1)) != 0)
        {
            throw new ArgumentException(
                $"축당 셀 수({cellsPerAxis})는 2~65,536 의 2의 거듭제곱이어야 한다.", nameof(cellsPerAxis));
        }

        _worldMin = worldMin;
        _inverseCellSize = 1f / cellSize;
        _maxCellIndex = cellsPerAxis - 1;
    }

    /// <summary>색인된 엔티티 수.</summary>
    public int Count => _entries.Count;

    /// <summary>엔티티를 추가한다.</summary>
    /// <returns>이미 있으면 <see langword="false"/>.</returns>
    public bool Add(ObjectId id, Vector2 position)
    {
        if (_entries.ContainsKey(id))
        {
            return false;
        }

        uint cellKey = CellKeyOf(position);
        AddToCell(cellKey, id);
        _entries[id] = new Entry(position, cellKey);
        return true;
    }

    /// <summary>엔티티 위치를 갱신한다. 셀이 바뀌면 옮긴다.</summary>
    /// <returns>색인에 없으면 <see langword="false"/>.</returns>
    public bool Update(ObjectId id, Vector2 position)
    {
        if (!_entries.TryGetValue(id, out Entry entry))
        {
            return false;
        }

        uint cellKey = CellKeyOf(position);
        if (cellKey != entry.CellKey)
        {
            RemoveFromCell(entry.CellKey, id);
            AddToCell(cellKey, id);
        }

        _entries[id] = new Entry(position, cellKey);
        return true;
    }

    /// <summary>엔티티를 제거한다.</summary>
    /// <returns>색인에 없으면 <see langword="false"/>.</returns>
    public bool Remove(ObjectId id)
    {
        if (!_entries.Remove(id, out Entry entry))
        {
            return false;
        }

        RemoveFromCell(entry.CellKey, id);
        return true;
    }

    /// <summary>색인된 위치를 조회한다.</summary>
    public bool TryGetPosition(ObjectId id, out Vector2 position)
    {
        if (_entries.TryGetValue(id, out Entry entry))
        {
            position = entry.Position;
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>반경 안의 엔티티를 <paramref name="results"/>에 덧붙인다.</summary>
    /// <param name="center">원의 중심.</param>
    /// <param name="radius">반지름. 음수·NaN 이면 안 된다.</param>
    /// <param name="results">결과를 덧붙일 리스트(호출자 소유). 비우지 않는다.</param>
    /// <returns>덧붙인 수.</returns>
    /// <remarks>셀은 후보 축소용이고, 판정은 저장된 실제 위치와의 거리 제곱 비교다(경계 포함).</remarks>
    public int QueryCircle(Vector2 center, float radius, ICollection<ObjectId> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!(radius >= 0f))
        {
            throw new ArgumentException($"반지름({radius})은 음수·NaN 일 수 없다.", nameof(radius));
        }

        float radiusSquared = radius * radius;
        int added = 0;
        Vector2 extent = new(radius, radius);

        ForEachCellInRange(center - extent, center + extent, (grid: this, center, radiusSquared, results),
            static (state, cell) =>
            {
                int count = 0;
                foreach (ObjectId id in cell)
                {
                    if (Vector2.DistanceSquared(state.grid._entries[id].Position, state.center) <= state.radiusSquared)
                    {
                        state.results.Add(id);
                        count++;
                    }
                }

                return count;
            }, ref added);

        return added;
    }

    /// <summary>영역 안의 엔티티를 <paramref name="results"/>에 덧붙인다.</summary>
    /// <param name="area">질의 영역. 경계 포함(<see cref="Aabb"/> 규칙).</param>
    /// <param name="results">결과를 덧붙일 리스트(호출자 소유). 비우지 않는다.</param>
    /// <returns>덧붙인 수.</returns>
    public int QueryAabb(in Aabb area, ICollection<ObjectId> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        int added = 0;
        ForEachCellInRange(area.Min, area.Max, (grid: this, area, results),
            static (state, cell) =>
            {
                int count = 0;
                foreach (ObjectId id in cell)
                {
                    if (state.area.Contains(state.grid._entries[id].Position))
                    {
                        state.results.Add(id);
                        count++;
                    }
                }

                return count;
            }, ref added);

        return added;
    }

    private void ForEachCellInRange<TState>(
        Vector2 min, Vector2 max, TState state, Func<TState, List<ObjectId>, int> visit, ref int added)
    {
        int minX = CellIndexOf(min.X - _worldMin.X);
        int minY = CellIndexOf(min.Y - _worldMin.Y);
        int maxX = CellIndexOf(max.X - _worldMin.X);
        int maxY = CellIndexOf(max.Y - _worldMin.Y);

        for (int cellY = minY; cellY <= maxY; cellY++)
        {
            for (int cellX = minX; cellX <= maxX; cellX++)
            {
                if (_cells.TryGetValue(MortonCode.Encode((ushort)cellX, (ushort)cellY), out List<ObjectId>? cell))
                {
                    added += visit(state, cell);
                }
            }
        }
    }

    private uint CellKeyOf(Vector2 position) =>
        MortonCode.Encode(
            (ushort)CellIndexOf(position.X - _worldMin.X),
            (ushort)CellIndexOf(position.Y - _worldMin.Y));

    /// <summary>좌표 오프셋 → 셀 인덱스. 범위 밖·NaN 은 가장자리 셀로 클램프한다(조용한 오매핑 방지).</summary>
    private int CellIndexOf(float offset)
    {
        float scaled = offset * _inverseCellSize;
        if (!(scaled > 0f))
        {
            return 0; // 음수와 NaN 이 모두 여기로 온다.
        }

        return scaled >= _maxCellIndex ? _maxCellIndex : (int)scaled;
    }

    private void AddToCell(uint cellKey, ObjectId id)
    {
        if (!_cells.TryGetValue(cellKey, out List<ObjectId>? cell))
        {
            cell = _cellPool.Count > 0 ? _cellPool.Pop() : [];
            _cells[cellKey] = cell;
        }

        cell.Add(id);
    }

    private void RemoveFromCell(uint cellKey, ObjectId id)
    {
        List<ObjectId> cell = _cells[cellKey];

        // 스왑 제거 — 셀 안의 순서는 의미가 없으므로 뒤 항목을 당겨 O(1)로 지운다.
        int index = cell.IndexOf(id);
        cell[index] = cell[^1];
        cell.RemoveAt(cell.Count - 1);

        if (cell.Count == 0)
        {
            _cells.Remove(cellKey);
            _cellPool.Push(cell);
        }
    }
}
