using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Abstract GameObject Pool Class
/// </summary>
public abstract class AbGameObjectPool
{

    List<GameObject> _objectPool = new List<GameObject>();

    abstract public GameObject CreateNewObject();


    public GameObject RequestObjectPool()
    {
        if (_objectPool.Count != 0)
        {
            _objectPool[0].SetActive(true);
            GameObject rObj = _objectPool[0];
            _objectPool.RemoveAt(0);

            return rObj;

        }
        else
        {
            return CreateNewObject();
        }
    }
    public void CollectGameObjectPool(GameObject obj)
    {
        obj.SetActive(false);
        _objectPool.Add(obj);
    }

    public void ClearGameObjectPool()
    {
        _objectPool.Clear();
        _objectPool = null;
    }
}
