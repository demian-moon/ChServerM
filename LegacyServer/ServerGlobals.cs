using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	/// <summary>
	/// 서버 전용 자원들
	/// </summary>
	static public class SrvGlobal
	{

		////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ServerConfig.mt를 사용해서 설정하는 변수들
		/// </summary>
		/// 
		static public void SetSrvGloalVariable(SrvTableM srvTable)
		{
			//////////////////////////////////////////////////////////////////////////////////
			// 공용 글로벌 변수 설정 - clientSetting 테이블
			//////////////////////////////////////////////////////////////////////////////////
			// 스크린 사이즈 
			if (srvTable.clientSettings.DataExist("screenResolution"))  // 서버 disconnection 기다리는 시간
			{
				GlobalM.screenWidth = int.Parse(srvTable.clientSettings.GetData("screenResolution", 1));
				GlobalM.screenHeight = int.Parse(srvTable.clientSettings.GetData("screenResolution", 2));

				GlobalM.screenHalfWidth = GlobalM.screenWidth / 2;
				GlobalM.screenHalfHeight = GlobalM.screenHeight / 2;
			}
			else
			{
				Debug.Assert(false, $"ClientSettings.mt - screenResolution 값 없음");
			}

			//////////////////////////////////////////////////////////////////////////////////
			// 서버 글로벌 변수 설정 - ServerConfig 테이블
			//////////////////////////////////////////////////////////////////////////////////

			if (srvTable.serverConfig.DataExist("netWorkDelayM_IQR_WindowSize"))  // 네트워크 딜레이 IQR 윈도우 사이즈
			{
				netWorkDelayM_IQR_WindowSize = srvTable.serverConfig.GetDataInteger("netWorkDelayM_IQR_WindowSize", 1);
			}


			if (srvTable.serverConfig.DataExist("disConnectForceWaitMs"))  // 서버 disconnection 기다리는 시간
			{
				disConnectForceWaitMs = srvTable.serverConfig.GetDataInteger("disConnectForceWaitMs", 1);
			}
			else
			{
				Debug.Assert(false, $"ServerConfig.mt - disConnectForceWaitMs 값 없음");
			}

			if (srvTable.serverConfig.DataExist("srvUpdateDeltaMs"))
			{
				srvUpdateDeltaMs = srvTable.serverConfig.GetDataInteger("srvUpdateDeltaMs", 1); // 서버 전체 스크립트 srvUpdateDeltaMs 값
			}
			else
			{
				Debug.Assert(false, $"ServerConfig.mt - srvUpdateDeltaMs 값 없음");
			}

			if (srvTable.serverConfig.DataExist("srvFixedUpdateDeltaMs"))
			{
				srvFixedUpdateDeltaMs = srvTable.serverConfig.GetDataInteger("srvFixedUpdateDeltaMs", 1); // 서버 전체 스크립트 srvFixedUpdateDeltaMs 값
			}
			else
			{
				Debug.Assert(false, $"ServerConfig.mt - srvFixedUpdateDeltaMs 값 없음");
			}

			if (srvTable.serverConfig.DataExist("outGoingActBlockFactor"))
			{
				cntOutGoingPkActBlock = Math.Max(1, (int)(Environment.ProcessorCount * float.Parse(srvTable.serverConfig.GetData("outGoingActBlockFactor", 1))));
			}
			else
			{
				Debug.Assert(false, $"ServerConfig.mt - outGoingActBlockFactor 값 없음");
			}

			if (srvTable.serverConfig.DataExist("incomeActBlockFactor"))
			{
				cntIncommingPkActBlock = Math.Max(1, (int)(Environment.ProcessorCount * float.Parse(srvTable.serverConfig.GetData("incomeActBlockFactor", 1))));
			}
			else
			{
				Debug.Assert(false, $"ServerConfig.mt - incomeActBlockFactor 값 없음");
			}
		}


		/// <summary>
		/// 몽고DB 사용시 서버 유저 인증용 몽고DB 컬렉션 이름
		/// </summary>
		static public string gSrvUserAuthTableName = "SrvUserAuth"; // 서버 유저 인증용 몽고DB 컬렉션 이름

		static public string gDbConnectionString = "mongodb://smck:smck4@localhost:27017"; // 몽고DB 연결 문자열

		/// <summary>
		/// 네트워크 딜레이 IQR 윈도우 사이즈 
		/// </summary>
		static public int netWorkDelayM_IQR_WindowSize;

		// 스크립트 FixedUdateDeltaMs - 실행 간격 ms (전체 서버 script 적용-맵은 따로 있음)
		static public double srvFixedUpdateDeltaMs;

		static public double srvUpdateDeltaMs;

		// 서버에서 outgoing 패킷 처리에 사용할 ActionBlock 개수 Factor (프로세서 개수 * Factor)
		static public int cntOutGoingPkActBlock;

		// 서버에서 income 패킷(memPk) 처리에 사용할 ActionBlock 개수 Factor (프로세서 개수 * Factor)
		static public int cntIncommingPkActBlock;



		// 서버 옵션들
		//static public bool bHeartBitAction = true;
		//static public TimeSpan heartBitDue = TimeSpan.FromMilliseconds(10000);  // 10초 후부터
		//static public TimeSpan heartBitPeriod = TimeSpan.FromMilliseconds(10000);   // 10초 간격
		static public int disConnectForceWaitMs;

		////////////////////////////////////////////////////////////////////////////////////////////////



		// 유저들 정보 
		static public ConcurrentDictionary<TcpClient, InnerSrvUserM> dicSrvUsers = new();

		/// <summary>
		/// 서버 유저 얻기 라이브러리 내에서만 쓴다. (internal 설정)
		/// 상속받은 앱서버에서는 실제 서버유저가 언제 dicSrvUsers에서 삭제되었는지 모르기 때문에
		/// </summary>
		/// <param name="tc"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static internal SrvUserM GetUser(TcpClient tc)
		{
			InnerSrvUserM srvUser;
			dicSrvUsers.TryGetValue(tc, out srvUser);

			return new SrvUserM(srvUser);
		}

		static public bool ExistUser(TcpClient tc)
		{
			return dicSrvUsers.ContainsKey(tc);
		}

		static public void AddUser(TcpClient tc, InnerSrvUserM srvUser)
		{
			if (dicSrvUsers.TryAdd(tc, srvUser) == false)
			{
				Debug.WriteLine($"버그M: 서버 유저추가 안됨: tc가 같음:{srvUser.Id}");
			}
		}

		static public InnerSrvUserM RemoveUser(TcpClient tc)
		{
			InnerSrvUserM srvUser;
			if (dicSrvUsers.TryRemove(tc, out srvUser) == false)
			{

				Debug.WriteLine($"워닝M: 서버 유저없는데 지우려고함:");
			}
			return srvUser;
		}


		// 서버 모든 유저들에게 동시 전송
		static async public Task SendPacketToAllUsers(PACKET_TYPE packetType, byte[] data, long oidExceptUser = 0)
		{
			if (dicSrvUsers.Count > 0)
			{
				Parallel.ForEach(dicSrvUsers.Values, srvUser =>
				{
					if (srvUser.Oid == oidExceptUser)
						return;

					srvUser.SerializeSendPacket(packetType, data);
				});
			}
		}


	}


}
