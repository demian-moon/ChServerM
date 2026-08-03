using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EcsServerLibM
{

	// 클라 서버 공용 전역 함수
	public class GlobalM
	{

				
		// 스크린 사이즈
		static public int screenWidth;
		static public int screenHeight;
		static public int screenHalfWidth;
		static public int screenHalfHeight;



		// 몬스터, Player등 게임 오브젝트 Oid;
		static private long gameOid;

		static public long MakeGameOid()
		{
			return Interlocked.Increment(ref gameOid);
		}
	}

	/// <summary>
	/// 클라 또는 서버가 보낸 암호키를 Dictionary에 저장 후 유저생성이 완료되면 전달해 주는 객체
	/// Tc가 key이므로 접속이 종료되면 Dictionary에서 Remove해야 됨
	/// </summary>
	static class CompressAndEncryptManM
	{
		// 유저들 정보 
		static public ConcurrentDictionary<TcpClient, CompressAndEncryptM> dicCompEncrypt = new();


		static public bool TryAdd(TcpClient tc, CompressAndEncryptM compEnc)
		{
			return dicCompEncrypt.TryAdd(tc, compEnc);
		}

		static public bool TryRemove(TcpClient tc, out CompressAndEncryptM compEnc)
		{
			return dicCompEncrypt.TryRemove(tc, out compEnc);
		}

		static public bool TryGetValue(TcpClient tc, out CompressAndEncryptM compEnc)
		{
			return dicCompEncrypt.TryGetValue(tc, out compEnc);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public bool IsReadyCompEnc(TcpClient tc)
		{
			if(dicCompEncrypt.TryGetValue(tc, out var compEnc) )
			{
				return compEnc.IsReady();
			}
			else
			{
				return false; // 등록이 안되어 있으면 false
			}
		}

	}

}
