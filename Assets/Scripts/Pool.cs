using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    /// <summary>极简对象池。</summary>
    public class Pool<T> where T : Component
    {
        readonly Func<T> factory;
        readonly Transform parent;
        readonly Stack<T> free = new Stack<T>();

        public Pool(Func<T> factory, Transform parent, int prewarm = 0)
        {
            this.factory = factory;
            this.parent = parent;
            for (int i = 0; i < prewarm; i++)
            {
                var o = Create();
                o.gameObject.SetActive(false);
                free.Push(o);
            }
        }

        T Create()
        {
            var o = factory();
            if (o != null && parent != null) o.transform.SetParent(parent, false);
            return o;
        }

        public T Get()
        {
            T o = null;
            while (free.Count > 0 && o == null) o = free.Pop();
            if (o == null) o = Create();
            if (o == null) return null;
            if (parent != null) o.transform.SetParent(parent, false);
            o.gameObject.SetActive(true);
            return o;
        }

        public void Release(T o)
        {
            if (o == null) return;
            o.gameObject.SetActive(false);
            free.Push(o);
        }
    }
}
