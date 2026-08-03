using EcsServerLibM;
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EcsClientLibM
{
	public class TimerM_ClaUser_Delay_Disconnect : ITimerActionM
	{
		ClientM _clientM;
		TcpClient _tc;

		int _dueTimeSec = 1;

		int exeTimes;

		public TimerM_ClaUser_Delay_Disconnect(ClientM clientM, TcpClient tc)
		{
			_clientM = clientM;
			_tc = tc;

		}

		public async Task DoAction()
		{
			exeTimes++;
			/////////////////////////////////////////////////////////////////////
			/// 로그인 Ok 처리 전에 종료되면 기다려서 처리
			/// /////////////////////////////////////////////////////////////////////
			UserM user = ClientM.GetUser(_tc);
			if (user.IsExist == true) // 
			{
				ClientM.gDisconnectTimer.RemoveTimer(_tc); // 타이머 지우기                

				user.DisconnectProcess();    // 서버유저가 가진 자원들 모두 지우기
				await _clientM.AppFinish(user).ConfigureAwait(false);

				ClientM.RemoveUser(_tc);    // dicSrvUsers에서  서버유저를 지움

				Debug.WriteLine($"###후처리로 로그인 OK 처리 하기도 전에 서버에서 Fin이 왔을 때 클라 처리 {user.Oid} ###");
			}
			else // 아직 LogIn 패킷 실행전이거나, 커넥션 후 아무것도 안하고 바로 접속 종료 한거면
			{
				if (exeTimes <= 10) // 로긴 실행전일 수 있으니 4번 다시 시도 
				{
					_dueTimeSec *= 2;
					ClientM.gDisconnectTimer.AddOrUpdateTimer(_tc, this, TimeSpan.FromSeconds(_dueTimeSec), Timeout.InfiniteTimeSpan);
				}
				else
				{
					ClientM.gDisconnectTimer.RemoveTimer(_tc); // 타이머 지우기                
					Debug.WriteLine($"------------------클라 대박사건 {user.Oid}--------------------");
				}
			}
		}
	}
}
