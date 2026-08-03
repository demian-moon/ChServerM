using EcsServerLibM;
using System.Diagnostics;
using System.Runtime.CompilerServices;


namespace EcsClientLibM
{
	public class ClientTimeM : TickTimeM
	{
		/// <summary>
		/// LOGIN_OK 패킷에 담겨온 서버에서 전달된 서버의 StopWatch.Frequency
		/// </summary>
		static public long gServerFrequency;

		/// <summary>
		/// 서버Frequency와 클라 Frequency의 차이에 따른 클라이언트 Tick의 가중치 값
		/// </summary>
		static public double gClientTickWeight;

		/// <summary>
		/// MilliSeconds를 서버틱으로 변환 
		/// </summary>
		/// <param name="ms"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long MsToServerTick(double ms)
		{
			return (long)(ms * (double)gServerFrequency / 1000.0);
		}


		/// <summary>
		/// 서버 tick을 MilliSeconds로 변환
		/// </summary>
		/// <param name="serverTick"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double ServerTickToMs(long serverTick)
		{
			if (serverTick <= 0)
				return 0;

			return serverTick * 1000.0 / (double)gServerFrequency;
		}

		/// <summary>
		/// 서버 GTick을 초로 변환
		/// </summary>
		/// <param name="serverTick"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double ServerTickToSec(long serverTick)
		{
			if (serverTick == 0)
				return 0;

			if (serverTick < 0)
			{
				Debug.Assert(false, "ServerTickToSec - tick 값이 음수임!!");
			}

			return serverTick / (double)gServerFrequency;
		}				
	}

}
