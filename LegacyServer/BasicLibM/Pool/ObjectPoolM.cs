using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	/// <summary>
	/// Simple object pool to reduce GC pressure
	/// </summary>
	public sealed class ObjectPoolM<T> where T : class, new()
	{
		private readonly ConcurrentQueue<T> _objects = new ConcurrentQueue<T>();
		private readonly Func<T> _generator;

		public ObjectPoolM(Func<T> generator = null)
		{
			_generator = generator ?? (() => new T());
		}

		public T Get() => _objects.TryDequeue(out var item) ? item : _generator();
		public void Return(T item) => _objects.Enqueue(item);

		public void Clear()
		{
			_objects.Clear();
		}
	}

}
