using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	/// <summary>
	/// 만료 작업을 소유하는 객체를 위한 인터페이스
	/// </summary>
	public interface IHasTimeEventsM
	{
		ConcurrentDictionary<string, AbTimeEventBaseM> TimeEvents { get; }  // 작업 ID를 키로 하여 관리되는 작업 컬렉션, Job을 Cancel()하면 알아서 지워진다.
	}


	/// <summary>
	/// 특정 시간에 만료되는 작업을 위한 인터페이스
	/// </summary>
	//public interface IExpireJobM
	//{
	//	string IdJob { get; }            // 작업의 고유 식별자
	//	IHasExpireJobsM Owner { get; }    // 작업의 소유자 객체
	//	bool IsCanceled { get; }         // 작업 취소 여부
	//	long ExpireTimestamp { get; }    // Stopwatch 틱 단위로 표현된 만료 시간
		
	//	void ApplyJob();                 // 작업 적용 메서드
	//	void Cancel();                   // 작업 취소 메서드
	//}

	/// <summary>
	/// 만료 가능한 작업의 기본 구현 클래스
	/// </summary>
	public abstract class AbTimeEventBaseM //: IExpireJobM
	{		
		private readonly long _expireTimestamp; // 작업 만료 시간
		private readonly IHasTimeEventsM _owner; // 작업 소유자
		private readonly string _idJob;         // 작업 고유 ID
		private volatile int _isTerminated;              // 작업 종료 상태(원자적 연산을 위한 정수형)
				
		public string IdJob => _idJob;
		public bool IsCanceled
		{
			get { return _isTerminated == 1; }
		}
		public long ExpireTimestamp => _expireTimestamp;
		abstract public IHasTimeEventsM Owner { get; }

		public AbTimeEventBaseM(string idJob, long expireTimestamp)
		{
			//_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_idJob = string.IsNullOrEmpty(idJob) ? throw new ArgumentException("Job ID cannot be null or empty", nameof(idJob)) : idJob;

			_expireTimestamp = expireTimestamp;
		}

		void TerminateJob()
		{
			if (Interlocked.CompareExchange(ref _isTerminated, 1, 0) == 0)
			{
				try
				{
					OnTerminate(_idJob);
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Error terminating job {_idJob}: {ex}");
				}
				finally
				{
					Owner?.TimeEvents.TryRemove(_idJob, out _);  // 작업 소유자에서도 제거
				}
			}
		}

		protected virtual void OnTerminate(string idJob) { }  // 작업 종료 시 호출되는 가상 메서드

		public virtual void ApplyJob() { }        // 작업 적용 시 호출되는 가상 메서드

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Cancel()
		{
			if (IsCanceled)
				return;		
			
			TerminateJob();
		}
	}
	

	/// <summary>
	/// Treiber 스택을 사용한 락-프리(lock-free), 메모리 할당 최소화 슬롯
	/// </summary>
	internal sealed class TimingWheelSlotM
	{
		// 락-프리 스택의 노드
		private class Node
		{
			public AbTimeEventBaseM Job;  // 노드에 저장된 작업
			public Node Next;       // 다음 노드 참조

			// 객체 풀로 반환될 때 초기화를 위한 메서드 추가
			public void Reset()
			{
				Job = null;
				Next = null;
			}
		}

		// Node 객체 풀링을 위한 객체 풀 추가
		private readonly ObjectPoolM<Node> _nodePool = new ObjectPoolM<Node>();

		// 스택의 헤드 포인터
		private Node _head;

		/// <summary>
		/// 작업을 스택에 락-프리 방식으로 추가 (Node 생성 외 추가 할당 없음)
		/// </summary>
		public void AddJob(AbTimeEventBaseM job)
		{
			
			if (job == null) throw new ArgumentNullException(nameof(job));

			var node = _nodePool.Get();
			node.Job = job;

			Node oldHead;
			do
			{
				//oldHead = _head;
				oldHead = Volatile.Read(ref _head);  // 매번 최신값 읽기				
				node.Next = oldHead;
				Thread.SpinWait(1);
			}
			while (Interlocked.CompareExchange(ref _head, node, oldHead) != oldHead);
		}

		/// <summary>
		/// 헤드를 null로 교환하고 스택을 순회하면서 작업을 targetList에 추가
		/// </summary>
		public void ExtractJobsTo(List<AbTimeEventBaseM> targetList)
		{
			if (targetList == null) throw new ArgumentNullException(nameof(targetList));

			var node = Interlocked.Exchange(ref _head, null);
			while (node != null)
			{
				targetList.Add(node.Job);
				var nextNode = node.Next;

				//---- 코드 수정 ----
				// 사용이 끝난 Node를 객체 풀로 반환
				node.Reset();
				_nodePool.Return(node);

				node = nextNode;
			}
		}

		/// <summary>
		/// 슬롯 내 작업의 대략적인 개수 (빈 슬롯 건너뛰기 용도)
		/// </summary>
		public bool IsEmpty
		{
			get
			{
				return _head == null;
			}		
		}

		/// <summary>
		/// 슬롯 내 모든 작업을 취소
		/// </summary>
		public void CancelAllJobs()
		{
			var node = Interlocked.Exchange(ref _head, null);
			while (node != null)
			{
				try
				{
					node.Job.Cancel();
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error cancelling job {node.Job.IdJob}: {ex}");
				}

				// 사용이 끝난 Node를 객체 풀로 반환
				var nextNode = node.Next;
				node.Reset();
				_nodePool.Return(node);
				node = nextNode;
			}
		}
	}

	/// <summary>
	/// 효율적인 타이머 스케줄링을 위한 해시 기반 타이밍 휠
	/// </summary>
	internal sealed class TimingWheelM
	{
		//private readonly int _slotCount;                        // 타이밍 휠의 슬롯 개수
		//private readonly long _tickDurationInStopwatchTicks;    // 각 틱의 지속 시간(Stopwatch 틱 단위)
		//private long _currentTickIndex;                         // 현재 틱 인덱스

		// 개선: 자주 접근하는 데이터를 하나의 구조체로 묶기
		[StructLayout(LayoutKind.Sequential)]
		private struct WheelMetadata
		{
			public readonly int _slotCount;
			public readonly long _tickDurationInStopwatchTicks;
			public long _currentTickIndex;  // 자주 변경되는 데이터는 마지막에

			public WheelMetadata(int slotCount,  long tickDurationInStopwatchTicks)
			{
				_slotCount = slotCount;
				_tickDurationInStopwatchTicks = tickDurationInStopwatchTicks;
			}
		}
		WheelMetadata wheelMeta; // 최적화 위해서

		private readonly TimingWheelSlotM[] _slots;             // 슬롯 배열		
		private readonly string _wheelName;                     // 휠 이름 (로깅용)

		private TimingWheelM _lowerWheel;  // 하위 휠 참조

		public TimingWheelM(int slotCount, long tickDurationInStopwatchTicks, string wheelName = null)
		{
			if (slotCount <= 0) 
				throw new ArgumentOutOfRangeException(nameof(slotCount), "Slot count must be positive");
			
			if (tickDurationInStopwatchTicks <= 0) 
				throw new ArgumentOutOfRangeException(nameof(tickDurationInStopwatchTicks), "Tick duration must be positive");

			_wheelName = wheelName ?? $"Wheel-{Guid.NewGuid().ToString().Substring(0, 8)}";

			wheelMeta = new WheelMetadata(slotCount, tickDurationInStopwatchTicks);
						
			_slots = new TimingWheelSlotM[wheelMeta._slotCount];
			for (int i = 0; i < wheelMeta._slotCount; i++)
				_slots[i] = new TimingWheelSlotM();
		}

		/// <summary>
		/// 하위 휠을 설정
		/// </summary>
		public void SetLowerWheel(TimingWheelM lowerWheel)
		{
			_lowerWheel = lowerWheel;
		}

		/// <summary>
		/// 이 휠의 틱 지속 시간 반환
		/// </summary>
		public long TickDuration => wheelMeta._tickDurationInStopwatchTicks;

		/// <summary>
		/// 이 휠이 커버할 수 있는 최대 시간 범위
		/// </summary>
		public long MaxTimeRange => wheelMeta._tickDurationInStopwatchTicks * wheelMeta._slotCount;

		/// <summary>
		/// 작업을 적절한 슬롯에 스케줄링
		/// </summary>
		public void AddJob(AbTimeEventBaseM job, bool selfAddFlag = false)
		{
			if (job.IsCanceled) return;
			long now = Stopwatch.GetTimestamp();
			long delay = Math.Max(0, job.ExpireTimestamp - now);

			// 하위 휠이 있고, 작업이 하위 휠의 범위 내에 있다면 하위 휠에 작업 추가
			if (_lowerWheel != null && delay < _lowerWheel.MaxTimeRange)
			{
				_lowerWheel.AddJob(job);
				return;
			}

			long ticksAway = delay / wheelMeta._tickDurationInStopwatchTicks;
			long tickIndex = wheelMeta._currentTickIndex;
			if (selfAddFlag) 
			{
				tickIndex ++; // 다시 자신에 넣는거면 최소한 다음틱에 넣어야 한다
			}
			
			int slot = (int)((tickIndex + ticksAway) % wheelMeta._slotCount); ;			
			
			//if(selfAddFlag)
			//	ServerM.logM.Debug("################넣은 슬롯 번호 : " + slot + ":" + _wheelName);
			//else
			//	ServerM.logM.Debug("----------------넣은 슬롯 번호 : " + slot + ":" +_wheelName);

			_slots[slot].AddJob(job);
		}

		/// <summary>
		/// 휠을 currentTimestamp까지 진행시키고, 만료된 작업을 expiredJobs에 추출
		/// </summary>
		public void Advance(long currentTimestamp,
							List<AbTimeEventBaseM> expiredJobs,
							ObjectPoolM<List<AbTimeEventBaseM>> pool)
		{

			if (expiredJobs == null) 
				throw new ArgumentNullException(nameof(expiredJobs));

			if (pool == null) 
				throw new ArgumentNullException(nameof(pool));

			

			long targetTick = currentTimestamp / wheelMeta._tickDurationInStopwatchTicks;
			long ticksToAdvance = targetTick - wheelMeta._currentTickIndex;

			if (ticksToAdvance <= 0)    // 이미 돌았는데 또 진입한 것이면 다음틱까지 아무 것도 안함
			{
				int k = 3;
				if (_wheelName == "shortWheel")
				{
					k = 5;
				}

				return;
			}

			long actual = Math.Min(ticksToAdvance, wheelMeta._slotCount);  // 한 번에 최대 한 바퀴만 진행
			for (long i = 0; i < actual; i++)
			{
				int slot = (int)(wheelMeta._currentTickIndex % wheelMeta._slotCount);
				//if (_wheelName == "shortWheel")
					//ServerM.logM.Debug("현재 진행 슬롯 번호 : " + slot);


				if (_slots[slot].IsEmpty == false)
				{
					var list = pool.Get();
					_slots[slot].ExtractJobsTo(list);
					foreach (var job in list)
					{
						if (job.IsCanceled) continue;
						if (job.ExpireTimestamp <= currentTimestamp)
							expiredJobs.Add(job);  // 만료된 작업 추가
						else
						{
							long remainingDelay = job.ExpireTimestamp - currentTimestamp;
							if (_lowerWheel != null && remainingDelay < _lowerWheel.MaxTimeRange)
							{
								// 하위 휠의 범위 내로 들어왔다면 하위 휠로 전달
								_lowerWheel.AddJob(job);
							}
							else
							{
								// 아직 하위 휠 범위가 아니면 현재 휠에서 재스케줄링
								AddJob(job, true); // 재 스케줄링
							}						
						}						
					}
					list.Clear();
					pool.Return(list);
				}
				wheelMeta._currentTickIndex++;
			}

			if (ticksToAdvance > actual)
				wheelMeta._currentTickIndex = targetTick;  // 여러 바퀴를 건너뛰어야 할 경우
		}

		/// <summary>
		/// 모든 슬롯의 작업을 취소
		/// </summary>
		public void CancelAllJobs()
		{
			for (int i = 0; i < wheelMeta._slotCount; i++)
			{
				_slots[i].CancelAllJobs();
			}
		}

		public override string ToString()
		{
			return $"{_wheelName}: SlotCount={wheelMeta._slotCount}, CurrentTick={wheelMeta._currentTickIndex}";
		}
	}

	/// <summary>
	/// 고성능, 계층적 타이밍 휠 기반 작업 스케줄러
	/// </summary>
	public sealed class TimeEventSchedulerM
	{
		private readonly ConcurrentQueue<AbTimeEventBaseM> _incoming = new ConcurrentQueue<AbTimeEventBaseM>();         // 새 작업 대기열
		private readonly ConcurrentDictionary<string, AbTimeEventBaseM> _allJobs = new ConcurrentDictionary<string, AbTimeEventBaseM>(Environment.ProcessorCount*2, 10000); // 모든 활성 작업
		private readonly ObjectPoolM<List<AbTimeEventBaseM>> _listPool = new ObjectPoolM<List<AbTimeEventBaseM>>();  // 리스트 객체 풀

		private readonly TimingWheelM _shortWheel;     // 단기 타이밍 휠
		private readonly TimingWheelM _mediumWheel;    // 중기 타이밍 휠
		private readonly TimingWheelM _longWheel;      // 장기 타이밍 휠
		private readonly TimingWheelM _veryLongWheel;  // 초장기 타이밍 휠
		private readonly TimingWheelM _monthlyWheel;	// 월단위 타이밍 휠

		private CancellationTokenSource _cts;          // 작업 취소 토큰 소스
		private Task _worker;                          // 작업자 태스크
		private int _intervalMs;                       // 업데이트 간격(밀리초)

		private readonly long _ticksPerMs;             // 밀리초당 틱 수
		private readonly long _ticksPerSecond;         // 초당 틱 수
		private readonly long _ticksPerMinute;         // 분당 틱 수
		private readonly long _ticksPerHour;           // 시간당 틱 수
		private readonly long _ticksPerDay;            // 일당 틱 수
		private readonly long _ticksPerMonth;           // 월당 틱 수

		// 타임스탬프 캐싱을 위한 필드 추가
		private long _lastProcessedTimestamp;

		// 미리 계산된 상수 필드 추가
		private readonly long _shortWheelMaxRange;
		private readonly long _mediumWheelMaxRange;
		private readonly long _longWheelMaxRange;
		private readonly long _veryLongWheelMaxRange;

		private readonly int _maxJobsPerTick;

		private readonly Queue<AbTimeEventBaseM> _deferredExpired = new Queue<AbTimeEventBaseM>(); // 이미 만료 되었지만 지연된 작업을 위한 큐

		public TimeEventSchedulerM(int maxJobsPerTick = 1000)
		{
			_maxJobsPerTick = maxJobsPerTick;

			_ticksPerMs = Stopwatch.Frequency / 1000;
			_ticksPerSecond = Stopwatch.Frequency;
			_ticksPerMinute = _ticksPerSecond * 60;
			_ticksPerHour = _ticksPerMinute * 60;
			_ticksPerDay = _ticksPerHour * 24;
			_ticksPerMonth = _ticksPerDay * 30; // 평균 월 길이(30.44일)이지만 30일로 

			// 미리 계산된 상수값 초기화
			_shortWheelMaxRange = 5 * _ticksPerMinute;
			_mediumWheelMaxRange = _ticksPerDay;
			_longWheelMaxRange = 7 * _ticksPerDay;
			_veryLongWheelMaxRange = 30 * _ticksPerDay;

			_shortWheel = new TimingWheelM(3000, _ticksPerMs * 100, "shortWheel");    // 100ms 해상도, 5분 범위
			_mediumWheel = new TimingWheelM(1440, _ticksPerMinute, "mediumWheel");     // 1분 해상도, 24시간 범위
			_longWheel = new TimingWheelM(168, _ticksPerHour, "longWheel");          // 1시간 해상도, 7일 범위
			_veryLongWheel = new TimingWheelM(30, _ticksPerDay, "veryLongWheel");        // 1일 해상도, 60일 범위
			_monthlyWheel = new TimingWheelM(12, _ticksPerMonth, "monthlyWheel");       // 1개월 해상도, 12개월 범위

			// 계층적 구조 설정: 상위 휠에서 하위 휠로의 참조 설정
			_monthlyWheel.SetLowerWheel(_veryLongWheel);
			_veryLongWheel.SetLowerWheel(_longWheel);
			_longWheel.SetLowerWheel(_mediumWheel);
			_mediumWheel.SetLowerWheel(_shortWheel);
		}

		public void StartLongRunning(int updateIntervalMs)
		{
			if (_worker != null)
				return;

			_intervalMs = Math.Max(1, updateIntervalMs);  // 최소 1ms 간격 보장
			_cts = new CancellationTokenSource();
			
			// 작업자 스레드 시작
			_worker = Task.Factory.StartNew(
				WorkerLoop,
				_cts.Token,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default);
		}

		public void Start(int updateIntervalMs)
		{
			if (_worker != null)
				return;

			_intervalMs = Math.Max(1, updateIntervalMs);  // 최소 1ms 간격 보장
			_cts = new CancellationTokenSource();

			// 작업자 스레드 시작
			_worker = Task.Run(WorkerLoop, _cts.Token);				
		}

		public void Stop()
		{
			_cts?.Cancel();                               // 작업자 스레드 취소 요청
			try 
			{
				ProcessExpired();                        // 남은 작업 처리
				_worker?.Wait(10000);					// 최대 10초간 작업자 종료 대기
			} 
			catch (Exception ex)
			{ 
			
			}        
			
			_cts?.Dispose();
		}

		public void AddJob(AbTimeEventBaseM job)
		{
			if (job.IsCanceled) 
				return; // 이미 취소된 작업 무시

			if(_allJobs.TryAdd(job.IdJob, job))             // 전체 작업 컬렉션에 추가)
			{
				_incoming.Enqueue(job);                      // 새 작업을 대기열에 추가
				job.ApplyJob();                              // 작업 즉시 적용
			}
			else
			{
				Debug.WriteLine($"Job {job.IdJob} already exists in scheduler");
			}
		}

		public bool CancelJob(string jobId)
		{
			if (_allJobs.TryRemove(jobId, out var job))  // 작업 컬렉션에서 제거 시도
			{				
				job.Cancel();                            // 작업 취소
														 // 				
				return true;
			}
			return false;
		}

		private async Task WorkerLoop()
		{
			try
			{
				while (!_cts.Token.IsCancellationRequested)  // 취소 요청이 없는 한 계속 실행
				{
					ProcessExpired();                        // 만료된 작업 처리
					await Task.Delay(_intervalMs, _cts.Token).ConfigureAwait(false);  // 설정된 간격만큼 대기
				}
			}	
			catch (OperationCanceledException) { }  // 작업 취소 예외는 무시
			catch (Exception ex)
			{
				Debug.WriteLine($"Fatal error in scheduler worker: {ex}");
			}
		}

		private void ProcessExpired()
		{
			// 타임스탬프 한 번만 가져오기
			long now = Stopwatch.GetTimestamp();


			// 1. 먼저 지연된 만료 작업부터 처리
			int processedCount = 0;
			while (processedCount < _maxJobsPerTick && _deferredExpired.TryDequeue(out var job))
			{
				if (job.IsCanceled) continue;

				try 
				{					
					job.Cancel(); 
				}
				catch (Exception ex) { /* 로그 처리 */ }
				finally
				{
					
					_allJobs.TryRemove(job.IdJob, out _);
				}
				processedCount++;
			}


			// 새 작업 처리			
			int cnt = 0;
			while (cnt++ < _maxJobsPerTick && _incoming.TryDequeue(out var job))
			{
				if (job.IsCanceled) continue;             // 이미 취소된 작업은 무시
				long delay = job.ExpireTimestamp - now;

				// 지연 시간에 따라 적절한 휠에 직접 할당하여 효율성 유지							
				if (delay <= _shortWheelMaxRange)
					_shortWheel.AddJob(job);              // 5분 이내: 단기 휠에 추가
				else if (delay <= _mediumWheelMaxRange)
					_mediumWheel.AddJob(job);             // 1일 이내: 중기 휠에 추가
				else if (delay <= _longWheelMaxRange)
					_longWheel.AddJob(job);               // 7일 이내: 장기 휠에 추가
				else if (delay <= _veryLongWheelMaxRange)
					_veryLongWheel.AddJob(job);           // 30일 이내: 초장기 휠에 추가
				else
					_monthlyWheel.AddJob(job);            // 그 이상: 월단위 휠에 추가

			}

			// 만료된 작업 수집
			var expired = _listPool.Get();                // 객체 풀에서 리스트 가져오기

			// 상위 휠부터 순차적으로 처리하여 계층적 이동이 일어나도록 함
			// 상위 휠에서 하위 휠로 작업이 이동하므로 이 순서가 중요함
			_monthlyWheel.Advance(now, expired, _listPool);
			_veryLongWheel.Advance(now, expired, _listPool);
			_longWheel.Advance(now, expired, _listPool);
			_mediumWheel.Advance(now, expired, _listPool);
			_shortWheel.Advance(now, expired, _listPool);

			
			// 만료된 작업 실행
			foreach (var job in expired)
			{
				if (job.IsCanceled) continue;             // 취소된 작업은 무시

				if (processedCount < _maxJobsPerTick)
				{
					try
					{
						// 주의 : 소유자에서 작업 제거 먼저 제거해야 Owner에 있는 
						// ExpireJobs를 통해 expire잡이 있는지 검사하는 로직이 올바로 동작한다						
						job.Cancel();						
					}               // 작업 종료 시도
					catch (Exception ex)
					{
						Debug.WriteLine($"Error terminating job {job.IdJob}: {ex}");
					}
					finally
					{							
						_allJobs.TryRemove(job.IdJob, out _);              // 전체 작업에서 제거
					}
					processedCount++;
				}
				else
				{
					// 처리 용량 초과시 바로 지연 큐에 추가 (임시 리스트 사용하지 않음)
					_deferredExpired.Enqueue(job);
				}
			}						

			expired.Clear();                              // 리스트 초기화
			_listPool.Return(expired);                    // 객체 풀에 리스트 반환
		}

		public long CreateExpirationTimestamp(long delayMs)
			=> Stopwatch.GetTimestamp() + delayMs * _ticksPerMs;  // 현재 시간으로부터 지연 시간 후의 만료 타임스탬프 계산

		/// <summary>
		/// 모든 작업을 취소하고 스케줄러 초기화
		/// </summary>
		public void CancelAllJobs()
		{
			// 모든 휠의 작업 취소
			_shortWheel.CancelAllJobs();
			_mediumWheel.CancelAllJobs();
			_longWheel.CancelAllJobs();
			_veryLongWheel.CancelAllJobs();
			_monthlyWheel.CancelAllJobs();

			// 대기 중인 작업 처리
			while (_incoming.TryDequeue(out var job))
			{
				job.Cancel();
			}

			// allJobs 컬렉션 비우기 (모든 작업에 대해 Cancel 호출 포함)
			foreach (var job in _allJobs.Values)
			{
				job.Cancel();
				job.Owner?.TimeEvents.TryRemove(job.IdJob, out _);
			}

			_allJobs.Clear();
		}

		/// <summary>
		/// IDisposable 패턴 구현을 위한 Dispose 메서드
		/// </summary>
		public void Dispose()
		{
			Stop();
			CancelAllJobs();
			_listPool.Clear();
		}

	}



	/*
	internal interface IExpireJob
	{
		string IdJob { get; } // Job Id
		IHasExpireJobs owner { get; }
		bool bCanceled { get; }		
		long expireTick { get; }
		void TerminateJob();
		void ApplyJob();
		void Cancel();
	}

	internal abstract class AbExpireJobM : IExpireJob
	{
		bool _bCanceled;
		long _expireTick;
		IHasExpireJobs _owner;
		string _idJob;

		public string IdJob { get => _idJob; } // Job Id
		public bool bCanceled { get => _bCanceled; }
		public long expireTick { get => _expireTick;}

		public IHasExpireJobs owner { get => _owner; }
		public void TerminateJob() 
		{
			if (Interlocked.CompareExchange(ref _bTerminateJob, 1, 0) == 0)
				SImp_TerminateJob();
		}

		protected virtual void SImp_TerminateJob() { } 

		int _bTerminateJob;
		public virtual void ApplyJob() { }
		public void Cancel()
		{
			_bCanceled = true;
			TerminateJob();
		}

		public AbExpireJobM(IHasExpireJobs owner, string jobId, long expireTick)
		{
			_owner = owner;
			_idJob = jobId;
			_expireTick = expireTick;
		}
	}


	internal interface IHasExpireJobs
	{		
		ConcurrentDictionary<string, IExpireJob> dicExpireJob { get; }

	}

	internal class TimingWheelSlot
	{
		public List<IExpireJob> Jobs = new List<IExpireJob>();
	}

	// 타임 휠 클래스
	internal class TimingWheel
	{
		private readonly int _slotCount;
		private readonly long _tickDuration;
		private readonly TimingWheelSlot[] _slots;
		private long _currentTick;

		public TimingWheel(int slotCount, long tickDuration)
		{
			_slotCount = slotCount;
			_tickDuration = tickDuration;
			_slots = new TimingWheelSlot[_slotCount];
			for (int i = 0; i < _slotCount; i++)
			{
				_slots[i] = new TimingWheelSlot();
			}
			_currentTick = 0;
		}

		public void AddJob(IExpireJob job)
		{
			long delayTicks = job.expireTick - Stopwatch.GetTimestamp();
			if (delayTicks < 0) delayTicks = 0;

			long ticksFromCurrent = delayTicks / _tickDuration;
			int slotIndex = (int)((_currentTick + ticksFromCurrent) % _slotCount);

			lock (_slots[slotIndex])
			{
				_slots[slotIndex].Jobs.Add(job);
			}
		}

		public void Advance(long currentTimestamp, List<IExpireJob> expiredJobs)
		{
			long ticksToAdvance = (currentTimestamp - _currentTick * _tickDuration) / _tickDuration;
			if (ticksToAdvance <= 0) return;

			for (long i = 0; i < ticksToAdvance; i++)
			{
				int slotIndex = (int)(_currentTick % _slotCount);
				List<IExpireJob> toExpire = null;
				lock (_slots[slotIndex])
				{
					toExpire = new List<IExpireJob>(_slots[slotIndex].Jobs);
					_slots[slotIndex].Jobs.Clear();
				}

				foreach (var job in toExpire)
				{
					if (job.expireTick <= currentTimestamp && !job.bCanceled)
					{
						expiredJobs.Add(job);
					}
					else
					{
						// 아직 만료되지 않은 작업은 다시 추가
						AddJob(job);
					}
				}

				_currentTick++;
			}
		}
	}

	internal class ExpireJobScheduler
	{
		int _updateIntervalMs;
		CancellationTokenSource _cancellationTokenSource;
		ConcurrentQueue<IExpireJob> _incommingQueue = new();
		PriorityQueue<IExpireJob, long> _expireQueue = new();

		Task _updateTask;

		public void Start(int updateIntervalMs)
		{
			_updateIntervalMs = updateIntervalMs;
			_cancellationTokenSource = new CancellationTokenSource();
			_updateTask = Task.Run(UpdateLoop, _cancellationTokenSource.Token);
		}

		public void Stop()
		{
			_cancellationTokenSource?.Cancel();
			_updateTask?.Wait(1000);
		}

		async Task UpdateLoop()
		{
			while (!_cancellationTokenSource.Token.IsCancellationRequested)
			{
				ProcessExpiredJob();
				await Task.Delay(_updateIntervalMs, _cancellationTokenSource.Token);
			}
		}


		private void ProcessExpiredJob()
		{
			// _incommingQueue에 있는 모든 만료 작업을 _expireQueue에 추가
			while (_incommingQueue.TryDequeue(out var expireJob))
			{
				_expireQueue.Enqueue(expireJob, expireJob.expireTick);
			}

			long currentTime = Stopwatch.GetTimestamp(); // ServerTimeM.GTick
			{
				// 만료된 효과가 있는 동안 반복
				while (_expireQueue.Count > 0 && _expireQueue.Peek().expireTick <= currentTime)
				{
					var expireJob = _expireQueue.Dequeue();

					// 이미 비활성화된 효과는 무시
					if (expireJob.bCanceled) continue;

					// 효과 종료 처리
					expireJob.TerminateJob();
					expireJob.owner.dicExpireJob.TryRemove(expireJob.IdJob, out var _);
				}
			}
		}

		public void AddJob(IExpireJob job)
		{
			_incommingQueue.Enqueue(job);
			job.owner.dicExpireJob.TryAdd(job.IdJob, job);
			job.ApplyJob();
		}

	} */
}
