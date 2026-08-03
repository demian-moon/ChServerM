using System;
using System.Collections.Generic;

namespace ChServerM.Features;

/// <summary>
/// <see cref="IFeatureCollection"/>의 기본 구현.
/// </summary>
/// <remarks>
/// <para>
/// 내부 저장소를 <b>첫 등록 시점에야</b> 만든다. 기능을 하나도 안 쓰는 커넥션
/// (대부분의 인메모리 커넥션이 그렇다)은 딕셔너리 할당이 0이다.
/// </para>
/// <para>
/// <see cref="IFeatureCollection"/>의 규약대로 <b>스레드 안전하지 않다.</b>
/// </para>
/// </remarks>
public sealed class FeatureCollection : IFeatureCollection
{
    private Dictionary<Type, object>? _features;
    private int _revision;

    /// <summary>빈 모음을 만든다.</summary>
    public FeatureCollection()
    {
    }

    /// <summary>초기 용량을 지정해 만든다.</summary>
    /// <param name="capacity">예상 기능 개수. 0이면 지연 생성한다.</param>
    public FeatureCollection(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > 0)
        {
            _features = new Dictionary<Type, object>(capacity);
        }
    }

    /// <inheritdoc />
    public int Revision => _revision;

    /// <summary>등록된 기능 개수.</summary>
    public int Count => _features?.Count ?? 0;

    /// <inheritdoc />
    public TFeature? Get<TFeature>() where TFeature : class
    {
        if (_features is null)
        {
            return null;
        }

        return _features.TryGetValue(typeof(TFeature), out object? value)
            ? (TFeature)value
            : null;
    }

    /// <inheritdoc />
    public void Set<TFeature>(TFeature? instance) where TFeature : class
    {
        if (instance is null)
        {
            if (_features is not null && _features.Remove(typeof(TFeature)))
            {
                _revision++;
            }

            return;
        }

        _features ??= new Dictionary<Type, object>();
        _features[typeof(TFeature)] = instance;
        _revision++;
    }

    /// <summary>모든 등록을 지운다.</summary>
    /// <remarks>
    /// 커넥션 객체를 풀에서 재사용할 때 호출한다. 내부 저장소는 유지해 재할당을 피한다.
    /// </remarks>
    public void Reset()
    {
        if (_features is { Count: > 0 })
        {
            _features.Clear();
            _revision++;
        }
    }
}
