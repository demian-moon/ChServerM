using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	public class TickTimeM
	{

		public static long GTick
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => Stopwatch.GetTimestamp();
		}

		public static double GTickMs
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => Stopwatch.GetTimestamp() * 1000.0 / (double)Stopwatch.Frequency;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double GetTickAfterMs(double ms)
		{
			return Stopwatch.GetTimestamp() + (double)MsToTick(ms);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double GetElapsedMs(long preTick)
		{
			var tick = Stopwatch.GetTimestamp() - preTick;

			return tick * 1000.0 / (double)Stopwatch.Frequency;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double GetElapsedSec(long preTick)
		{
			var tick = Stopwatch.GetTimestamp() - preTick;
			if (tick <= 0)
			{
				return 0;
			}

			return tick / (double)Stopwatch.Frequency;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long GetElapsedTick(long preTick)
		{
			var tick = Stopwatch.GetTimestamp() - preTick;
			if (tick <= 0)
			{
				return 0;
			}

			return tick;
		}

		public static long GTickPerSec { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return Stopwatch.Frequency; } }         // 스톱워치의 1초에 해당 하는 값
		public static long GTickPerMs { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return Stopwatch.Frequency / 1000; } }   // 스톱워치의 1/1000초 (ms) 해당하는 값

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long MsToTick(double ms)
		{
			return (long)(ms * Stopwatch.Frequency / 1000.0);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long SecToTick(double sec)
		{
			return (long)(sec * Stopwatch.Frequency);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double GTickToMs(long tick)
		{
			if (tick <= 0)
				return 0;

			return tick * 1000.0 / (double)Stopwatch.Frequency;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double GTickToSec(long tick)
		{
			if (tick == 0)
				return 0;

			if (tick < 0)
			{
				Debug.Assert(false, "GTickToSec - tick 값이 음수임!!");
			}

			return tick / (double)Stopwatch.Frequency;
		}
	}

	/// <summary>
	/// RefreshLastUpdateTime이후 주어진 intervalMs이 지났는지 체크 하는 함수 (최초는 생성 시점이 lastUpdateTime)
	/// </summary>
	public class ElapsedTimeManM
	{
		int _intervalMs;
		long _lastUpdateTimeTick;  // 마지막 실행시간
		long _intervalTick;

		public ElapsedTimeManM(int intervalMs)
		{
			// RefreshLastUpdateTime(); 맨처음은 무조건 실행시키기 위해서 주석처리
			_intervalMs = intervalMs;
			_intervalTick = TickTimeM.MsToTick(intervalMs);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsElapsed()
		{
			if(_intervalMs <= 0)
			{
				return true; // -1 이면 무조건 실행
			}

			if ((Stopwatch.GetTimestamp() - _lastUpdateTimeTick) >= _intervalTick)
			{
				return true;
			}
			return false;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public bool IsElapsed(long lastUpdateTime, int intervalMs)
		{
			if (TickTimeM.GetElapsedMs(lastUpdateTime) < intervalMs)
			{
				return false;
			}
			return true;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RefreshLastUpdateTime()
		{
			_lastUpdateTimeTick = Stopwatch.GetTimestamp();
		}

		/// <summary>
		/// 업데이트 남은 시간
		/// </summary>
		/// <returns></returns>
		/// [MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetLeftUpdateTimeMs()
		{
			return _intervalMs - GetElapsedUpdateTimeMs();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long GetLeftUpdateTimeTick()
		{
			return _intervalTick - GetPastUpdateTick();
		}

		/// <summary>
		/// 업데이트 후 지난 시간 
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetElapsedUpdateTimeMs()
		{
			return (int)TickTimeM.GetElapsedMs(_lastUpdateTimeTick);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long GetPastUpdateTick()
		{
			return Stopwatch.GetTimestamp() - _lastUpdateTimeTick;
		}
	}


	/// <summary>
	/// invervalMs 시간을 받고 그 시간이 지나야만 실행이 가능한 함수	
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public abstract class ElapsedExecuteM<T> where T : class
	{
		T _targetObj;

		private ElapsedTimeManM _elapsedTimeMan;

		/// <summary>
		/// 
		/// </summary>
		/// <param name="targetObj">실행의 주체 인스턴스</param>
		/// <param name="invervalMs">-1 값이면 실행에 지연시간이 없음</param>
		public ElapsedExecuteM(T targetObj, int invervalMs)
		{
			_targetObj = targetObj;
			_elapsedTimeMan = new ElapsedTimeManM(invervalMs);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public virtual bool CanExeCute()
		{
			return true;
		}

		public async ValueTask<bool> Execute()
		{
			if (_elapsedTimeMan.IsElapsed() == false || CanExeCute() == false)
			{
				return false;
			}

			await ImpExecute(_targetObj).ConfigureAwait(false);
			_elapsedTimeMan.RefreshLastUpdateTime();
			return true;
		}

		public abstract ValueTask ImpExecute(T targetObj);   // 실제 실행할 함수구현 - 넘겨진 targetObj의 함수를 호출한다

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetLeftExecuteTimeMs()
		{
			return _elapsedTimeMan.GetLeftUpdateTimeMs();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetPastExecuteTimeMs()
		{
			return _elapsedTimeMan.GetElapsedUpdateTimeMs();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long GetLeftExecuteTimeTick()
		{
			return _elapsedTimeMan.GetLeftUpdateTimeTick();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long GetPastExecuteTimeTick()
		{
			return _elapsedTimeMan.GetPastUpdateTick();
		}

	}

	/// <summary>
	/// invervalMs 시간을 받고 그 시간이 지나야만 실행이 가능한 
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public struct ElapsedExecuteFuncAsyncM<T> where T : struct
	{
		public delegate ValueTask FuncAsync(T arg);

		private ElapsedTimeManM _elapsedTimeMan;
		FuncAsync funcAsync;

		public ElapsedExecuteFuncAsyncM(int invervalMs, FuncAsync funcAsync)
		{
			_elapsedTimeMan = new ElapsedTimeManM(invervalMs);
			this.funcAsync = funcAsync;
		}


		public async ValueTask Execute(T arg)
		{
			if (_elapsedTimeMan.IsElapsed() == false)
			{
				return;
			}

			await funcAsync.Invoke(arg).ConfigureAwait(false);

			_elapsedTimeMan.RefreshLastUpdateTime();
			return;
		}


		public int GetLeftExecuteTimeMs()
		{
			return _elapsedTimeMan.GetLeftUpdateTimeMs();
		}

		public int GetPastExecuteTimeMs()
		{
			return _elapsedTimeMan.GetElapsedUpdateTimeMs();
		}

		public long GetLeftExecuteTimeTick()
		{
			return _elapsedTimeMan.GetLeftUpdateTimeTick();
		}

		public long GetPastExecuteTimeTick()
		{
			return _elapsedTimeMan.GetPastUpdateTick();
		}

	}
}
