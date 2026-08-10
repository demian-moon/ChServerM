using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ChServerM.SourceGen;

/// <summary>
/// 값 동등성을 가지는 읽기 전용 배열 — <b>증분 제너레이터 캐시의 전제 조건</b>.
/// </summary>
/// <typeparam name="T">요소 형식. 값 동등성이어야 한다.</typeparam>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="ImmutableArray{T}"/> 의 <c>Equals</c> 는 <b>바탕 배열의 참조</b>를
/// 비교한다. 그래서 모델 record 안에 그대로 담으면, 내용이 똑같아도 <b>키를 한 번 누를 때마다</b>
/// 새 배열이 만들어져 "모델이 바뀌었다" 로 판정되고 생성이 통째로 다시 돈다 — 증분 파이프라인을
/// 만들어 놓고 그 이점을 스스로 버리는 셈이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 불변이며 스레드 안전하다.
/// </para>
/// </remarks>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    /// <summary>배열을 감싼다.</summary>
    /// <param name="items">요소. 기본값이면 빈 배열로 취급한다.</param>
    public EquatableArray(ImmutableArray<T> items) => _items = items.IsDefault ? ImmutableArray<T>.Empty : items;

    /// <summary>요소 수.</summary>
    public int Count => _items.IsDefault ? 0 : _items.Length;

    /// <summary>인덱스로 요소를 얻는다.</summary>
    /// <param name="index">인덱스.</param>
    public T this[int index] => _items[index];

    /// <inheritdoc/>
    public bool Equals(EquatableArray<T> other)
    {
        if (Count != other.Count)
        {
            return false;
        }

        for (int i = 0; i < Count; i++)
        {
            if (!_items[i].Equals(other._items[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // 순서까지 반영하는 단순 조합. 캐시 키 용도이므로 충돌 저항이 목적이 아니다.
        int hash = 17;
        for (int i = 0; i < Count; i++)
        {
            hash = (hash * 31) + _items[i].GetHashCode();
        }

        return hash;
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return _items[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
