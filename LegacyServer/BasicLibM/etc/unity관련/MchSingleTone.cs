using UnityEngine;
using System.Collections;

public abstract class MchSingleton<T> where T:class, new()
{

    protected MchSingleton() { }
    protected static T _instance = null;
    
    public static T instance
    {
        get
        {
            if (_instance == null)
                _instance = new T();
            return _instance;
        }
    }
}

