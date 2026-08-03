using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	public class ScriptDelayEventM : AbTimeEventBaseM
	{
		ScriptDelaysM _scriptDelays;
		public ScriptDelayEventM(ScriptDelaysM scriptDelays, string idJob, long expireTimestamp) : base(idJob, expireTimestamp)
		{
			_scriptDelays = scriptDelays;
		}

		public override IHasTimeEventsM Owner => _scriptDelays;

		protected override void OnTerminate(string idJob)
		{
			_scriptDelays.EnqueSetAndResetEvent(idJob);			
		}

	}

	// 스크립트의 Delay를 
	public class ScriptDelaysM : IHasTimeEventsM
	{
		int idNum;
		
		ConcurrentDictionary<string, AsyncManualResetEventM> _dicResetEvent = new();

		// SetAndReset 해야될 resetEvent들
		static public ConcurrentQueue<AsyncManualResetEventM> queSetAndResetEvent = new();

		ConcurrentDictionary<string, AbTimeEventBaseM> _timeEvents => new();
		public ConcurrentDictionary<string, AbTimeEventBaseM> TimeEvents => _timeEvents;

		TimeEventSchedulerM _timeEventScheduler;
		public ScriptDelaysM(TimeEventSchedulerM timeEventScheduler)
		{
			_timeEventScheduler = timeEventScheduler;
		}

		public async ValueTask Sleep(int delayMs)
		{
			var resetEvent = new AsyncManualResetEventM();

			var idResetEvent = new StringBuilder(idNum++).ToString();
			_dicResetEvent.TryAdd(idResetEvent, resetEvent); // 사전에 추가

			_timeEventScheduler.AddJob(new ScriptDelayEventM(this, idResetEvent, _timeEventScheduler.CreateExpirationTimestamp(delayMs) ) );
			// ScriptDelayEventM 지연시간 후 만료되는 이벤트를 보낸다.

			await resetEvent.WaitAsync();
		}
		
		public void EnqueSetAndResetEvent(string idResetEvent)
		{
			if(_dicResetEvent.TryGetValue(idResetEvent, out var resetEvent))
			{
				queSetAndResetEvent.Enqueue(resetEvent);
			}
		}

	}
		
	/// <summary>
	/// TaskCompletionSource를 이용해서 멈추고 서는 신호 시그널 클래스
	/// </summary>
	public sealed class AsyncManualResetEventM
	{
		private static readonly ValueTask s_completedTask = new ValueTask();
		private volatile TaskCompletionSource<byte> _tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);

		public AsyncManualResetEventM(bool initialState = false)
		{
			if (initialState)
				_tcs.TrySetResult(0);
		}
				

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ValueTask WaitAsync(CancellationToken cancellationToken = default)
		{
			var tcs = _tcs; // 다른쓰레드가 중간에 Reset으로 변경할 수 있기 때문에 캡쳐

			if (tcs.Task.IsCompleted)
				return s_completedTask;

			if (cancellationToken.IsCancellationRequested)
				return new ValueTask(Task.FromCanceled(cancellationToken));

			if (cancellationToken.CanBeCanceled)
				return WaitAsyncCore(tcs, cancellationToken);

			return new ValueTask(tcs.Task);
		}

		// 수정된 취소 처리 로직
		private static async ValueTask WaitAsyncCore(TaskCompletionSource<byte> tcs, CancellationToken cancellationToken)
		{
			var task = tcs.Task;

			// 취소 등록을 먼저 수행
			using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken), false);

			try
			{
				await task.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Set()
		{
			_tcs.TrySetResult(0);
		}

		public void Reset()
		{
			var currentTcs = _tcs;
			if (!currentTcs.Task.IsCompleted)
				return;

			var newTcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
			Interlocked.CompareExchange(ref _tcs, newTcs, currentTcs);
		}

		// 원자적 Set-Reset 구현
		public void SetAndReset()
		{
			var currentTcs = _tcs;
			currentTcs.TrySetResult(0);

			var newTcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
			Interlocked.CompareExchange(ref _tcs, newTcs, currentTcs);
		}
	}


}
