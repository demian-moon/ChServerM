using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{

	public class CmdArgM<E>
	{
		E _cmdType;
		object _oCmdArg;

		static int iNum = 0;

		public int _count;

		public CmdArgM() { _count = Interlocked.Increment(ref iNum); }

		public void SetArg(E eCmdType, object oCmdArg)
		{
			this.eCmd = eCmdType;
			this.oCmdArg = oCmdArg;
		}

		//public CmdArgM(E eCmdType, object oCmdArg)
		//{
		//    this.eCmd = eCmdType;
		//    this.oCmdArg = oCmdArg;
		//}

		public E eCmd { get => _cmdType; set => _cmdType = value; }
		public object oCmdArg { get => _oCmdArg; set => _oCmdArg = value; }

		//public void Clear()
		//{
		//    eCmd = default(E);
		//    oCmdArg = null;
		//}
	}


	// 리턴값이 필요 없는 경우 쓴다
	/// <summary>
	/// 멀티 쓰레드 환경에서 싱글 스레드로 데이터 변경을 하기 위해 
	/// Boxing, UnBoxing이 일어나지 않도록 arg로 전달되는 object에는 int형 등을 피할 것
	/// 리턴값이 없는 경우에 쓴다. 리턴값이 있는 경우는 AbCmdFuncMachineM을 사용 할 것
	/// </summary>
	/// <typeparam name="E"></typeparam>
	/// <typeparam name="T"></typeparam>
	public abstract class AbCmdMachineM<E, T> where E : Enum
	{
		protected T _tagetObj;

		protected ConcurrentObjPoolM<CmdArgM<E>> _cmdArgPool = new ConcurrentObjPoolM<CmdArgM<E>>();  // 오브젝트 Pool 매번 생성하는 것을 방지하기 위해서, 한번 커지면 줄어들지 않지만, 


		public AbCmdMachineM(T targetObj)
		{
			_tagetObj = targetObj;
			_actBlock = new ActionBlock<CmdArgM<E>>(_RunCmdAction);

			LoadCmdActions();

		}

		ActionBlock<CmdArgM<E>> _actBlock;

		// usort 패킷 타입과 액션을 가진 dictionary
		Dictionary<E, Func<object, Task>> _dicCmdAction = new();


		/// <summary>
		/// 외부에서 사용자 정의 명령을 실행하기 위해 호출하는 RunCmd 함수 (싱글쓰레드 실행을 보장하기 위한)        
		/// </summary>
		/// <param name="eCmd"></param>
		/// <param name="oArg"></param>
		/// <returns></returns>
		public async Task RunCmdAction(E eCmd, object oArg)
		{
			CmdArgM<E> cmdArgM = _cmdArgPool.Rent();
			cmdArgM.SetArg(eCmd, oArg);
			await _actBlock.SendAsync(cmdArgM).ConfigureAwait(false);
		}

		/// <summary>
		/// TransformBlock --> BufferBlock으로 이어지는 DataFlow에서 사용되는 실제 실행 함수
		/// </summary>
		/// <param name="cmdArgM"></param>
		/// <returns></returns>
		/// <exception cref="Exception"></exception>
		private async Task _RunCmdAction(CmdArgM<E> cmdArgM)
		{
			try
			{
				if (_dicCmdAction.TryGetValue(cmdArgM.eCmd, out Func<object, Task> cmdAction) == true)
				{
					await cmdAction(cmdArgM.oCmdArg).ConfigureAwait(false);
				}
				else
				{
					Debug.WriteLine("CmdMachine Cmd 타입이 없음");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine("CmdMachine Cmd 타입이 없음\"):" + ex.Message);
				throw new Exception();
			}

			_cmdArgPool.Return(cmdArgM);
		}


		/// <summary>
		/// 추가해 놓은 명령 함수들을 로딩하는 부분 - 생성자에서 처리
		/// </summary>
		private void LoadCmdActions()
		{
			AddCmdHandler(_tagetObj, _dicCmdAction);
		}

		/// <summary>
		/// 실제 사용자 함수 정의를 등록하는 부분
		/// </summary>
		/// <param name="targetObj">CmdMachine 인스턴스를 가진 객체 - 실제 실행함수를 가지고 있는 객체를 의미</param>
		/// <param name="dicCmdAction">해당 매개변수로 전달되는 dicCmdAction에 명령을 추가 </param>
		public abstract void AddCmdHandler(T targetObj, Dictionary<E, Func<object, Task>> dicCmdAction);

	}



	//public enum E_VISOTOR_CMD { SET_HP }


	/// <summary>
	/// 비지토 커맨드 머신의 ActionBlock에서 실제 Action 함수가 받는 매개변수 
	/// 이 클래스의 멤버 가지고 실제 Action을 함(타겟 오브젝트에 eCmd로 function 찾아서 arg 매개변수로 넘겨서 처리 함)
	/// </summary>
	/// <typeparam name="E"></typeparam>
	/// <typeparam name="T"></typeparam>
	public class VisitorSourceM<E, T> where T : IHasGameOid
	{

		public VisitorSourceM()
		{

		}
		public void SetArg(E eCmd, T targetObj, object oArg)
		{
			_eCmd = eCmd;
			_targetObj = targetObj;
			_oArg = oArg;
		}
		public E _eCmd;
		public T _targetObj;
		public object _oArg;


	}

	/// <summary>
	/// 비지토 커맨드 머신
	/// </summary>
	/// <typeparam name="E"></typeparam>
	/// <typeparam name="T"></typeparam>
	public abstract class AbVisitorCommandMachineM<E, T> where T : IHasGameOid where E : Enum
	{

		int _iCntVisitorActBlock;
		protected ConcurrentObjPoolM<VisitorSourceM<E, T>> _cmdArgPool = new ConcurrentObjPoolM<VisitorSourceM<E, T>>();  // 오브젝트 Pool 매번 생성하는 것을 방지하기 위해서, 한번 커지면 줄어들지 않지만, 

		public ActionBlock<VisitorSourceM<E, T>>[] _arrVisitorActBlock;


		Dictionary<E, Action<VisitorSourceM<E, T>>> _dicCmdAction = new Dictionary<E, Action<VisitorSourceM<E, T>>>();

		public AbVisitorCommandMachineM(int iCntVisitorActBlock)
		{
			_iCntVisitorActBlock = iCntVisitorActBlock;
			LoadCmdActions();

			for (int i = 0; i < _iCntVisitorActBlock; i++)
				_arrVisitorActBlock[i] = new ActionBlock<VisitorSourceM<E, T>>(VisitObject);
		}

		public async void RunVisitorCmd(E eCmd, T targetObj, object oArg)
		{
			var visitorSourceM = _cmdArgPool.Rent();
			visitorSourceM.SetArg(eCmd, targetObj, oArg);

			var idx = targetObj.Oid % _iCntVisitorActBlock;
			await _arrVisitorActBlock[idx].SendAsync(visitorSourceM).ConfigureAwait(false);
		}

		/// <summary>
		/// ActionBlock에서 사용하는 실제 action 함수
		/// </summary>
		/// <param name="source"></param>
		private void VisitObject(VisitorSourceM<E, T> source)
		{
			if (_dicCmdAction.TryGetValue(source._eCmd, out Action<VisitorSourceM<E, T>> actFunc) == true)
			{
				actFunc(source);
			}
			else
			{
				Debug.WriteLine("VisitObject Cmd 타입이 없음");
			}


			_cmdArgPool.Return(source);
		}

		abstract public void AddCmdHandler(Dictionary<E, Action<VisitorSourceM<E, T>>> dicCmdAction);

		private void LoadCmdActions()
		{
			AddCmdHandler(_dicCmdAction);
		}

	}

}
