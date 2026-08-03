using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	/// <summary>
	/// 유저 디스커넥트 인터페이스 - 파이프 Writer에서 Fin받았을 때 종료 처리를 위한 추상 클래스
	/// </summary>
	public abstract class AbDisconnectProcess
	{
		public string name = "cla";

		public string GetName() { return name; }
		abstract public ValueTask DisconnectProcess(TcpClient tc);


	}


	/// <summary>
	/// 유저 클래스
	/// </summary>
	//[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public class InnerUserM : PkObjM, IDisposable, IObservable<InnerUserM>
	{

		string _id;         // 유저 ip
		string _pw;   // 유저 pw
		protected TimerM<TIMER_TYPE> dicTimer = new TimerM<TIMER_TYPE>();

		List<byte[]> sendByteBuffer = new List<byte[]>();

		ALLOWED_PACKET_STATE _allowedPkState;
		USER_OBSERVER_STATE _observerState;

		public string Id { get => _id; set => _id = value; }
		//public string Pw { get => _pw; set => _pw = value; }

		public ALLOWED_PACKET_STATE AllowedPkState { get => _allowedPkState; set => _allowedPkState = value; }
		public USER_OBSERVER_STATE ObserverState { get => _observerState; set => _observerState = value; }


		//[MethodImpl(MethodImplOptions.AggressiveInlining)]
		//override public void MemPkAction(in MemPacketM memPk)
		//{
		//    PacketProcessM.SendMemPacket(memPk);
		//}

		/// <summary>
		/// Serialize 한 후에 패킷을 보내는 함수 - 패킷 즉시 보낼때만 사용         
		/// </summary>
		/// <param name="ePacketType"></param>
		/// <param name="sendData"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		override public void SerializeSendPacket(PACKET_TYPE ePacketType, byte[] sendData)
		{
			SendPacketM.SendPacket(Tc, Pid, ePacketType, sendData, _compEnc);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override void FlushSendBuffer()
		{
			if (_sendBufferLength == 0)
				return;

			byte[] dataToSend;

			dataToSend = ArrayPool<byte>.Shared.Rent(_sendBufferLength);
			Buffer.BlockCopy(_sendBuffer, 0, dataToSend, 0, _sendBufferLength);
			var dataToSendLenth = _sendBufferLength;
			_sendBufferLength = 0;


			if (Tc.Connected)
			{
				try
				{
					//NetworkStream netStream = Tc.GetStream();
					//netStream.WriteAsync(dataToSend, 0, dataToSendLenth).ConfigureAwait(false);

					var finalPkData = new FinalPkDataM(Tc, dataToSend, dataToSendLenth);
					SendPacketM.SendPacket(finalPkData);


				}
				catch (Exception e)
				{
					Debug.WriteLine($"Write stream 오류 ~~~ {e.Message}");
					//throw e;
				}
				finally
				{
					//GlobalM.arrayPool.Return(dataToSend);
				}
			}
			else
			{
				Debug.WriteLine("SendPacket(PacketM packet) 소켓이 이미 close ~~~ ");
				//throw new Exception("소켓이미 클로스~~");
			}

		}


		public InnerUserM(TcpClient tc)
		{
			Tc = tc;
		}

		public InnerUserM(TcpClient tc, string id, uint pid, long oid)
		{
			Tc = tc;
			Id = id;
			Pid = pid;
			Oid = oid;
		}

		
		public void RemoveTimer(TIMER_TYPE timerType)
		{
			dicTimer.RemoveTimer(timerType);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		// 종료 프로세스 함수들 
		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// 종료를 위해 IoPipe에서 호출하는 것을 제외하고 직접 호출 하지 않는다 -- 호출을 원하면 RequestDisconnectForce 사용
		/// IoPipe에서 Disconnection을 감지했을 때 --> User.DisconnectProcess --> ServerM.UserFinish
		/// 유저의 리소스를 정리 하는 
		/// </summary>
		/// <returns></returns>        
		public void DisconnectProcess()
		{
			ObserverState = USER_OBSERVER_STATE.DISCONNECTING;
			foreach (var observer in _observerList.ToArray())    // 디스 커넥트 사실을 알린다
			{
				if (_observerList.Contains(observer))
					observer.OnNext(this);
			}

			Debug.WriteLine($"모두 정리하고 유저 디스커넥트---- {Id}");
			Dispose();  //  유저 관련 리소스 모두 정리

		}


		// 유저가 접속 종료를 했을 때 call 되는 함수 --- 서버유저는 오버라이드 함수가 있으니 주의!!!!
		virtual public void RequestDisconnectForce() // 강제 종료하더라도 Fin에 의해서 IoPipeline에서 정상 종료 절차 밟음 - 확인할 것
		{
			try
			{
				var ns = Tc.GetStream();

				// 클라는 암호화 객체 해제 필요 없음 - 

				// Fin 안왔을 때 처리 클라는 안함
				// _dicTimerM.AddOrUpdateTimer(eTimerMType.DISCONNECT_USER_FORCE, new TimerM_User_Disconnect(this), TimeSpan.FromMilliseconds(NetworkM.gDisConnectForceWaitMiliSec), Timeout.InfiniteTimeSpan);
				// Fin 보냄            
				//ns.Socket.Shutdown(SocketShutdown.Send);
				Tc.Client.Shutdown(SocketShutdown.Send);

			}
			catch (Exception e)
			{
				Debug.WriteLine($"클라 클로즈 처리 예외 - RequestDisconnectForce{e.Message}");
			}
		}
		// 종료 프로세스 관련 함수 끝
		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////




		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// 옵져버 패턴 - 유저 disconnect 등을 옵저버에게 알리기 위해
		/// </summary>         
		private LinkedList<IObserver<InnerUserM>> _observerList = new LinkedList<IObserver<InnerUserM>>();

		public IDisposable Subscribe(IObserver<InnerUserM> observer)
		{
			if (observer != null && !_observerList.Contains(observer))
			{
				_observerList.AddLast(observer);
			}

			return new UnsubscriberM<InnerUserM>(_observerList, observer);
		}

		/// <summary>
		/// 옵저버들이게 알림
		/// </summary>
		public void NotifyObserversComplete()
		{
			foreach (var observer in _observerList.ToArray())  // 컨테이너가 수정되면 안되므로 ToArray()로 변경 - OnCompleted()에서 _observers를 변경함
			{
				if (_observerList.Contains(observer))
					observer.OnCompleted();
			}

			_observerList.Clear();
		}


		// 옵저버 패턴 끝
		/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

		/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Dispose 패턴 구현  - 명시적으로 Dispose() 부를때는 관리되는 리소스와 관리되지 않는 리소스 모두 삭제, 소멸자가 불릴때는 관리되지 않는 리소스만 삭제 패턴 (Dispose(false))
		/// </summary>
		private bool _disposed; // = false;
		~InnerUserM()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		// 유저 디스커넥트 했을 때 처리해야 되는 리소스 관련 함수들
		protected virtual void Dispose(bool disposing)
		{
			if (_disposed == true)
				return;

			if (disposing)
			{
				NotifyObserversComplete(); // 유저 옵저버 모두 정리                                

				dicTimer.DisposeAllTimer();  // 모든 타이머 해제

				_compEnc?.Dispose(); // Encrypt 객체 해제

			}

			// 관리되지 않는 리소스 해제
			_disposed = true;

			// 파생 클래스에서 호출 할 것
			// base.Dispose(disposing);
		}

	}
	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	// UserM 끝
	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

	public class UserM
	{
		protected InnerUserM _user;

		public UserM(InnerUserM userM)
		{
			_user = userM;
		}

		public uint Pid
		{
			get => (_user != null) ? _user.Pid : 0;
			set
			{
				if (_user != null)
					_user.Pid = value;
			}
		}           // packet id

		public TcpClient Tc
		{
			get => (_user != null) ? _user.Tc : null;
			set
			{
				if (_user != null)
					_user.Tc = value;
			}
		}
		public CancellationTokenSource Cts
		{
			get => (_user != null) ? _user.Cts : null;
			set
			{
				if (_user != null)
					_user.Cts = value;
			}
		}

		public long Oid
		{
			get => (_user != null) ? _user.Oid : 0;
			set
			{
				if (_user != null)
					_user.Oid = value;
			}
		}

		public string Id
		{
			get => (_user != null) ? _user.Id : string.Empty;
			set
			{
				if (_user != null)
					_user.Id = value;
				else
					_user.Id = string.Empty;
			}
		}

		//public string Pw
		//{
		//	get => (_user != null) ? _user.Pw : string.Empty;
		//	set
		//	{
		//		if (_user != null)
		//			_user.Pw = value;
		//	}
		//}

		// 암호화 key 
		public CompressAndEncryptM CompEnc
		{
			get => (_user != null) ? _user._compEnc : null;
		}

		public ALLOWED_PACKET_STATE AllowedPkState
		{
			get => (_user != null) ? _user.AllowedPkState : ALLOWED_PACKET_STATE.A_SC_ANY_STATE;
			set
			{
				if (_user != null)
					_user.AllowedPkState = value;
			}
		}
		public USER_OBSERVER_STATE ObserverState
		{
			get => (_user != null) ? _user.ObserverState : USER_OBSERVER_STATE.NORMAL;
			set
			{
				if (_user != null)
					_user.ObserverState = value;
			}
		}

		public bool IsExist { get => (_user != null) ? true : false; }


		/// <summary>
		/// Serialize 한 후에 패킷을 보내는 함수 
		/// </summary>
		/// <param name="ePacketType"></param>
		/// <param name="sendData"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void SerializeSendPacket(PACKET_TYPE ePacketType, byte[] sendData)
		{
			_user?.SerializeSendPacket(ePacketType, sendData);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteSendBuffer(PACKET_TYPE ePacketType, byte[] sendData)
		{
			_user?.WriteSendBuffer(ePacketType, sendData);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteSendBuffer(PACKET_TYPE ePacketType, ReadOnlySequence<byte> sendData)
		{
			_user?.WriteSendBuffer(ePacketType, sendData);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void FlushSendBuff()
		{
			if (_user == null)
				return;

			_user.FlushSendBuffer();
		}

		public void WritePkTimeNow() // 현재 시간 찍기
		{
			_user?.WritePkTimeNow();
		}

		public void RemoveTimer(TIMER_TYPE timerType)
		{
			_user?.RemoveTimer(timerType);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		// 종료 프로세스 함수들 
		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// 종료를 위해 IoPipe에서 호출하는 것을 제외하고 직접 호출 하지 않는다 -- 호출을 원하면 RequestDisconnectForce 사용
		/// IoPipe에서 Disconnection을 감지했을 때 --> User.DisconnectProcess --> ServerM.UserFinish
		/// 유저의 리소스를 정리 하는 
		/// </summary>
		/// <returns></returns>        
		public async ValueTask DisconnectProcess()
		{
			_user?.DisconnectProcess();
		}


		// 유저가 접속 종료를 했을 때 call 되는 함수 --- 서버유저는 오버라이드 함수가 있으니 주의!!!!
		public void RequestDisconnectForce() // 강제 종료하더라도 Fin에 의해서 IoPipeline에서 정상 종료 절차 밟음 - 확인할 것
		{
			_user?.RequestDisconnectForce();
		}
		// 종료 프로세스 관련 함수 끝
		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////




		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// 옵져버 패턴 - 유저 disconnect 등을 옵저버에게 알리기 위해
		/// 리턴값이 null일 수 있으니 반드시 체크 할 것
		/// </summary>         

		public IDisposable Subscribe(IObserver<InnerUserM> observer)
		{
			if (_user != null)
				return _user.Subscribe(observer);

			return null;
		}

		/// <summary>
		/// 옵저버들이게 알림
		/// </summary>
		public void NotifyObserversComplete()
		{
			_user?.NotifyObserversComplete();
		}


		// 옵저버 패턴 끝
		/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

		public void Dispose()
		{
			_user?.Dispose();
		}
	}



	// 옵저버가 참조하는 유저 스테이트
	public enum USER_OBSERVER_STATE
	{
		NORMAL = 1,
		DISCONNECTING,      // Disconnecting 될 때 설정됨
	}

}
