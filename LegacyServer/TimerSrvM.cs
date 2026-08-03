using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	// 서버 타이머 액션
	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 로그인 처리 하기도 전에 클라에서 Fin이 왔을 때 처리 
	/// </summary>
	public class TimerM_SrvUser_Delay_Disconnect : ITimerActionM
	{
		ServerM _serverM;
		TcpClient _tc;

		int _dueTimeSec = 1;   // 실행 지연시간

		int _exeTimes;

		public TimerM_SrvUser_Delay_Disconnect(ServerM serverM, TcpClient tc)
		{
			_serverM = serverM;
			_tc = tc;

		}

		public async Task DoAction()
		{
			_exeTimes++;

			///////////////////////////////////////////////////////////////////////////////////
			// 아직 LogIn 패킷 실행전이거나, 커넥션 후 아무것도 안하고 바로 접속 종료 한거면 등록될 때 까지 대기
			///////////////////////////////////////////////////////////////////////////////////
			SrvUserM srvUser = SrvGlobal.GetUser(_tc);
			if (srvUser.IsExist == true) // 
			{
				_serverM.DecrementServerUserCnt(); // 서버 유저 숫자 줄이기
				ServerM.gDisconnectTimer.RemoveTimer(_tc); // 타이머 지우기				

				srvUser.DisconnectProcess();    // 서버유저가 가진 자원들 모두 지우기
				_serverM.AppUserFinish(srvUser);   // 앱에서 앱유저 지우고, 게임중이었다면 관련 리소스 해제
				SrvGlobal.RemoveUser(_tc);    // dicSrvUsers에서  서버유저를 지움

				Debug.WriteLine($"###후처리로 로그인 처리 하기도 전에 클라에서 Fin이 왔을 때 처리 {srvUser.Oid} ###");
			}
			else
			{
				if (_exeTimes <= 10) // 로긴 실행전일 수 있으니 4번 다시 시도 
				{
					_dueTimeSec *= 2;
					ServerM.gDisconnectTimer.AddOrUpdateTimer(_tc, this, TimeSpan.FromSeconds(_dueTimeSec), Timeout.InfiniteTimeSpan);
				}
				else
				{
					ServerM.gDisconnectTimer.RemoveTimer(_tc); // 타이머 지우기
					Debug.WriteLine($"------------------대박사건(SrvUser:{srvUser.Oid})--------------------");
				}
			}
		}
	}



	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////    
	/// <summary>
	/// 서버 타이머 관련 

	///
	/// 허트 비트 관련
	/// </summary>
	public class TimerM_HeartBitSend : ITimerActionM
	{
		InnerUserM _u;
		public TimerM_HeartBitSend(InnerUserM u)
		{
			_u = u;
		}

		public async Task DoAction()
		{
			_u.WriteSendBuffer(PACKET_TYPE.PC_HEART_BIT, null);    // 허트 비트 보내기 - 3초안에 답변 안오면 끊기
																   //_u._dicTimerM.AddOrUpdateTimer(eTimerMType.HEART_BIT_ALIVE_CHECK, new TimerM_HeartBitCheck(_u), TimeSpan.FromMilliseconds(3000), Timeout.InfiniteTimeSpan);

		}
	}


	// 허트비트 체크 
	public class TimerM_HeartBitCheck : ITimerActionM
	{
		InnerUserM _u;
		public TimerM_HeartBitCheck(InnerUserM u)
		{
			_u = u;
		}

		public async Task DoAction()
		{
			_u.RequestDisconnectForce();
		}
	}

	/// <summary>
	/// 서버 틱 값 관련
	/// </summary>
	//public class Timer_ServerTickSend : ITimerActionM    
	//{
	//    MembersM<TangUser> _members;
	//    public Timer_ServerTickSend(MembersM members)
	//    {
	//        _members = members;
	//    }

	//    static long k;
	//    public async Task DoAction()
	//    {   
	//        FsServerTickFactory stf = new (NetworkGlobalVariableM.GTick);
	//        var data = stf.Serialize();                            

	//        _membersForPk.SendPacketToMembers(PACKET_TYPE.PC_SERVER_TICK, data);



	//    }
	//}

}
