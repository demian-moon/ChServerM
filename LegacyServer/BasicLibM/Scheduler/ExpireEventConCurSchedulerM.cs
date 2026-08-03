using Collections.Pooled;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{


	/// <summary>
	/// Concurrent하지 않으니 주의 할 것 - HashM자체가 동시성 지원하지 않음(필요하면 변경해야 함)
	/// 특정 시간에 Dictionary를 삭제하는 타임 Job
	/// </summary>
	public class ExpireJobForDicRemoveM : AbExpireEventM
	{
		PooledDictionary<string, string> _targetHash;
		public string _hashKey;
		public ExpireJobForDicRemoveM(PooledDictionary<string, string> targetHash, string hashKey, DateTime triggerTime, Action<ITimeEventM> callback) : base(triggerTime, callback)
		{
			_targetHash = targetHash;
			_hashKey = hashKey;
		}

		public override void Execute()
		{
			_targetHash.Remove(_hashKey);
		}
	}


	/// <summary>
	/// 타임 이벤트 만들때 베이스로 쓰이는 클래스
	/// </summary>
	public abstract class AbExpireEventM : ITimeEventM
	{
		public DateTime TriggerTime { get; }
		public Action<ITimeEventM> CallBackProcess { get; set; }

		public int bCanceled { get => _bCanceled; }

		int _bCanceled;
		public void Cancel()
		{
			Interlocked.Increment(ref _bCanceled);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="triggerTime"></param>
		/// <param name="callBackProcess">실행후 통보 받는 delegate</param>
		public AbExpireEventM(DateTime triggerTime, Action<ITimeEventM> callBackProcess = null)
		{
			TriggerTime = triggerTime;
			CallBackProcess = callBackProcess;
		}

		abstract public void Execute();
	}

	/// <summary>
	/// 타임이벤트 인터페이스
	/// </summary>
	public interface ITimeEventM : IExecutableM
	{
		int bCanceled { get; }
		Action<ITimeEventM> CallBackProcess { get; set; }    // 처리 후 피드백 펑션
		DateTime TriggerTime { get; }
	}

	/// <summary>
	/// 처리 시간이 있는 모든 이벤트 스케쥴러 (Concurrent)
	/// !!주의 : 추가되는 ITimeEventM.CallBackProcess로 전달되는 CallbackProcess는 동시성 발생하니 반드시 주의 할 것
	/// </summary>
	/// <typeparam name="T"> </typeparam>
	public class ExpireEventConCurSchedulerM<T> where T : AbExpireEventM
	{
		private int _taskDelayMs;   // 태스크 delay 
		private readonly ConcurrentQueue<T> _incomingQueue = new();           // 신규 이벤트용 Queue

		private readonly SortedList<DateTime, PooledList<T>> _eventQueue = new();   // TriggerTime으로 정렬된 이벤트 큐		
		private readonly CancellationTokenSource _cancellationTokenSource = new();
		private Task _schedulerTask;

		PooledList<DateTime> _keysToRemove = new();

		/// <summary>
		/// 타임 이벤트 스케쥴러 (Concurrent) 
		/// </summary>
		/// <param name="taskDelayMs">cpu</param>
		public ExpireEventConCurSchedulerM()
		{

		}

		public void StartSchedulerAsync(int taskDelayMs)
		{
			this._taskDelayMs = taskDelayMs;
			_schedulerTask = new Task(ProcessTimeEventsAsync, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning);
			_schedulerTask.Start();
		}

		/// <summary>
		/// 신규 이벤트 추가
		/// </summary>
		/// <param name="timeEvent"></param>
		public void AddTimeEvent(T timeEvent)
		{
			_incomingQueue.Enqueue(timeEvent);
		}


		private async void ProcessTimeEventsAsync()
		{
			while (!_cancellationTokenSource.Token.IsCancellationRequested)
			{
				ProcessSchedules();
				await Task.Delay(_taskDelayMs).ConfigureAwait(false); // 적절한 지연으로 CPU 사용률 조정
			}
			_cancellationTokenSource.Dispose();
		}


		public void ProcessSchedules()
		{
			ProcessIncomingQueue();
			DateTime now = DateTime.UtcNow;

			foreach (var kvp in _eventQueue)
			{
				if (kvp.Key > now) break;

				foreach (var timeEvent in kvp.Value)
				{
					// 캔슬된 이벤트 검사
					if (timeEvent.bCanceled == 0)
					{
						timeEvent.Execute();
						if (timeEvent.CallBackProcess != null)
							timeEvent.CallBackProcess(timeEvent);    // 처리 된 것을 통보해줌 (동시성 발생하니 반드시 주의 할 것)
					}
				}
				_keysToRemove.Add(kvp.Key);
			}

			// 처리 완료한 키 제거
			foreach (var key in _keysToRemove)
			{
				_eventQueue.Remove(key);
			}

			_keysToRemove.Clear(); // 처리한 이벤트 지우기      
		}

		// 신규 이벤트들을 _eventQueue로 이동
		private void ProcessIncomingQueue()
		{
			while (_incomingQueue.TryDequeue(out var timeEvent))
			{
				if (!_eventQueue.TryGetValue(timeEvent.TriggerTime, out var eventList))
				{
					eventList = new PooledList<T>();
					_eventQueue.Add(timeEvent.TriggerTime, eventList);
				}
				eventList.Add(timeEvent);
			}
		}

		public async ValueTask StopScheduler()
		{
			_cancellationTokenSource.Cancel();
			if (_schedulerTask != null)
			{
				await _schedulerTask.ConfigureAwait(false);
			}

			_keysToRemove.Clear();
		}
	}


	/// <summary>
	/// 처리 시간이 있는 모든 이벤트 스케쥴러 - 
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class ExpireEventSchedulerM<T> where T : ITimeEventM
	{
		private readonly SortedList<DateTime, PooledList<T>> _eventQueue = new();   // TriggerTime으로 정렬된 이벤트 큐        
		PooledList<DateTime> _keysToRemove = new();


		/// <summary>
		/// 신규 이벤트 추가
		/// </summary>
		/// <param name="timeEvent"></param>
		public void AddTimeEvent(T timeEvent)
		{
			if (!_eventQueue.TryGetValue(timeEvent.TriggerTime, out var eventList))
			{
				eventList = new PooledList<T>();
				_eventQueue.Add(timeEvent.TriggerTime, eventList);
			}
			eventList.Add(timeEvent);
		}


		public void ProcessSchedules()
		{
			DateTime now = DateTime.UtcNow;
			

			if (_eventQueue.Count <= 0)
				return;

			foreach (var kvp in _eventQueue)
			{
				if (kvp.Key > now) break;

				foreach (var timeEvent in kvp.Value)
				{
					// 캔슬된 이벤트 검사
					if (timeEvent.bCanceled == 0)
					{
						timeEvent.Execute();
						if (timeEvent.CallBackProcess != null)
							timeEvent.CallBackProcess(timeEvent);    // 처리 된 것을 통보해줌
					}

				}
				_keysToRemove.Add(kvp.Key);
			}

			//처리 완료한 키 제거
			foreach (var key in _keysToRemove)
			{
				_eventQueue.Remove(key);
			}

			_keysToRemove.Clear(); // 처리한 이벤트 지우기                

		}
	}
}
