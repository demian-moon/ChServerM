using MongoDB.Bson;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;


namespace EcsServerLibM
{

	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 서버 유저M
	/// </summary>
	public class InnerSrvUserM : InnerUserM
	{

		public bool MetaDataDownloadOk { get; set; } = false;
		bool _disposed; // = false;
		
		public ObjectId DB_ID { get; set; }

		public NetWorkDelayM netDelay;  // 테트워크 딜레이 처리기

		public InnerSrvUserM(TcpClient tc) : base(tc)
		{
			netDelay = new NetWorkDelayM(SrvGlobal.netWorkDelayM_IQR_WindowSize);   // 100개             
		}

		/// <summary>
		/// Serialize 한 후에 패킷을 보내는 함수 - 패킷 즉시 보낼때만 사용
		/// </summary>
		/// <param name="ePacketType"></param>
		/// <param name="sendData"></param>
		/// <returns></returns>

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		override public void SerializeSendPacket(PACKET_TYPE ePacketType, byte[] sendData)
		{
			SendPacketGroupM.SendPacket(Oid, Tc, Pid, ePacketType, sendData, _compEnc);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override void FlushSendBuffer()
		{
			byte[] dataToSend;


			if (_sendBufferLength == 0)
				return;

			dataToSend = ArrayPool<byte>.Shared.Rent(_sendBufferLength);
			Buffer.BlockCopy(_sendBuffer, 0, dataToSend, 0, _sendBufferLength);
			var dataToSendLenth = _sendBufferLength;
			_sendBufferLength = 0;


			if (Tc.Connected)
			{
				try
				{
					var finalPkData = new FinalPkDataM(Tc, dataToSend, dataToSendLenth);
					SendPacketGroupM.SendPacket(Oid, finalPkData);
				}
				catch (Exception e)
				{
					Debug.WriteLine($"Write stream 오류 ~~~ {e.Message}");
					//throw e;
				}
				finally
				{

				}
			}
			else
			{
				Debug.WriteLine("SendPacket(PacketM packet) 소켓이 이미 close ~~~ ");
				//throw new Exception("소켓이미 클로스~~");
			}

		}


		~InnerSrvUserM()
		{
			Dispose(false);
		}

		override protected void Dispose(bool disposing)
		{
			if (_disposed == true)
				return;

			if (disposing)
			{
				// 관리되는 오브젝트 정리                
			}

			// 관리되지 않는 메모리 정리

			_disposed = true;
			base.Dispose(disposing);
		}

		// 이걸로 강제 종료 하는 게 맞음 (서버유저)
		override public void RequestDisconnectForce()   // 강제 종료하더라도 Fin에 의해서 IoPipeline에서 정상 종료 절차 밟음 - 확인할 것
		{
			try
			{
				var ns = Tc.GetStream();

				// 강제 종료 타이머 설정
				dicTimer.AddOrUpdateTimer(TIMER_TYPE.DISCONNECT_USER_FORCE, new TimerM_User_Disconnect_Force(this), TimeSpan.FromMilliseconds(SrvGlobal.disConnectForceWaitMs), Timeout.InfiniteTimeSpan);

				// Fin 보냄            
				//ns.Socket.Shutdown(SocketShutdown.Send);
				Tc.Client.Shutdown(SocketShutdown.Send);

			}
			catch (Exception e)
			{
				Debug.WriteLine($"서버 클로즈 처리 - RequestDisconnectForce- Fin보냄{e.Message}");
			}
		}
	}

	public class SrvUserM : UserM
	{
		public SrvUserM(InnerSrvUserM u) : base(u) { }

		public ObjectId DB_ID
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => (_user != null) ? (_user as InnerSrvUserM).DB_ID : ObjectId.Empty;
			set
			{
				if (_user != null)
				{
					var srvUser = _user as InnerSrvUserM;
					srvUser.DB_ID = value;
				}
			}
		}

		public NetWorkDelayM netDelay
		{
			get => (_user != null) ? (_user as InnerSrvUserM).netDelay : null;
		}
		public bool MetaDataDownloadOk
		{
			get => (_user != null) ? (_user as InnerSrvUserM).MetaDataDownloadOk : false;
			set
			{
				if (_user != null)
				{
					var srvUser = _user as InnerSrvUserM;
					srvUser.MetaDataDownloadOk = value;
				}
			}
		}
	}

}
