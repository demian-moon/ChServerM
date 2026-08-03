using System;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{


	/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// 비동기로 구현 !!
	/// <summary>
	/// 멀티쓰레드 환경에서 함수 동시 실행 방지 클래스 
	/// 특정 T targetObj의 함수자체를 중복 실행하지 못하게 막는다 - 중복 실행되지만 순차적으로(싱글스레드) 실행하는 AbCmdMachine과 차이가 있음
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public abstract class SingleExecuteAsyncM<T>
	{
		readonly object _lockObj = new object();

		protected T _targetObj;
		int _bRunning;


		public SingleExecuteAsyncM(T targetObj)
		{
			_targetObj = targetObj;
		}

		public virtual bool CanExeCute() { return true; }

		public async Task ExecuteAsync()
		{
			if (_CanExeCute() == false)
				return;

			//lock(_lockObj)
			//{               
			//    _bRunning = true;
			//}

			Interlocked.CompareExchange(ref _bRunning, 1, 0);

			await ImpSingleExecuteAyncFunc(_targetObj).ConfigureAwait(false);

			Interlocked.CompareExchange(ref _bRunning, 0, 1);

			//lock(_lockObj)            
			//{

			//    _bRunning = false;
			//}            

			return;
		}

		public bool _CanExeCute()
		{
			if (_bRunning == 1 || CanExeCute() == false)
				return false;

			return true;
		}


		// 임시로 주석 처리 - 필요하면 풀 것
		//public virtual bool ImpCanExecute() // 상속받아 구현할 때 실행 조건 
		//{
		//    return true;
		//}

		public abstract Task ImpSingleExecuteAyncFunc(T targetObj);   // 실제 실행할 함수구현 - 넘겨진 targetObj의 함수를 호출한다

	}

	/// <summary>
	/// 멀티 쓰레드 상에서 동시 실행 되지 않도록 
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public abstract class ElapsedSingleExecuteAsyncM<T> : SingleExecuteAsyncM<T>
	{

		private ElapsedTimeManM _elapsedTimeMan;

		public ElapsedSingleExecuteAsyncM(T targetObj, int invervalMs) : base(targetObj)
		{
			_elapsedTimeMan = new ElapsedTimeManM(invervalMs);
		}

		public override bool CanExeCute()
		{
			//return true;
			return _elapsedTimeMan.IsElapsed();
		}

		public override async Task ImpSingleExecuteAyncFunc(T targetObj)
		{
			await ImpExecuteAsync(targetObj).ConfigureAwait(false);
			_elapsedTimeMan.RefreshLastUpdateTime();
		}

		public abstract Task ImpExecuteAsync(T targetObj);   // 실제 실행할 함수구현 - 넘겨진 targetObj의 함수를 호출한다

	}


	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////    
	/// <summary>
	/// 중복 실행 방지 - delegate 
	/// </summary>

	public struct SingleExecuteFuncAsyncM
	{
		public delegate ValueTask FuncAsync();

		FuncAsync funcAsync;
		Func<bool> canExecute;
		int _runFlag;

		public SingleExecuteFuncAsyncM(FuncAsync funcAsync, Func<bool> canExecute = null)
		{
			this.funcAsync = funcAsync;

			if (canExecute != null)
				this.canExecute = canExecute;
			else
				canExecute = () => true;
		}

		public void SetAction(FuncAsync funcAsync)
		{
			this.funcAsync = funcAsync;
		}

		public async ValueTask Execute()
		{
			if (_runFlag == 1 || canExecute?.Invoke() == false)
				return;

			if (Interlocked.CompareExchange(ref _runFlag, 1, 0) == _runFlag)
				return;

			await funcAsync.Invoke().ConfigureAwait(false);

			_runFlag = 0;
			return;
		}
	}

	/// <summary>
	/// 중복 실행 방지 - delegate (매개변수 T)
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public struct SingleExecuteFuncAsyncM<T>
	{
		public delegate ValueTask FuncAsync(in T arg);

		FuncAsync funcAsync;
		Func<bool> canExecute;
		int _runFlag;

		public SingleExecuteFuncAsyncM(FuncAsync funcAsync, Func<bool> canExecute = null)
		{
			this.funcAsync = funcAsync;

			if (canExecute != null)
				this.canExecute = canExecute;
			else
				canExecute = () => true;
		}

		public void SetAction(FuncAsync funcAsync)
		{
			this.funcAsync = funcAsync;
		}

		public async ValueTask Execute(T arg)
		{
			if (_runFlag == 1 || canExecute?.Invoke() == false)
				return;

			if (Interlocked.CompareExchange(ref _runFlag, 1, 0) == _runFlag)
				return;

			await funcAsync.Invoke(arg).ConfigureAwait(false);

			_runFlag = 0;
			return;
		}
	}


	/// <summary>
	/// 일정시간 간격으로 한번만 실행되는 함수(async delegate)를 위한 구조체
	/// 실행을 담당하는 async 함수가 완료되지 않으면 재실행되지 않게 설계됨
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public struct ElapsedSingleExecuteFuncAsyncM<T>
	{
		public delegate ValueTask FuncAsync(T arg);

		ElapsedTimeManM elapsedMan;

		FuncAsync funcAsync;
		Func<bool> canExecute;
		int _runFlag;

		public ElapsedSingleExecuteFuncAsyncM(int intervalMs, FuncAsync funcAsync, Func<bool> canExecute = null)
		{
			this.funcAsync = funcAsync;
			this.elapsedMan = new ElapsedTimeManM(intervalMs);

			if (canExecute != null)
				this.canExecute = canExecute;
			else
				canExecute = () => true;
		}

		public void SetActionFunc(FuncAsync funcAsync)
		{
			this.funcAsync = funcAsync;
		}

		public void SetCanExecuteFunc(Func<bool> canExecute)
		{
			this.canExecute = canExecute;
		}

		public async ValueTask Execute(T arg)
		{
			if (_runFlag == 1 || canExecute?.Invoke() == false || elapsedMan.IsElapsed() == false)
				return;

			if (Interlocked.CompareExchange(ref _runFlag, 1, 0) == _runFlag)
				return;

			await funcAsync.Invoke(arg).ConfigureAwait(false);
			elapsedMan.RefreshLastUpdateTime();

			_runFlag = 0;
			return;
		}
	}


	public abstract class AbSingleExecuteM<T>
	{

		protected T _targetObj;
		int _runFlag;
		//SpinLock spinLock = new SpinLock();


		public AbSingleExecuteM(in T targetObj)
		{
			_targetObj = targetObj;
		}

		public virtual bool CanExeCute() { return true; }

		public async ValueTask Execute()
		{
			if (_CanExeCute() == false)
				return;


			if (Interlocked.CompareExchange(ref _runFlag, 1, 0) == _runFlag)
				return;

			await ImpSingleExecute(_targetObj).ConfigureAwait(false);

			_runFlag = 0;


			return;
		}

		public bool _CanExeCute()
		{
			if (_runFlag == 1 || CanExeCute() == false)
				return false;

			return true;
		}


		// 임시로 주석 처리 - 필요하면 풀 것
		//public virtual bool ImpCanExecute() // 상속받아 구현할 때 실행 조건 
		//{
		//    return true;
		//}

		public abstract ValueTask ImpSingleExecute(T targetObj);   // 실제 실행할 함수구현 - 넘겨진 targetObj의 함수를 호출한다

	}



	/// <summary>
	/// 멀티 쓰레드 상에서 동시 실행 되지 않도록 
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public abstract class ElapsedSingleExecuteM<T> : AbSingleExecuteM<T>
	{

		private ElapsedTimeManM _elapsedTimeMan;

		public ElapsedSingleExecuteM(in T targetObj, int invervalMs) : base(targetObj)
		{
			_elapsedTimeMan = new ElapsedTimeManM(invervalMs);
		}

		public override bool CanExeCute()
		{
			//return true;
			return _elapsedTimeMan.IsElapsed();
		}

		public override async ValueTask ImpSingleExecute(T targetObj)
		{
			await ImpExecute(targetObj).ConfigureAwait(false);
			_elapsedTimeMan.RefreshLastUpdateTime();
		}

		public abstract ValueTask ImpExecute(T targetObj);   // 실제 실행할 함수구현 - 넘겨진 targetObj의 함수를 호출한다

	}
}
