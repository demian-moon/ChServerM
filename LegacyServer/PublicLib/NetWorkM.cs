using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{

	/// <summary>
	/// 클라-서버 공용 자원들
	/// </summary>
	/// 

	public abstract class AbNetworkBase
	{
		
		/// <summary>
		///  Disconnection을 처리하기 위한 타이머
		/// </summary>
		static public TimerM<TcpClient> gDisconnectTimer;

		/// <summary>
		/// 클라이언트와 서버가 공유하는 유니크 코드(같아야만 접속 된다)
		/// </summary>
		static public uint uniqueProgramNumber; // 앱 프로그램 넘버 (pid에 담아 보내는데 FlatBuffer 0은 기록을 안해서 헤더 사이즈가 달라짐 : 쓰면 안됨!!!!)       

		// AllowedPacketMan 관련
		static protected AllowedPacketMan _allowedPkMan;
		static public bool IsAllowedPacket(UserM user, PACKET_TYPE curPkType)
		{
			return _allowedPkMan.IsAllowed(user.AllowedPkState, curPkType);
		}

		static public bool IsAllowedPacketNotLogined(PACKET_TYPE curPkType)
		{
			return _allowedPkMan.IsAllowed(ALLOWED_PACKET_STATE.A_SC_NOT_LOGINED, curPkType);
		}

		// 패킷 디스패쳐 관련
		protected MemPkDispatcher _memPkDispatcher;

		public CancellationTokenSource _cts;

		public AbNetworkBase()
		{
			_cts = new CancellationTokenSource();			
			gDisconnectTimer = new TimerM<TcpClient>();
		}

		/// <summary>
		/// 자기 자신 IP 얻기
		/// </summary>
		/// <returns></returns>
		static public string GetMyIP()
		{
			IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
			string ipv4 = string.Empty;
			for (int i = 0; i < host.AddressList.Length; i++)
			{
				if (host.AddressList[i].AddressFamily == AddressFamily.InterNetwork)
				{
					ipv4 = host.AddressList[i].ToString();
					break;
				}
			}
			return ipv4;
		}

		// TCP KeepAlive 설정 함수
		// - socket: 설정할 클라이언트의 소켓
		// - on: KeepAlive 기능 활성화 여부 (true = 활성화, false = 비활성화)
		// - keepAliveTime: 처음 KeepAlive 패킷을 보내기까지의 대기 시간 (밀리초)
		// - keepAliveInterval: KeepAlive 패킷을 재전송하는 간격 (밀리초)
		static public void SetKeepAlive(Socket socket, bool on, int keepAliveTime, int keepAliveInterval)
		{
			// KeepAlive 옵션 설정 값을 담기 위한 배열 (on/off + keepAliveTime + keepAliveInterval)
			// 배열의 크기: 각 옵션은 uint(4바이트)로 구성, 총 3개의 옵션 값이 필요하므로 배열의 크기는 12바이트.
			int size = sizeof(uint);
			byte[] inOptionValues = new byte[size * 3]; // 배열 [on/off(4바이트), keepAliveTime(4바이트), keepAliveInterval(4바이트)]

			// 첫 번째 4바이트: KeepAlive 기능을 활성화할지 여부 (1 = 활성화, 0 = 비활성화)
			BitConverter.GetBytes((uint)(on ? 1 : 0)).CopyTo(inOptionValues, 0); // 기능 on/off 설정

			// 두 번째 4바이트: KeepAlive 패킷을 보내기까지 대기하는 시간 (밀리초)
			// 서버가 이 시간 동안 아무런 패킷을 받지 않으면 KeepAlive 패킷을 처음으로 전송
			BitConverter.GetBytes((uint)keepAliveTime).CopyTo(inOptionValues, size); // KeepAlive 시간 설정

			// 세 번째 4바이트: KeepAlive 패킷을 재전송하는 주기 (밀리초)
			// KeepAlive 패킷 전송 후에도 응답이 없으면 이 주기마다 재전송
			BitConverter.GetBytes((uint)keepAliveInterval).CopyTo(inOptionValues, size * 2); // KeepAlive 재전송 주기 설정

			// 소켓에 KeepAlive 옵션을 설정
			// IOControl 메서드는 소켓의 저수준 옵션을 제어할 수 있는 메서드로, KeepAlive 옵션을 커스텀 값으로 설정
			socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
		}
	}

}