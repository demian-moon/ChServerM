using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	/// <summary>
	/// 동시 추가가능한 Task 순차 실행 큐
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class ConcurSeqTaskExecM<T>
	{
		private readonly ConcurrentQueue<T> _queue = new();
		private readonly Func<T, Task> _processor;
		private int _isRunning = 0;

		public ConcurSeqTaskExecM(Func<T, Task> processor)
		{
			_processor = processor;
		}

		public void Enqueue(T item)
		{
			_queue.Enqueue(item);

			// 아직 실행 중이 아니면 실행 시도
			if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
			{
				_ = Task.Run(ProcessQueueAsync);
			}
		}

		private async Task ProcessQueueAsync()
		{
			while (_queue.TryDequeue(out var item))
			{
				try
				{
					await _processor(item).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[Error] {ex}");
				}
			}

			// 끝났다고 표시
			Interlocked.Exchange(ref _isRunning, 0);

			// 놓친 요청이 있으면 다시 실행
			if (!_queue.IsEmpty &&
				Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
			{
				_ = Task.Run(ProcessQueueAsync);
			}
		}
	}


	/////////////////////////////////////////////////////////////////////////////////////////////////////
	// Unity3d에서 GameObject에 붙여 넣어야 하는 코드
	//using System.Threading;
	//using UnityEngine;
	//using YourDllNamespace;  // DLL 네임스페이스

	///// <summary>
	///// Unity가 애셈블리를 로드한 직후(씬 로드 전)에 실행되어
	///// ConcurSeqTaskContextExecMConfig.UiContext를 세팅해 줍니다.
	///// </summary>
	//public static class BootstrapConcurSeq
	//{
	//	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	//	[Preserve]
	//	static void InitUiContext()
	//	{
	//		// 이 시점의 Current 가 UnitySynchronizationContext 여야 합니다.
	//		var unityCtx = SynchronizationContext.Current;
	//		ConcurSeqTaskContextExecMConfig.SetUiSynchronizationContext(unityCtx);

	//		Debug.Log($"[BootstrapConcurSeq] UiContext set to {unityCtx?.GetType().Name}");
	//	}
	//}



	/// <summary>
	/// ConcurSeqTaskContextExecM이 사용할 UI 스레드용 SynchronizationContext를
	/// 외부에서 설정해 주기 위한 전역 설정 클래스
	/// </summary>
	public static class ConcurSeqTaskContextExecMConfig
	{
		internal static SynchronizationContext UiContext { get; private set; }
			= SynchronizationContext.Current;  // 유니티에서는 null리턴

		/// <summary>
		/// Unity 또는 호스트 애플리케이션에서 메인 스레드의 SynchronizationContext를
		/// 이 메서드로 설정해 주십시오.
		/// </summary>
		/// <param name="context">메인 스레드의 SynchronizationContext</param>
		public static void SetUiSynchronizationContext(SynchronizationContext context)
		{
			UiContext = context ?? new SynchronizationContext();
		}
	}


	// -----------------------------
	// ConcurSeqTaskContextExecM.cs
	// -----------------------------


	public interface IUIThreadCheck
	{
		bool IsUIThread();
	}

	public class ConcurSeqTaskContextExecM<T> where T : IUIThreadCheck
	{	

		readonly Channel<T> _channel = Channel.CreateBounded<T>(
		new BoundedChannelOptions(1000) // 게임 서버 부하에 맞게 조정
		{
			FullMode = BoundedChannelFullMode.Wait, // 백프레셔 적용
			SingleReader = true, // 단일 리더 최적화 적용
			SingleWriter = false, // 여러 클라이언트에서 동시에 패킷이 들어올 수 있음
			AllowSynchronousContinuations = false // 비동기 처리 유지
		});
		readonly Func<T, Task> _processor;
		readonly SynchronizationContext _uiContext = ConcurSeqTaskContextExecMConfig.UiContext;
		readonly SendOrPostCallback _uiPostCallback;
		int _isRunning;

		public ConcurSeqTaskContextExecM(Func<T, Task> processor)
		{
			_processor = processor ?? throw new ArgumentNullException(nameof(processor));
			_uiPostCallback = UiPostCallback;  // 람다 할당 1회
		}

		public void Post(T item)
		{
			_channel.Writer.TryWrite(item);
			if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
				_ = Task.Run(ProcessQueueAsync);
		}

		async Task ProcessQueueAsync()
		{
			// 채널이 완전히 닫힐 때까지(Complete 될 때까지) 무한 루프
			while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				// 한 번에 가능한 만큼 비우기
				while (_channel.Reader.TryRead(out var item))
				{
					try
					{
						if (item.IsUIThread())
						{
							// RunContinuationsAsynchronously 로 컨티뉴이션 최소 블로킹[1]
							var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
							_uiContext.Post(_uiPostCallback, (item, tcs));
							await tcs.Task.ConfigureAwait(false);
						}
						else
						{
							await _processor(item).ConfigureAwait(false);
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[Error] {ex}");
					}
				}
			}

			Interlocked.Exchange(ref _isRunning, 0);
			if (!_channel.Reader.Completion.IsCompleted &&
				Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
			{
				_ = Task.Run(ProcessQueueAsync);
			}
		}

		void UiPostCallback(object state)
		{
			var (item, tcs) = ((T, TaskCompletionSource<bool>))state;
			// ConfigureAwait(false) + ContinueWith 로 Post 쪽 오버헤드 최소화
			_processor(item).ContinueWith(t =>
			{
				if (t.IsFaulted) tcs.TrySetException(t.Exception.InnerExceptions);
				else if (t.IsCanceled) tcs.TrySetCanceled();
				else tcs.TrySetResult(true);
			}, TaskScheduler.Default);
		}
	}





	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class ConcurSeqTaskContextExecLongRunM<T> where T : IUIThreadCheck
	{
		//	// disposed 플래그
		bool _disposed = false;

		//	UI 상태 객체 풀 (박싱 회피 목적)
		readonly ConcurrentBag<UIWorkItem> _uiWorkPool = new();


		readonly Channel<T> _channel = Channel.CreateUnbounded<T>(
						new UnboundedChannelOptions
						{
							SingleReader = true,
							SingleWriter = false,
							AllowSynchronousContinuations = false
						});

		readonly Func<T, Task> _processor;
		readonly SynchronizationContext _uiContext = ConcurSeqTaskContextExecMConfig.UiContext;
		readonly SendOrPostCallback _uiPostCallback;
		int _isRunning;

		public ConcurSeqTaskContextExecLongRunM(Func<T, Task> processor)
		{
			_processor = processor ?? throw new ArgumentNullException(nameof(processor));
			_uiPostCallback = UiPostCallback;      // 람다 할당 1회

			// 아직 실행 중인 워커가 없으면 전용 스레드 한 번만 기동
			if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
			{
				_ = Task.Factory.StartNew(
						async () => await ProcessQueueAsync().ConfigureAwait(false),
						CancellationToken.None,
						TaskCreationOptions.LongRunning,
						TaskScheduler.Default)
					.Unwrap();                     // 중첩 Task 제거
			}
		}

		public void Post(T item)
		{
			_channel.Writer.TryWrite(item);

			
		}

		async Task ProcessQueueAsync()
		{
			try
			{
				// 채널이 완전히 닫힐 때까지(Complete 될 때까지) 무한 루프
				while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
				{
					// 한 번에 가능한 만큼 비우기
					while (_channel.Reader.TryRead(out var item))
					{
						try
						{
							if (item.IsUIThread())
							{
								//UI 전용 경로: 풀에서 상태 객체 가져와서 UI 컨텍스트에 Post
								var work = RentUIWorkItem();
								work.Item = item;
								work.Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

								_uiContext.Post(_uiPostCallback, work);

								// UI 콜이 완료될 때까지 대기
								await work.Tcs.Task.ConfigureAwait(false);

								// 사용 후 해제
								ReturnUIWorkItem(work);
							}
							else
							{
								await _processor(item).ConfigureAwait(false);
							}
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[Error] {ex}");
						}
					}
				}
			}
			finally
			{
				// 채널이 아직 살아 있으면(Complete 전) 워커를 다시 띄움
				Interlocked.Exchange(ref _isRunning, 0);
				if (!_channel.Reader.Completion.IsCompleted &&
					Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
				{
					_ = Task.Factory.StartNew(
							async () => await ProcessQueueAsync().ConfigureAwait(false),
							CancellationToken.None,
							TaskCreationOptions.LongRunning,
							TaskScheduler.Default)
						.Unwrap();
				}
			}
		}

		// UI Thread 실행 콜백(원본 유지)
		void UiPostCallback(object state)
		{
			var (item, tcs) = ((T, TaskCompletionSource<bool>))state;
			_processor(item).ContinueWith(t =>
			{
				if (t.IsFaulted) tcs.TrySetException(t.Exception.InnerExceptions);
				else if (t.IsCanceled) tcs.TrySetCanceled();
				else tcs.TrySetResult(true);
			}, TaskScheduler.Default);
		}

		// ============================
		// UIWorkItem 풀 관리
		// - 박싱 제거 목적: SynchronizationContext.Post에 전달되는 객체는 참조 타입이어야 함.
		// - UIWorkItem 풀은 UI 작업 빈도가 매우 높을 때 GC 할당을 줄이도록 도와줌.
		// ============================
		UIWorkItem RentUIWorkItem()
		{
			if (_uiWorkPool.TryTake(out var w))
			{
				// 재사용: 초기화는 호출자(사용자) 쪽에서 Item/Tcs를 할당
				return w;
			}
			return new UIWorkItem();
		}

		void ReturnUIWorkItem(UIWorkItem w)
		{
			// 상태 클리어 (참조 유출 방지)
			w.Reset();
			_uiWorkPool.Add(w);
		}

		sealed class UIWorkItem
		{
			public T Item;
			public TaskCompletionSource<bool> Tcs;

			public void Reset()
			{
				Item = default!;
				Tcs = null!;
			}
		}

		// ============================
		// Dispose / Cancel
		// ============================
		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			// 더 이상 쓰기 허용 안함
			_channel.Writer.TryComplete();		

			// 작업 취소 신호
			//_cts.Cancel();
			//_cts.Dispose();
		}



	}



	//public sealed class ConcurSeqTaskContextExecLongRunM<T> : IDisposable where T : IUIThreadCheck
	//{
	//	// 메인 처리용 채널 (싱글 리더) - bounded 권장
	//	readonly Channel<T> _mainChannel;

	//	// Ordered queue용 채널: (seq, item) 형태로 저장. bounded로 메모리 보호.
	//	readonly Channel<(long seq, T item)> _orderedChannel;

	//	// 사용자 처리기
	//	readonly Func<T, Task> _processor;

	//	// UI 처리용 컨텍스트
	//	readonly SynchronizationContext _uiContext;
	//	readonly SendOrPostCallback _uiPostCallback;

	//	// 취소/종료용
	//	readonly CancellationTokenSource _cts = new();

	//	// Ordered 시퀀스 번호(호출 시점에 할당)
	//	long _seq = 0;

	//	// 워커 상태 플래그
	//	int _isWorkerRunning = 0;
	//	int _isSerializerRunning = 0;

	//	// UI 상태 객체 풀 (박싱 회피 목적)
	//	readonly ConcurrentBag<UIWorkItem> _uiWorkPool = new();

	//	// disposed 플래그
	//	bool _disposed = false;

	//	/// <summary>
	//	/// 생성자
	//	/// - processor: 실제 항목 처리 함수
	//	/// - capacityMain: 메인 채널 (처리자) 버퍼 크기
	//	/// - capacityOrdered: ordered 채널(시퀀스 보장 큐) 버퍼 크기
	//	/// - uiContext: UI 동작이 필요한 경우 전달할 SynchronizationContext (null이면 current 사용)
	//	/// </summary>
	//	public ConcurSeqTaskContextExecLongRunM(
	//	Func<T, Task> processor,
	//	int capacityMain = 10000,
	//	int capacityOrdered = 20000,
	//	SynchronizationContext? uiContext = null)
	//	{
	//		_processor = processor ?? throw new ArgumentNullException(nameof(processor));
	//		_uiContext = uiContext ?? SynchronizationContext.Current ?? throw new ArgumentNullException(nameof(uiContext));
	//		_uiPostCallback = UiPostCallback;

	//		var mainOpts = new BoundedChannelOptions(capacityMain)
	//		{
	//			FullMode = BoundedChannelFullMode.Wait, // 가득 차면 WriteAsync에서 대기
	//			SingleReader = true,
	//			SingleWriter = false,
	//			AllowSynchronousContinuations = false
	//		};
	//		_mainChannel = Channel.CreateBounded<T>(mainOpts);

	//		var orderedOpts = new BoundedChannelOptions(capacityOrdered)
	//		{
	//			FullMode = BoundedChannelFullMode.Wait, // 필요시 PostOrderedAsync로 대기 가능
	//			SingleReader = true,
	//			SingleWriter = false,
	//			AllowSynchronousContinuations = false
	//		};
	//		_orderedChannel = Channel.CreateBounded<(long, T)>(orderedOpts);
	//	}

	//	// ============================
	//	// 공개 API
	//	// ============================

	//	/// <summary>
	//	/// 호출 순서 보장: 즉시(논블로킹) 시퀀스번호를 할당하고 ordered 채널에 넣는다.
	//	/// 성공하면 true, ordered 채널이 가득하면 false 반환.
	//	/// (메모리 무제한 증가 방지 목적의 bounded 채널 + TryWrite 사용)
	//	/// </summary>
	//	public bool PostOrdered(T item)
	//	{
	//		ThrowIfDisposed();
	//		// 호출 시점에 시퀀스 할당 -> 호출 순서(시작 순서) 보장
	//		var mySeq = Interlocked.Increment(ref _seq);
	//		StartSerializerIfNeeded();
	//		StartWorkerIfNeeded();

	//		// 즉시 시도. 실패 시 false 반환 (사용자는 재시도/드롭 정책 결정)
	//		return _orderedChannel.Writer.TryWrite((mySeq, item));
	//	}

	//	/// <summary>
	//	/// 호출 순서 보장: ordered 채널이 가득하면 대기하여 넣음(비동기).
	//	/// </summary>
	//	public ValueTask PostOrderedAsync(T item, CancellationToken cancellationToken = default)
	//	{
	//		ThrowIfDisposed();
	//		var mySeq = Interlocked.Increment(ref _seq);
	//		StartSerializerIfNeeded();
	//		StartWorkerIfNeeded();
	//		return _orderedChannel.Writer.WriteAsync((mySeq, item), cancellationToken);
	//	}

	//	/// <summary>
	//	/// 메인 채널로 직접 비동기 작성 (대기 가능)
	//	/// </summary>
	//	public ValueTask PostAsync(T item, CancellationToken cancellationToken = default)
	//	{
	//		ThrowIfDisposed();
	//		StartWorkerIfNeeded();
	//		return _mainChannel.Writer.WriteAsync(item, cancellationToken);
	//	}

	//	/// <summary>
	//	/// 메인 채널 즉시 쓰기 시도 (성공/실패 반환)
	//	/// </summary>
	//	public bool TryPost(T item)
	//	{
	//		ThrowIfDisposed();
	//		StartWorkerIfNeeded();
	//		return _mainChannel.Writer.TryWrite(item);
	//	}

	//	// ============================
	//	// Serializer: orderedChannel -> mainChannel (순서 보장)
	//	// - 전용 스레드에서 동기적으로 채널에 쓰는 형태로 구현해서 mainChannel에 들어가는 순서가 보장된다.
	//	// - WaitToReadAsync().GetAwaiter().GetResult()로 블록 대기하므로 별도의 Sleep이 필요 없다.
	//	// ============================
	//	void StartSerializerIfNeeded()
	//	{
	//		if (Interlocked.CompareExchange(ref _isSerializerRunning, 1, 0) == 0)
	//		{
	//			// LongRunning 전용 스레드에서 동기적으로 루프가 돌도록 GetAwaiter().GetResult() 사용
	//			Task.Factory.StartNew(
	//			() => SerializerLoop(),
	//			_cts.Token,
	//			TaskCreationOptions.LongRunning,
	//			TaskScheduler.Default);
	//		}
	//	}

	//	void SerializerLoop()
	//	{
	//		try
	//		{
	//			var reader = _orderedChannel.Reader;
	//			// 루프: ordered채널이 완료될 때까지(또는 취소될 때까지) 블록 대기하면서 처리
	//			while (true)
	//			{
	//				// 대기 (채널이 닫히면 false)
	//				bool has = reader.WaitToReadAsync(_cts.Token).GetAwaiter().GetResult();
	//				if (!has) break;

	//				// 가능한 만큼 비우며 각 항목을 메인 채널에 **동기적**으로 넣는다(순서 보장)
	//				while (reader.TryRead(out var pair))
	//				{
	//					// main 채널이 full이면 여기서 블록된다(WriteAsync의 결과를 동기적으로 대기)
	//					_mainChannel.Writer.WriteAsync(pair.item, _cts.Token).GetAwaiter().GetResult();
	//				}
	//			}
	//		}
	//		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
	//		{
	//			// 정상 취소
	//		}
	//		catch (Exception ex)
	//		{
	//			// 운영환경에서는 structured logging(Serilog 등)로 대체
	//			Console.WriteLine($"[Serializer Error] {ex}");
	//		}
	//		finally
	//		{
	//			//Interlocked.Exchange(ref _isSerializerRunning, 0);

	//			//// 레이스 조건 처리: 새 항목이 들어왔으면 재시작
	//			//if (!_orderedChannel.Reader.Completion.IsCompleted &&
	//			//!_orderedChannel.Reader.Completion.IsCompleted && // redundant but safe
	//			//!_orderedChannel.Reader.TryPeek(out _))
	//			//{
	//			//	// nothing to do; TryPeek is not available - we simply attempt restart if channel not completed and not empty
	//			//}

	//			// 안전하게 재시작 로직: (간단한 방법)
	//			if (!_orderedChannel.Reader.Completion.IsCompleted &&
	//			!_orderedChannel.Reader.WaitToReadAsync().IsCompleted &&
	//			Interlocked.CompareExchange(ref _isSerializerRunning, 1, 0) == 0)
	//			{
	//				Task.Factory.StartNew(
	//				() => SerializerLoop(),
	//				_cts.Token,
	//				TaskCreationOptions.LongRunning,
	//				TaskScheduler.Default);
	//			}
	//		}
	//	}

	//	// ============================
	//	// Processor: mainChannel -> _processor(item)
	//	// - 싱글 리더로 순차 처리
	//	// - UI 항목은 SynchronizationContext.Post로 전달하고 TaskCompletionSource로 대기
	//	// ============================
	//	void StartWorkerIfNeeded()
	//	{
	//		if (Interlocked.CompareExchange(ref _isWorkerRunning, 1, 0) == 0)
	//		{
	//			Task.Factory.StartNew(
	//			() => ProcessQueueAsync().GetAwaiter().GetResult(),
	//			_cts.Token,
	//			TaskCreationOptions.LongRunning,
	//			TaskScheduler.Default);
	//		}
	//	}

	//	async Task ProcessQueueAsync()
	//	{
	//		try
	//		{
	//			// ReadAllAsync를 사용하면 채널이 닫힐 때까지 항목을 순차적으로 읽음
	//			await foreach (var item in _mainChannel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
	//			{
	//				try
	//				{
	//					if (item.IsUIThread())
	//					{
	//						// UI 전용 경로: 풀에서 상태 객체 가져와서 UI 컨텍스트에 Post
	//						var work = RentUIWorkItem();
	//						work.Item = item;
	//						work.Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	//						_uiContext.Post(_uiPostCallback, work);

	//						// UI 콜이 완료될 때까지 대기
	//						await work.Tcs.Task.ConfigureAwait(false);

	//						// 사용 후 해제
	//						ReturnUIWorkItem(work);
	//					}
	//					else
	//					{
	//						await _processor(item).ConfigureAwait(false);
	//					}
	//				}
	//				catch (OperationCanceledException) when (_cts.IsCancellationRequested)
	//				{
	//					// 취소 시 루프 나가기
	//					break;
	//				}
	//				catch (Exception ex)
	//				{
	//					// 운영 환경에서는 structured logger로 대체
	//					Console.WriteLine($"[Processor Error] {_processor.Method.Name}: {ex}");
	//				}
	//			}
	//		}
	//		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
	//		{
	//			// 정상 취소
	//		}
	//		catch (Exception ex)
	//		{
	//			Console.WriteLine($"[Worker Error] {ex}");
	//		}
	//		finally
	//		{
	//			Interlocked.Exchange(ref _isWorkerRunning, 0);
	//			// 필요시 재시작 로직을 넣을 수 있음 (응용)
	//			if (!_mainChannel.Reader.Completion.IsCompleted &&
	//			Interlocked.CompareExchange(ref _isWorkerRunning, 1, 0) == 0)
	//			{
	//				Task.Factory.StartNew(
	//				() => ProcessQueueAsync().GetAwaiter().GetResult(),
	//				_cts.Token,
	//				TaskCreationOptions.LongRunning,
	//				TaskScheduler.Default);
	//			}
	//		}
	//	}

	//	// ============================
	//	// UI Post 콜백: 풀링된 UIWorkItem 객체를 받음(박싱 없음)
	//	// - _processor를 UI 스레드에서 호출하고, 완료되면 TaskCompletionSource에 결과 세팅
	//	// - 완료 후 객체는 풀로 반환
	//	// ============================
	//	void UiPostCallback(object state)
	//	{
	//		var work = (UIWorkItem)state;
	//		try
	//		{
	//			var task = _processor(work.Item);

	//			// 만약 task가 이미 완료됨 -> 동기적으로 결과 반영
	//			if (task.IsCompleted)
	//			{
	//				if (task.IsFaulted)
	//				{
	//					if (task.Exception != null)
	//						work.Tcs.TrySetException(task.Exception.InnerExceptions);
	//					else
	//						work.Tcs.TrySetException(new Exception("Task faulted but Exception is null."));
	//				}
	//				else if (task.IsCanceled)
	//				{
	//					work.Tcs.TrySetCanceled();
	//				}
	//				else
	//				{
	//					work.Tcs.TrySetResult(true);
	//				}

	//				// 풀로 반환
	//				// (리턴은 ProcessQueueAsync에서 이미 ReturnUIWorkItem 호출하므로 중복 방지 위해 여기서는 반환하지 않음)
	//			}
	//			else
	//			{
	//				// 비동기 완료 경로: ThreadPool에서 continuation 실행하여 TCS에 결과 세팅
	//				task.ContinueWith(t =>
	//				{
	//					try
	//					{
	//						if (t.IsFaulted)
	//						{
	//							if (t.Exception != null)
	//								work.Tcs.TrySetException(t.Exception.InnerExceptions);
	//							else
	//								work.Tcs.TrySetException(new Exception("Task faulted but Exception is null."));
	//						}
	//						else if (t.IsCanceled)
	//						{
	//							work.Tcs.TrySetCanceled();
	//						}
	//						else
	//						{
	//							work.Tcs.TrySetResult(true);
	//						}
	//					}
	//					finally
	//					{
	//						// 주의: ProcessQueueAsync가 work를 Return하도록 design 했으므로
	//						// 여기서는 ReturnUIWorkItem을 호출하지 않는다.
	//						// 그러나 안전을 위해 pool 반환은 ProcessQueueAsync가 담당.
	//					}
	//				},
	//				CancellationToken.None,
	//				TaskContinuationOptions.DenyChildAttach,
	//				TaskScheduler.Default);
	//			}
	//		}
	//		catch (Exception ex)
	//		{
	//			// 동기적으로 발생한 예외
	//			work.Tcs.TrySetException(ex);
	//		}
	//	}

	//	// ============================
	//	// UIWorkItem 풀 관리
	//	// - 박싱 제거 목적: SynchronizationContext.Post에 전달되는 객체는 참조 타입이어야 함.
	//	// - UIWorkItem 풀은 UI 작업 빈도가 매우 높을 때 GC 할당을 줄이도록 도와줌.
	//	// ============================
	//	UIWorkItem RentUIWorkItem()
	//	{
	//		if (_uiWorkPool.TryTake(out var w))
	//		{
	//			// 재사용: 초기화는 호출자(사용자) 쪽에서 Item/Tcs를 할당
	//			return w;
	//		}
	//		return new UIWorkItem();
	//	}

	//	void ReturnUIWorkItem(UIWorkItem w)
	//	{
	//		// 상태 클리어 (참조 유출 방지)
	//		w.Reset();
	//		_uiWorkPool.Add(w);
	//	}

	//	sealed class UIWorkItem
	//	{
	//		public T Item;
	//		public TaskCompletionSource<bool> Tcs;

	//		public void Reset()
	//		{
	//			Item = default!;
	//			Tcs = null!;
	//		}
	//	}

	//	// ============================
	//	// Dispose / Cancel
	//	// ============================
	//	public void Dispose()
	//	{
	//		if (_disposed) return;
	//		_disposed = true;

	//		// 더 이상 쓰기 허용 안함
	//		_mainChannel.Writer.TryComplete();
	//		_orderedChannel.Writer.TryComplete();

	//		// 작업 취소 신호
	//		_cts.Cancel();
	//		_cts.Dispose();
	//	}

	//	void ThrowIfDisposed()
	//	{
	//		if (_disposed) throw new ObjectDisposedException(nameof(ConcurSeqTaskContextExecLongRunM<T>));
	//	}
	//}





	/// <summary>
	/// 동시 추가 가능·순차 실행; UI 작업은 메인 스레드, 나머지는 스레드풀에서 처리.
	/// </summary>
	//public class ConcurSeqTaskContextExecM<T> where T : IUIThreadCheck
	//{
	//	readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
	//	readonly Func<T, Task> _processor;
	//	int _isRunning = 0;

	//	public ConcurSeqTaskContextExecM(Func<T, Task> processor)
	//	{
	//		_processor = processor ?? throw new ArgumentNullException(nameof(processor));
	//	}

	//	public void Enqueue(T item)
	//	{
	//		_channel.Writer.TryWrite(item);
	//		if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
	//			_ = Task.Run(ProcessQueueAsync);
	//	}

	//	async Task ProcessQueueAsync()
	//	{
	//		var reader = _channel.Reader;

	//		while (await reader.WaitToReadAsync())
	//		{
	//			while (reader.TryRead(out var item))
	//			{
	//				try
	//				{
	//					if (item.IsUIThread())
	//					{
	//						var tcs = new TaskCompletionSource<bool>();
	//						ConcurSeqTaskContextExecMConfig.UiContext.Post(async _ =>
	//						{
	//							try
	//							{
	//								await _processor(item);
	//								tcs.TrySetResult(true);
	//							}
	//							catch (Exception ex)
	//							{
	//								tcs.TrySetException(ex);
	//							}
	//						}, null);
	//						await tcs.Task;
	//					}
	//					else
	//					{
	//						await _processor(item).ConfigureAwait(false);
	//					}
	//				}
	//				catch (Exception ex)
	//				{
	//					Console.WriteLine($"[Error] {ex}");
	//				}
	//			}
	//		}

	//		Interlocked.Exchange(ref _isRunning, 0);

	//		// 채널이 아직 닫히지 않았고 새 아이템이 들어왔으면 재시작
	//		if (!_channel.Reader.Completion.IsCompleted &&
	//			!_channel.Reader.Completion.IsFaulted &&
	//			!_channel.Reader.Completion.IsCanceled &&
	//			Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
	//		{
	//			_ = Task.Run(ProcessQueueAsync);
	//		}
	//	}
	//}



}
