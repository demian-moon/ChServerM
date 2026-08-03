using Collections.Pooled;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using static log4net.Appender.FileAppender;

namespace EcsServerLibM
{

	public class ConcurrentSparseSetM<T> : IDisposable where T : struct, IEquatable<T>
	{
		private readonly PooledDictionary<T, int> _sparse;
		private readonly PooledList<T> _dense;
		private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
		private int _count;
		private bool disposedValue;

		public ConcurrentSparseSetM(int capacity)
		{
			_sparse = new PooledDictionary<T, int>(capacity);
			_dense = new PooledList<T>(capacity);
			_count = 0;
		}

		public ConcurrentSparseSetM()
		{
			_sparse = new PooledDictionary<T, int>();
			_dense = new PooledList<T>();
			_count = 0;
		}

		public void Clear()
		{
			_lock.EnterWriteLock(); // ReadWrite 불가 Lock
			try
			{
				_sparse.Clear();
				_dense.Clear();
				_count = 0;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}


		public bool TryAdd(T value)
		{
			_lock.EnterWriteLock();
			try
			{
				if (_sparse.ContainsKey(value))
					return false;

				_sparse[value] = _count;
				_dense.Add(value);
				_count++;
				return true;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public bool TryRemove(T value)
		{
			_lock.EnterWriteLock();
			try
			{
				if (!_sparse.TryGetValue(value, out int index))
					return false;

				_count--;
				if (index < _count)
				{
					T lastValue = _dense[_count];

					_dense[index] = lastValue;
					_sparse[lastValue] = index;
				}

				_dense.RemoveAt(_count);
				_sparse.Remove(value);
				return true;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public bool Contains(T value)
		{
			_lock.EnterReadLock();
			try
			{
				return _sparse.ContainsKey(value);
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		public int Count
		{
			get
			{
				_lock.EnterReadLock();
				try
				{
					return _count;
				}
				finally
				{
					_lock.ExitReadLock();
				}
			}
		}


		public T Get(int index)
		{
			_lock.EnterReadLock();
			try
			{
				if (index >= 0 && index < _count)
				{
					return _dense[index];
				}
			}
			finally
			{
				_lock.ExitReadLock();
			}
			throw new IndexOutOfRangeException($"Index {index} is out of range.");
		}

		public T[] ToArray()
		{
			_lock.EnterReadLock();
			try
			{
				if (_count == 0)
					return Array.Empty<T>();

				var result = new T[_count];
				for (int i = 0; i < _count; i++)
				{
					result[i] = _dense[i];
				}
				return result;
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.
					_lock?.Dispose();
					_sparse?.Dispose();
					_dense?.Dispose();
				}

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.
				disposedValue = true;
			}
		}

		// // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
		// ~ConcurrentSparseSetM()
		// {
		//     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
		//     Dispose(disposing: false);
		// }

		public void Dispose()
		{
			// 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}



	public class SparseSetM<T> : IDisposable where T : struct, IEquatable<T>
	{
		private readonly PooledDictionary<T, int> _sparse;
		private readonly PooledList<T> _dense;
		private int _count;
		private bool disposedValue;

		public SparseSetM(int capacity)
		{
			_sparse = new PooledDictionary<T, int>(capacity);
			_dense = new PooledList<T>(capacity);
			_count = 0;
		}

		public SparseSetM()
		{
			_sparse = new PooledDictionary<T, int>();
			_dense = new PooledList<T>();
			_count = 0;
		}

		public void Clear()
		{
			_sparse.Clear();
			_dense.Clear();
			_count = 0;
		}

		public bool TryAdd(T value)
		{
			if (_sparse.ContainsKey(value))
				return false;

			_sparse[value] = _count;
			_dense.Add(value);
			_count++;
			return true;
		}

		public bool TryRemove(T value)
		{

			if (!_sparse.TryGetValue(value, out int index))
				return false;

			_count--;
			if (index < _count)
			{
				T lastValue = _dense[_count];

				_dense[index] = lastValue;
				_sparse[lastValue] = index;
			}

			_dense.RemoveAt(_count);
			_sparse.Remove(value);
			return true;

		}

		public bool Contains(T value)
		{
			return _sparse.ContainsKey(value);
		}
		public int Count
		{
			get
			{
				return _count;
			}
		}

		public T Get(int index)
		{
			if (index >= 0 && index < _count)
			{
				return _dense[index];
			}
			throw new KeyNotFoundException($"SparseSet에 없음 found.");

		}

		public ReadOnlySpan<T> AsSpan()
		{
			return _dense.Span;
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.
					_sparse?.Dispose();
					_dense?.Dispose();
				}

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.
				disposedValue = true;
			}
		}

		// // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
		// ~SparseSetM()
		// {
		//     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
		//     Dispose(disposing: false);
		// }

		public void Dispose()
		{
			// 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}


	public class ConcurrentSparseSetGetM<KEY, T> : IDisposable
	{
		private readonly PooledDictionary<KEY, int> _sparse;
		private readonly PooledList<T> _dense;
		private readonly PooledList<KEY> _keys;
		private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
		private int _count;
		private bool disposedValue;

		public ConcurrentSparseSetGetM(int capacity)
		{
			_sparse = new PooledDictionary<KEY, int>(capacity);
			_dense = new PooledList<T>(capacity);
			_keys = new PooledList<KEY> (capacity);
			_count = 0;
		}

		public ConcurrentSparseSetGetM()
		{
			_sparse = new PooledDictionary<KEY, int>();
			_dense = new PooledList<T>();
			_keys = new PooledList<KEY>();
			_count = 0;
		}

		public void Clear()
		{
			_lock.EnterWriteLock(); // ReadWrite 불가 Lock
			try
			{
				_sparse.Clear();
				_dense.Clear();
				_keys.Clear();
				_count = 0;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public KEY[] GetKeys()
		{ 
			_lock.EnterReadLock();
			try
			{
				if(_count == 0)
					return Array.Empty<KEY>();

				return [.. _keys];
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}


		public bool TryAdd(KEY key, T value)
		{
			_lock.EnterWriteLock();
			try
			{
				if (_sparse.ContainsKey(key))
					return false;

				_sparse[key] = _count;
				_dense.Add(value);
				_keys.Add(key);
				_count++;
				return true;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public bool TryRemove(KEY key)//, out T removeObj)
		{
			_lock.EnterWriteLock();
			try
			{
				if (!_sparse.TryGetValue(key, out int index))
				{
					//removeObj = default(T);
					return false;
				}

				_count--;
				if (index < _count)
				{
					T lastValue = _dense[_count];
					KEY lastOid = _keys[_count];

					_dense[index] = lastValue;
					_keys[index] = lastOid;

					_sparse[lastOid] = index;
				}

				//removeObj = _dense[_count];

				_dense.RemoveAt(_count);
				_keys.RemoveAt(_count);
				_sparse.Remove(key);
				return true;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public bool Contains(KEY key)
		{
			_lock.EnterReadLock();
			try
			{
				return _sparse.ContainsKey(key);
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		public int Count
		{
			get
			{
				_lock.EnterReadLock();
				try
				{
					return _count;
				}
				finally
				{
					_lock.ExitReadLock();
				}
			}
		}


		public T Get(KEY key)
		{
			_lock.EnterReadLock();
			try
			{
				if (_sparse.TryGetValue(key, out int index) && index < _count)
				{
					return _dense[index];
				}
			}
			finally
			{
				_lock.ExitReadLock();
			}
			Debug.WriteLine($"ConcurrentSparseSetGetM에 OID {key} not found.");
			return default(T); // 또는 예외를 던질 수도 있음
		}

		public T[] ToArray()
		{
			_lock.EnterReadLock();
			try
			{
				if(_count == 0)
					return Array.Empty<T>();

				var result = new T[_count];
				for (int i = 0; i < _count; i++)
				{
					result[i] = _dense[i];
				}
				return result;
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.
					_lock?.Dispose();
					_keys?.Dispose();
					_sparse?.Dispose();
					_dense?.Dispose();
				}

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.
				disposedValue = true;
			}
		}

		// // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
		// ~ConcurrentSparseSetGetM()
		// {
		//     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
		//     Dispose(disposing: false);
		// }

		public void Dispose()
		{
			// 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}

		//public (long oid, T value)[] ToArray()
		//{
		//    _lock.EnterReadLock();
		//    try
		//    {
		//        if (_count == 0)
		//            return Array.Empty<(long oid, T value)>();

		//        var result = new (long oid, T value)[_count];
		//        for (int i = 0; i < _count; i++)
		//        {
		//            result[i] = (_oids[i], _dense[i]);
		//        }
		//        return result;
		//    }
		//    finally
		//    {
		//        _lock.ExitReadLock();
		//    }
		//}

	}


	public class SparseSetGetM<KEY, T> : IDisposable
	{
		private readonly PooledDictionary<KEY, int> _sparse;
		private readonly PooledList<T> _dense;
		private readonly PooledList<KEY> _oids;
		private int _count;
		private bool disposedValue;

		public SparseSetGetM(int capacity)
		{
			_sparse = new PooledDictionary<KEY, int>(capacity);
			_dense = new PooledList<T>(capacity);
			_oids = new PooledList<KEY>(capacity);
			_count = 0;
		}

		public SparseSetGetM()
		{
			_sparse = new PooledDictionary<KEY, int>();
			_dense = new PooledList<T>();
			_oids = new PooledList<KEY>();
			_count = 0;
		}

		public void Clear()
		{
			_sparse.Clear();
			_dense.Clear();
			_oids.Clear();
			_count = 0;
		}
		public bool TryAdd(KEY oid, T value)
		{
			if (_sparse.ContainsKey(oid))
				return false;

			_sparse[oid] = _count;
			_dense.Add(value);
			_oids.Add(oid);
			_count++;
			return true;
		}

		public bool TryRemove(KEY oid)
		{
			if (!_sparse.TryGetValue(oid, out int index))
				return false;

			_count--;
			if (index < _count)
			{
				T lastValue = _dense[_count];
				KEY lastOid = _oids[_count];

				_dense[index] = lastValue;
				_oids[index] = lastOid;

				_sparse[lastOid] = index;
			}

			_dense.RemoveAt(_count);
			_oids.RemoveAt(_count);
			_sparse.Remove(oid);
			return true;

		}

		public bool ContainsKey(KEY oid)
		{
			return _sparse.ContainsKey(oid);
		}
		public int Count => _count;
		

		public T Get(KEY oid)
		{

			if (_sparse.TryGetValue(oid, out int index) && index < _count)
			{
				return _dense[index];
			}

			throw new KeyNotFoundException($"SparseSetGetM에 OID {oid} not found.");

		}

		public T[] ToArray()
		{
			if(_count == 0)
				return Array.Empty<T>();

			var result = new T[_count];
			for (int i = 0; i < _count; i++)
			{
				result[i] = _dense[i];
			}
			return result;

		}

		public ReadOnlySpan<T> AsSpan()
		{
			return _dense.Span;
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.					
					_sparse?.Dispose();
					_dense?.Dispose();
				}

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.
				disposedValue = true;
			}
		}

		// // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
		// ~SparseSetGetM()
		// {
		//     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
		//     Dispose(disposing: false);
		// }

		public void Dispose()
		{
			// 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}
}
