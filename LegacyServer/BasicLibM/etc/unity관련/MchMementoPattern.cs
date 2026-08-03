using UnityEngine;
using System.Collections;
using System;

public class StatMemento<T> where T : ICloneable
{
    T _stats;
    public T GetStat()
    {
        return (T)_stats.Clone();
    }

    public StatMemento(T stat)
    {
        _stats = stat;
    }

}


public class StatMgr<T> where T : ICloneable
{
    public T _stats;

    public StatMgr(T stat)
    {
        _stats = stat;
    }

    public void RestoreStat(StatMemento<T> m)
    {
        _stats = m.GetStat();
    }

    public StatMemento<T> SaveToStat()
    {
        return new StatMemento<T>((T)_stats.Clone());
    }

}