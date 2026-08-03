using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	// 스크립트 매니져
	/// <summary>
	/// 작성된 스크립트를 동록해서 로딩(상속 class)하고 해당 스크립트를 얻어오는 함수
	/// </summary>
	/// <typeparam name="T"></typeparam>   

	public abstract class AbScriptManM
	{
		Dictionary<string, AbScriptM> _dicScript = new Dictionary<string, AbScriptM>();

		protected const string NULL_SCRIPT_NAME = "null";

		/// <summary>
		/// 상속해서 NullScript를 작성하면 알아서 등록됨
		/// </summary>
		/// <returns></returns>
		protected abstract AbScriptM CreateNullScript();

		/// <summary>
		/// 상속 구현해서 작성된 모든 스크립트를 AddScript<S> 함수로 로딩한다.
		/// </summary>
		protected abstract void _RegisterScript();


		public AbScriptManM()
		{
			//RegisterScript(); 명시적으로 등록하는게 낫다 오류 검증등
		}

		protected void AddScript<S>(string scriptName) where S : AbScriptM, new()
		{

			if (_dicScript.ContainsKey(scriptName) == true)
			{
				throw new DuplicateNameException($"스크립트 이름이 중복됩니다. {scriptName}");
			}

			var script = new S();
			script.Name = scriptName;
			_dicScript.Add(scriptName, script);
		}

		protected void AddScript(AbScriptM script)
		{
			if (string.IsNullOrEmpty(script.Name))
			{
				throw new InvalidOperationException($"{script} Name 프로퍼티에 스크립트 이름을 반드시 설정해야 합니다.");
			}

			if (_dicScript.ContainsKey(script.Name) == true)
			{
				throw new DuplicateNameException($"스크립트 이름이 중복됩니다. {script.Name}");
			}

			_dicScript.Add(script.Name, script);
		}


		public void RegisterScript()
		{
			// Null 스크립트 등록, 이름까지 "null"로 등록
			var nullScript = CreateNullScript();
			nullScript.Name = NULL_SCRIPT_NAME;
			if (_dicScript.ContainsKey(NULL_SCRIPT_NAME) == false)
				_dicScript.Add(nullScript.Name, nullScript);

			_RegisterScript();
		}

		/// <summary>
		/// 로딩된 모든 스크립트에서 스크립트 이름으로 해당 스크립트를 찾는다
		/// </summary>
		/// <param name="scriptName"></param>
		/// <returns></returns>
		public AbScriptM GetScript(string scriptName)
		{
			if (_dicScript.TryGetValue(scriptName, out AbScriptM script) == false)
			{
				throw new KeyNotFoundException($"스크립트가 없습니다. - {scriptName}");
			}
			return script.Clone() as AbScriptM;
		}

		public void Clear()
		{
			_dicScript.Clear();
		}
	}


	// 스크립트 
	/// <summary>
	/// 스크립트 추상 클래스
	/// </summary>
	public abstract class AbScriptM : ICloneable
	{
		//ScriptDelaysM _sleepM;
		//ScriptDelaysM SleepM 
		//{
		//	get
		//	{
		//		return LazyInitializer.EnsureInitialized(ref _sleepM, () => new ScriptDelaysM(ServerM.gTimeScheduler));
		//	}
		//}

		/// <summary>
		/// 반드시 설정해야 한다
		/// </summary>
		public object Self { get; set; }

		AbScriptableForGameObjM Trigger { get; set; }

		public string Name { get; set; }

		// 업데이트 interval
		long _fixedupdateIntervalTick;
		long _updateIntervalTick;

		// last 업데이트 Tick
		long _lastUpdateTick;
		long _lastFixedUpdateTick;


		// Start() 실행
		bool _bStarFuncProcessed;

		// Run() 실행 시간 기록용
		long _lastRunTick;

		// 실행중인지 체크
		volatile int _updateRunningFlag;

		protected long FixedupdateIntervalTick
		{
			get
			{
				if (_fixedupdateIntervalTick == 0)
					_fixedupdateIntervalTick = TickTimeM.MsToTick(GetFixedUpdateIntervalMs());

				return _fixedupdateIntervalTick;
			}
			set { _fixedupdateIntervalTick = value; }
		}

		protected long UpdateIntervalTick
		{
			get
			{
				if (_updateIntervalTick == 0)
					_updateIntervalTick = TickTimeM.MsToTick(GetUpdateIntervalMs());

				return _updateIntervalTick;
			}
			set { _updateIntervalTick = value; }
		}

		public bool StarFuncProssed { get => _bStarFuncProcessed; set { if (_bStarFuncProcessed == false) _bStarFuncProcessed = value; } }

		protected long LastFixedUpdateTick
		{
			get
			{
				if (_lastFixedUpdateTick == 0)
				{
					_lastFixedUpdateTick = TickTimeM.GTick;
				}

				return _lastFixedUpdateTick;
			}
			set => _lastFixedUpdateTick = value;
		}


		protected long LastUpdateTick
		{
			get
			{
				if (_lastUpdateTick == 0)
					_lastUpdateTick = Stopwatch.GetTimestamp();

				return _lastUpdateTick;
			}
			set => _lastUpdateTick = value;
		}

		protected long LastRunTick
		{
			get
			{
				if (_lastRunTick == 0)
					_lastRunTick = Stopwatch.GetTimestamp();

				return _lastRunTick;
			}
			set => _lastRunTick = value;
		}

		// 처음 스크립트 실행시간
		protected long StartScriptTick { get; set; }
		

		public void SetTrigger(AbScriptableForGameObjM trigger)
		{
			Trigger = trigger;
		}

		/// <summary>
		/// 유저가 상속해서 구현할 Update
		/// </summary>
		/// <returns></returns>
		/// 
		async ValueTask _Start()
		{
			StartScriptTick = Stopwatch.GetTimestamp(); // 스크립트 시작 시간 기록
			await Start().ConfigureAwait(false);
		}
		protected virtual async ValueTask Start() { }

		protected virtual async ValueTask Update(long curTick, long elapsedTick) { }


		// 스킬 아이템등 사용할 때 실행되는 스크립트 함수
		public async ValueTask RunScript() 
		{
			if (StarFuncProssed == false)
			{
				await _Start().ConfigureAwait(false);
				StarFuncProssed = true;
			}
			
			await Run().ConfigureAwait(false);
			LastRunTick = Stopwatch.GetTimestamp(); // 실행한 시간 기록
		}

		public virtual async ValueTask Run() { }


		virtual protected async ValueTask FixedUpdate(long curTick, long elapsedTick) { }


		virtual protected double GetFixedUpdateIntervalMs()
		{
			return SrvGlobal.srvFixedUpdateDeltaMs;
		}

		virtual protected double GetUpdateIntervalMs()
		{
			return SrvGlobal.srvUpdateDeltaMs;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool CanUpdate()
		{			
			if (GetElpasedUpdateTick() >= UpdateIntervalTick && _updateRunningFlag == 0)
				return true;

			return false;

		}

		virtual public async ValueTask<bool> RunUpdate()
		{
			try
			{
				if (StarFuncProssed == false)
				{
					await _Start().ConfigureAwait(false);
					StarFuncProssed = true;
				}

				var curTick = Stopwatch.GetTimestamp();
				var elapsedUpdateTick = curTick - LastUpdateTick;
				if (elapsedUpdateTick >= UpdateIntervalTick) // 일단 FixedUpdate와 함께 한다
				{
					// 먼저 읽기로 확인
					if (_updateRunningFlag == 0 &&
						Interlocked.CompareExchange(ref _updateRunningFlag, 1, 0) == 0)
					{
						await FixedUpdate(curTick, elapsedUpdateTick).ConfigureAwait(false);
						await Update(curTick, elapsedUpdateTick).ConfigureAwait(false);

						LastUpdateTick = Stopwatch.GetTimestamp();
						_updateRunningFlag = 0; // volatile로 메모리 가시성, 업데이트가 끝났으니 플래그를 초기화
					}
					
					return true;
				}
			}
			catch (Exception ex)
			{
				ServerM.logM.Debug($"{Name} 스크립트 실행 중 예외 발생: {ex.Message}");
				// 예외 처리 로직 추가 (예: 로그 기록, 알림 등)
			}

			return false;

			//////////////////////////////////// Fixed Update 제대로 쓸때 아래 모두 주석 풀어 쓰면 됨 ///////////////////////////////////////////////////////////

			//var fixedupdateDeltaTick = ServerTimeM.MsToTick(SrvGlobal.srvFixedUpdateDeltaMs);   // 서버 전체 적용
			//var elapsedFixedUpdateTick = curTick - lastFixedUpdateTick; // 마지막 FixedUpdateTick 이후 지난 시간            

			////int expCnt = (int)elapsedFixedUpdateTick / (int)_fixedupdateDeltaTick;
			//////Debug.WriteLine($"기대 횟수{expCnt}");
			/////

			//while (elapsedFixedUpdateTick >= fixedupdateDeltaTick) // 지난시간 기준으로 계산
			//{
			//    lastFixedUpdateTick += fixedupdateDeltaTick;
			//    await FixedUpdate(curTick - fixedupdateDeltaTick, fixedupdateDeltaTick).ConfigureAwait(false);
			//    elapsedFixedUpdateTick -= fixedupdateDeltaTick;
			//}

			//if (elapsedFixedUpdateTick > _fixedupdateIntervalTick)
			//{                
			//    await FixedUpdate(curTick, elapsedFixedUpdateTick).ConfigureAwait(false);
			//    lastFixedUpdateTick = ServerTimeM.GTick;
			//}

			//var elapsedUpdateTick = curTick - lastUpdateTick;            
			//if (elapsedUpdateTick >= _updateIntervalTick )
			//{
			//    await Update(curTick, elapsedUpdateTick).ConfigureAwait(false);
			//    lastUpdateTick = ServerTimeM.GTick;                 
			//}                               
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public double GetElapsedFixedUpdateMs()
		{
			return TickTimeM.GetElapsedMs(LastFixedUpdateTick);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long GetElapsedFixedUpdateTick()
		{
			return TickTimeM.GetElapsedTick(LastFixedUpdateTick);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public double GetElapsedUpdateMs()
		{
			return TickTimeM.GetElapsedMs(LastUpdateTick);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long GetElpasedUpdateTick()
		{
			return TickTimeM.GetElapsedTick(LastUpdateTick);
		}

		public object Clone()
		{
			return MemberwiseClone();
		}


	}

	/// <summary>
	/// 게임오브젝트 (컬리전 이벤트가 있는 스크립트)에 쓰임
	/// </summary>
	public interface ICollisionEventM
	{
		public void OnCollisionEnter(CollisionM collision, long curTick, long elapsedTick);

		public void SendCollisionEnterPacketToUsers(CollisionM collision, long curTick, long elapsedTick);

		// 충돌체가 다른 객체와 충돌중일 때 호출되는 메서드
		public void OnCollisionStay(CollisionM collision, long curTick, long elapsedTick);

		public void SendCollisionStayPacketToUsers(CollisionM collision, long curTick, long elapsedTick);

		// 충돌체가 다른 객체와 충돌이 끝났을 때 호출되는 메서드
		public void OnCollisionExit(CollisionM collision, long curTick, long elapsedTick);

		// 충돌체가 트리거 영역에 진입했을 때 호출되는 메서드
		public void OnTriggerEnter(IColliderM other, long curTick, long elapsedTick);

		public void SendTriggerEnterPacketToUsers(IColliderM other, long curTick, long elapsedTick);


		// 충돌체가 트리거 영역에 머무르고 있을 때 호출되는 메서드
		public void OnTriggerStay(IColliderM other, long curTick, long elapsedTick);

		public void SendTriggerStayPacketToUsers(IColliderM other, long curTick, long elapsedTick);

		// 충돌체가 트리거 영역에서 벗어났을 때 호출되는 메서드
		public void OnTriggerExit(IColliderM other, long curTick, long elapsedTick);

	}

	/// <summary>
	/// 스크립터블 게임오브젝트에서 가지고 있는 Script - Collision Event가 있다
	/// </summary>
	public class ScriptForGameObjM : AbScriptM, ICollisionEventM
	{
		//new public AbScriptableForGameObjM Self { get; set; }           // AbScriptM의 Self를 가린다. 변수의 타입에 따라서 불림으로 주의!!! (다이나믹 바인딩안됨)

		public virtual void OnCollisionEnter(CollisionM collision, long curTick, long elapsedTick)
		{
		}

		// 충돌체가 다른 객체와 충돌중일 때 호출되는 메서드
		public virtual void OnCollisionStay(CollisionM collision, long curTick, long elapsedTick)
		{
		}

		// 충돌체가 다른 객체와 충돌이 끝났을 때 호출되는 메서드
		public virtual void OnCollisionExit(CollisionM collision, long curTick, long elapsedTick)
		{
		}

		// 충돌체가 트리거 영역에 진입했을 때 호출되는 메서드
		public virtual void OnTriggerEnter(IColliderM other, long curTick, long elapsedTick)
		{
		}

		// 충돌체가 트리거 영역에 머무르고 있을 때 호출되는 메서드
		public virtual void OnTriggerStay(IColliderM other, long curTick, long elapsedTick)
		{
		}

		// 충돌체가 트리거 영역에서 벗어났을 때 호출되는 메서드
		public virtual void OnTriggerExit(IColliderM other, long curTick, long elapsedTick)
		{
		}

		public virtual void SendCollisionEnterPacketToUsers(CollisionM collision, long curTick, long elapsedTick)
		{
		}

		public virtual void SendCollisionStayPacketToUsers(CollisionM collision, long curTick, long elapsedTick)
		{
		}

		
		// 인터페이스를 구현하는 객체가 트리거로 설정되어 있을 때, 패킷 발생 시키고 있음
		public virtual void SendTriggerEnterPacketToUsers(IColliderM other, long curTick, long elapsedTick)
		{
		}

		public virtual void SendTriggerStayPacketToUsers(IColliderM other, long curTick, long elapsedTick)
		{
		}
	}

}
