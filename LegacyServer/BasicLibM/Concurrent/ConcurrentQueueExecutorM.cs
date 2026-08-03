using Collections.Pooled;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 업데이트 후에 처리하는 처리기 클래스
	/// </summary>
	// 
	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

	public class ConcurrentQueueExecutorM<T>
	{
		T target;
		int maxProcessOneTime;
		int curProcessTimes;
		ConcurrentQueue<IExecutableAsyncM> _afterUpdateProcessor = new ConcurrentQueue<IExecutableAsyncM>();
		ConcurrentQueue<Action<T>> _afterUpdateAction = new ConcurrentQueue<Action<T>>();

		public void Clear()
		{ 
			_afterUpdateProcessor.Clear();
			_afterUpdateAction.Clear();
			curProcessTimes = 0;
		}

		public ConcurrentQueueExecutorM(T target, int maxProcessOneTime)
		{
			this.target = target;
			this.maxProcessOneTime = maxProcessOneTime;
		}

		public void Add(IExecutableAsyncM exeObj)
		{
			_afterUpdateProcessor.Enqueue(exeObj);
		}

		public void Add(Action<T> action)
		{
			_afterUpdateAction.Enqueue(action);
		}


		public async Task Execute()
		{
			while (_afterUpdateProcessor.TryDequeue(out IExecutableAsyncM process)) // Exit처리 
			{
				await process.Execute().ConfigureAwait(false);
				curProcessTimes++;
				if (curProcessTimes >= maxProcessOneTime)
					break;


			}

			while (curProcessTimes < maxProcessOneTime && _afterUpdateAction.TryDequeue(out Action<T> action)) // 앞에꺼 부터 연산 하니까 조심
			{
				action(target);
				curProcessTimes++;
			}

			curProcessTimes = 0;
		}
	}


	public class QueueExecutorM<T>
	{
		T target;
		int maxProcessOneTime;
		int curProcessTimes;
		PooledQueue<IExecutableAsyncM> _afterUpdateProcessor = new PooledQueue<IExecutableAsyncM>();
		PooledQueue<Action<T>> _afterUpdateAction = new PooledQueue<Action<T>>();

		public void Clear()
		{ 
			_afterUpdateProcessor.Clear();
			_afterUpdateAction.Clear();
			curProcessTimes = 0;
		}

		public QueueExecutorM(T target, int maxProcessOneTime)
		{
			this.target = target;
			this.maxProcessOneTime = maxProcessOneTime;
		}

		public void Add(IExecutableAsyncM exeObj)
		{
			_afterUpdateProcessor.Enqueue(exeObj);
		}

		public void Add(Action<T> action)
		{
			_afterUpdateAction.Enqueue(action);
		}


		public async Task Execute()
		{
			while (_afterUpdateProcessor.TryDequeue(out IExecutableAsyncM process)) // Exit처리 
			{
				await process.Execute().ConfigureAwait(false);
				curProcessTimes++;
				if (curProcessTimes >= maxProcessOneTime)
					break;


			}

			while (curProcessTimes < maxProcessOneTime && _afterUpdateAction.TryDequeue(out Action<T> action)) // 앞에꺼 부터 연산 하니까 조심
			{
				action(target);
				curProcessTimes++;
			}

			curProcessTimes = 0;
		}
	}

}
