using FbsClassM;
using Google.FlatBuffers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


namespace EcsServerLibM
{


	//public enum TM_PACKET_TYPE : ushort
	//{
	//    /////////////////////////////////////////////////////
	//    /*server --> client     1 ~ 20000 사용*/
	//    ACK_CHAT = 1,
	//    ACK_ROOM_LIST,
	//    ACK_JOIN_ROOM,
	//    ACK_ROOM_USER_LIST,

	//    /*client --> server     20001 ~ 40000 사용*/
	//    RQ_CHAT,
	//    RQ_JOIN_ROOM,
	//    CREATE_ROOM,
	//    RQ_ROOM_LIST,
	//    RQ_ROOM_EXIT,
	//}
	// 패킷 타입
	public enum PACKET_TYPE : ushort
	{
		NOT_USED = 0,  // --> 사용하지 않는다 Flatbuffer 때문에 사용시 헤더 사이즈 달라짐
					   /////////////////////////////////////////////////////

		///// 공용 (서버, 클라) /////
		PSC_RQ_DISCONNECT = 40001,          // 상속받은 사용자 Client, Server에서 1~40000까지 사용
		PSC_COMP_ENC_CHANGE,                 // 압축, 암호화 KEY
		PSC_RSA,

		///// server --> client /////
		PC_VERSION_CHECK_RESULT,
		PC_LOGIN_OK,
		PC_HEART_BIT,

		PC_PROGRESS_BAR,    // 프로그레스 바 업데이트 (서버에서 클라로 보내는 것)


		// 요청 및 응답
		PC_SERVER_TICK,         // 서버 tick값
		PS_RSP_SERVER_TICK,


		///// client --> server /////        
		PS_VERSION_CHECK,
		PS_LOGIN,
		PS_LOGIN_FIN,   // LOIN_OK 받은후 
		PS_LOGOUT,
		PS_HEART_BIT_ALIVE,    // HEART_BIT 받은후
	}

	/// <summary>
	/// 최종 NetworkStream WriteAsync 전송 Data
	/// </summary>
	public struct FinalPkDataM : IUIThreadCheck
	{
		public TcpClient _tc;
		public byte[] _pkData;
		public int _sendPkDataLength;

		/// <summary>
		/// NetworkStream.WriteAsync를 위한 클래스 (ActionBlock에서 매개변수로 사용 됨)
		/// </summary>
		/// <param name="tc"></param>
		/// <param name="pkData">ArrayPool에서 Rent된 전송할 byte [] </param>
		/// <param name="sendPkDataLength">실제 전송 데이터 길이 (pkData가 Rent한 것이므로 사이즈가 다름!!!!)</param>
		public FinalPkDataM(TcpClient tc, byte[] pkData, int sendPkDataLength)
		{
			_tc = tc;
			_pkData = pkData;
			_sendPkDataLength = sendPkDataLength;
		}

		public bool IsUIThread()
		{
			return false;
		}
	}


	/// <summary>
	/// 패킷 클래스 PacketM
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct PacketM
	{

		public static ushort gPkHeadLen = 28;             // 패킷 헤더 길이
		public static ushort gConHeadLen = 24;       // 콘텐츠 헤더 길이
		public static ushort gEncHeadLen = 32;      // 압축, 암호화 헤더 길이

		private TcpClient _tc;
		private uint _pid;
		private ushort _packetType;
		private byte[] _conData;

		public CompressAndEncryptM CompEnc { get; set; }

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PacketM(TcpClient tc, uint pid, ushort packetType, byte[] sendData, CompressAndEncryptM compEnc = null)
		{
			Tc = tc;
			Pid = pid;
			PacketType = packetType;
			ConData = sendData;
			CompEnc = compEnc;
		}

		public TcpClient Tc { get => _tc; set => _tc = value; }
		public uint Pid { get => _pid; set => _pid = value; }
		public ushort PacketType { get => _packetType; set { if (value == 0) Debug.WriteLine("0번은 예약된 번호입니다 (feat. Flatbuffer)"); _packetType = value; } }
		public byte[] ConData { get => _conData; set => _conData = value; }


		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// PkHeadM 관련 
		/// </summary>
		/// <param name="bytePkHeadM"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static FbsPkHeadM DeserializePkHead(byte[] bytePkHead)
		{
			return FbsPkHeadM.GetRootAsFbsPkHeadM(new ByteBuffer(bytePkHead));
		}


		/// <summary>
		/// 체크 섬 계산
		/// </summary>
		/// <returns></returns
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public bool IsValidCheckSum(FbsPkHeadM fbsPkHeadM)
		{
			return true;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ContentHead 관련
		/// </summary>
		/// <param name="byteContentHead"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static FbsContentHeadM DeserializeContentHead(byte[] byteContentHead)
		{
			return FbsContentHeadM.GetRootAsFbsContentHeadM(new ByteBuffer(byteContentHead));
		}


		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Send Packet : 직접 호출 하지 않는다. user.SerializeSendPacket을 호출 할 것
		/// </summary>
		/// <param name="packet"></param>
		/// <returns></returns>        
		static public async Task SendPacket(FinalPkDataM pkData)
		{
			var tc = pkData._tc;			
			var finalData = pkData._pkData;
			var fianlDataLenth = pkData._sendPkDataLength;  // 실제 전송 데이터가 다름

			if (tc.Connected == false)
			{
				ArrayPool<byte>.Shared.Return(finalData);
				return;
			}

			if (tc.Connected)
			{
				try
				{
					NetworkStream netStream = tc.GetStream();
					await netStream.WriteAsync(finalData, 0, fianlDataLenth).ConfigureAwait(false); // finalData.Length를 사용하면 안됨 실제 전송할 데이터 길이가 다름
				}
				catch (Exception e)
				{
					Debug.WriteLine($"Write stream 오류 ~~~ {e.Message}");
					//throw e;
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(finalData);    // finalData는 ArrayPool에서 Rent 된 Byte [] 임
				}
			}
			else
			{
				Debug.WriteLine("SendPacket(PacketM packet) 소켓이 이미 close ~~~ ");
				//throw new Exception("소켓이미 클로스~~");
			}

		}


		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// NetStream.AsyncWrite에 실제 보낼 byte[] 데이터를 만드는 함수 
		/// </summary>
		/// <param name="packet"></param>
		/// <returns></returns>        
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public bool TryMakeSendPacketData(in PacketM packet, out FinalPkDataM pkData)
		{
			if (packet.Tc.Connected == false)
			{
				pkData = default(FinalPkDataM);
				return false;
			}

			var pid = packet.Pid;
			var tc = packet.Tc;
			var ePacketType = packet.PacketType;
			var sendData = packet.ConData;
			var compEnc = packet.CompEnc;

			var lengthSendData = 0;
			var fbb = new FlatBufferBuilder(64);

			// 헤더 만들기
			var osPkHead = FbsPkHeadM.CreateFbsPkHeadM(fbb, pid, PacketM.gConHeadLen, 1); // 첵섬 1
			fbb.Finish(osPkHead.Value);

			var byteHeader = fbb.SizedByteArray();
			var lengthHeader = byteHeader.Length;



			// 콘텐츠 헤더 만들기
			fbb = new FlatBufferBuilder(64);
			int sendDataLen = 0;
			if (sendData != null)
				sendDataLen = sendData.Length;
			var osConHead = FbsContentHeadM.CreateFbsContentHeadM(fbb, ePacketType, sendDataLen);
			fbb.Finish(osConHead.Value);

			var byteConHeader = fbb.SizedByteArray();
			var lengthConHeader = byteConHeader.Length;

			// 전송 데이터 만들기            
			byte[] combinePk;
			int lengthCombinePk = lengthHeader + lengthConHeader;
			if (sendData != null)    // 데이터가 있으면 보낸다           
				lengthSendData = sendData.Length;

			lengthCombinePk += lengthSendData;
			combinePk = ArrayPool<byte>.Shared.Rent(lengthCombinePk);

			Buffer.BlockCopy(byteHeader, 0, combinePk, 0, lengthHeader);
			Buffer.BlockCopy(byteConHeader, 0, combinePk, lengthHeader, lengthConHeader);
			if (sendData != null)
				Buffer.BlockCopy(sendData, 0, combinePk, lengthHeader + lengthConHeader, lengthSendData);


			byte[] finalData = combinePk;
			int finalDataLen = lengthCombinePk;

			if (compEnc != null) // 인크립터 설정되어 있으면 압축, 암호화 해서 보냄
			{
				bool isCompress = compEnc.Compress(combinePk, lengthCombinePk, out byte[] byteComp); // 압축 먼저

				var byteEncrypt = compEnc.Encrypt(byteComp);   // 클라는 Aes, 서버는 Xor 암호화                
				ArrayPool<byte>.Shared.Return(combinePk);    // 리소스 반환 // 순서 중요 (Compress 안됐을 수 있으니 Encrypt 후에 불러야 됨)

				int lengthByteEcrypt = byteEncrypt.Length;          // Encrypt 한 후 사이즈

				if (isCompress == true)
				{
					compEnc.ReturnPoolAfterCompress(byteComp); // arrayPool 리소스 리턴     
				}

				// 압축 암호화 헤더 만들기
				fbb = new FlatBufferBuilder(64);
				var osEncryptHeader = FbsEncryptHeadM.CreateFbsEncryptHeadM(fbb, (sbyte)(isCompress ? 1 : 0), lengthByteEcrypt, lengthCombinePk);
				fbb.Finish(osEncryptHeader.Value);

				var byteEncryptHeader = fbb.SizedByteArray(); // 압축 헤더

				int lengthEncryptHeader = byteEncryptHeader.Length;

				int lengthEncryptPk = lengthEncryptHeader + lengthByteEcrypt;
				byte[] encryptPk = ArrayPool<byte>.Shared.Rent(lengthEncryptPk); // 어레이 풀 렌트

				Buffer.BlockCopy(byteEncryptHeader, 0, encryptPk, 0, lengthEncryptHeader);
				Buffer.BlockCopy(byteEncrypt, 0, encryptPk, lengthEncryptHeader, lengthByteEcrypt);

				finalData = encryptPk;
				finalDataLen = lengthEncryptPk;
			}

			pkData = new FinalPkDataM(tc, finalData, finalDataLen);
			return true;

		}

		/// <summary>
		/// NetStream 전송 데이터를 만드는 함수 
		/// 만들어진 pkData는 ArrayPool Rent 했기 때문에 반드시 Return이 필요함. PacketM.SendPacket 내부에서 리턴 함
		/// </summary>
		/// <param name="tc"></param>
		/// <param name="pid"></param>
		/// <param name="ePacketType"></param>
		/// <param name="sendData"></param>
		/// <param name="compEnc"></param>
		/// <param name="pkData">NetStream 전송 데이터 </param>
		/// <returns>데이터 만들수 있으면 true </returns>

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public bool TryMakeSendPacketData(TcpClient tc, uint pid, PACKET_TYPE ePacketType, byte[] sendData, CompressAndEncryptM compEnc, out FinalPkDataM pkData)
		{
			if (tc.Connected == false)
			{
				pkData = default(FinalPkDataM);
				return false;
			}

			var lengthSendData = 0;
			var fbb = new FlatBufferBuilder(64);

			// 헤더 만들기
			var osPkHead = FbsPkHeadM.CreateFbsPkHeadM(fbb, pid, PacketM.gConHeadLen, 1); // 첵섬 1
			fbb.Finish(osPkHead.Value);

			var byteHeader = fbb.SizedByteArray();
			var lengthHeader = byteHeader.Length;



			// 콘텐츠 헤더 만들기
			fbb = new FlatBufferBuilder(64);
			int sendDataLen = 0;
			if (sendData != null)
				sendDataLen = sendData.Length;
			var osConHead = FbsContentHeadM.CreateFbsContentHeadM(fbb, (ushort)ePacketType, sendDataLen);
			fbb.Finish(osConHead.Value);

			var byteConHeader = fbb.SizedByteArray();
			var lengthConHeader = byteConHeader.Length;

			// 전송 데이터 만들기            
			byte[] combinePk;
			int lengthCombinePk = lengthHeader + lengthConHeader;
			if (sendData != null)    // 데이터가 있으면 보낸다           
				lengthSendData = sendData.Length;

			lengthCombinePk += lengthSendData;
			combinePk = ArrayPool<byte>.Shared.Rent(lengthCombinePk);

			Buffer.BlockCopy(byteHeader, 0, combinePk, 0, lengthHeader);
			Buffer.BlockCopy(byteConHeader, 0, combinePk, lengthHeader, lengthConHeader);
			if (sendData != null)
				Buffer.BlockCopy(sendData, 0, combinePk, lengthHeader + lengthConHeader, lengthSendData);


			byte[] finalData = combinePk;
			int finalDataLen = lengthCombinePk;

			if (compEnc != null) // 인크립터 설정되어 있으면 압축, 암호화 해서 보냄
			{
				bool isCompress = compEnc.Compress(combinePk, lengthCombinePk, out byte[] byteComp); // 압축 먼저

				var byteEncrypt = compEnc.Encrypt(byteComp);   // 클라는 Aes, 서버는 Xor 암호화                
				ArrayPool<byte>.Shared.Return(combinePk);    // 리소스 반환 // 순서 중요 (Compress 안됐을 수 있으니 Encrypt 후에 불러야 됨)

				int lengthByteEcrypt = byteEncrypt.Length;          // Encrypt 한 후 사이즈

				if (isCompress == true)
				{
					compEnc.ReturnPoolAfterCompress(byteComp); // arrayPool 리소스 리턴     
				}

				// 압축 암호화 헤더 만들기
				fbb = new FlatBufferBuilder(64);
				var osEncryptHeader = FbsEncryptHeadM.CreateFbsEncryptHeadM(fbb, (sbyte)(isCompress ? 1 : 0), lengthByteEcrypt, lengthCombinePk);
				fbb.Finish(osEncryptHeader.Value);

				var byteEncryptHeader = fbb.SizedByteArray(); // 압축 헤더

				int lengthEncryptHeader = byteEncryptHeader.Length;

				int lengthEncryptPk = lengthEncryptHeader + lengthByteEcrypt;
				byte[] encryptPk = ArrayPool<byte>.Shared.Rent(lengthEncryptPk); // 어레이 풀 렌트

				Buffer.BlockCopy(byteEncryptHeader, 0, encryptPk, 0, lengthEncryptHeader);
				Buffer.BlockCopy(byteEncrypt, 0, encryptPk, lengthEncryptHeader, lengthByteEcrypt);

				finalData = encryptPk;
				finalDataLen = lengthEncryptPk;
			}

			pkData = new FinalPkDataM(tc, finalData, finalDataLen);
			return true;

		}

		/// <summary>
		/// 로그인 관련 
		/// </summary>
		/// <param name="id"></param>
		/// <param name="pw"></param>
		/// <returns></returns>        
		static public byte[] SerializeLoginIdPw(string id, string pw, int clientVersion)
		{
			var fbb = new FlatBufferBuilder(128);
			var osId = fbb.CreateString(id);
			var osPw = fbb.CreateString(pw);

			FbsLogInIdPw.StartFbsLogInIdPw(fbb);
			FbsLogInIdPw.AddId(fbb, osId);
			FbsLogInIdPw.AddPw(fbb, osPw);
			FbsLogInIdPw.AddVersion(fbb, clientVersion);
			var osLoginIdPw = FbsLogInIdPw.EndFbsLogInIdPw(fbb);
			fbb.Finish(osLoginIdPw.Value);

			return fbb.SizedByteArray();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public FbsLogInIdPw DeserializeLoginIdPw(byte[] byteLoginPw)
		{
			return FbsLogInIdPw.GetRootAsFbsLogInIdPw(new ByteBuffer(byteLoginPw));
		}


		//static public async Task AsyncWrite(TcpClient tc, byte[] data)
		//{
		//    if (tc.Connected)
		//    {
		//        try
		//        {
		//            NetworkStream netStream = tc.GetStream();
		//            await netStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
		//        }
		//        catch (Exception e)
		//        {
		//            Debug.WriteLine($"Write stream 오류 ~~~ {e.Message}");
		//        }
		//    }
		//    else
		//    {
		//        Debug.WriteLine("PacketM.AsyncWrite 소켓이 이미 close ~~~ ");
		//    }
		//}

	}

	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////    
	/// <summary>
	/// FbsClass를 Serialize하거나 Deserialize 할 때 사용 하는 클래스
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public abstract class FbsClassFactory<T> where T : struct
	{
		protected FlatBufferBuilder _fbb;


		// 사용 예)
		//  public class RoomInfoFactory : FbsClassFactory<FbsRoomInfo>
		//  {
		//        ChatRoom _chatRoom;


		// RoomInfoListFactory에서 RoomInfoFactory를 사용할 꺼라 fbb를 받아서 처리하도록 base(fbb)를 해 줬음 - 안그러면 default 값이 null이라 자체적으로 fbb를 생성해 버림
		// fbb = null의 의미는 RoomInfoFactory 단독으로 생성 할 때도 있기 때문에 편하게 쓰기 위함 안그럼 FlatBufferBuilder 만들어서 넘겨야함
		// --------------------------------------------------------------------------------------------------------------------------------------------------
		//    public RoomInfoFactory(ChatRoom chatRoom, FlatBufferBuilder fbb = null) : base(fbb)    
		//    {                                                                                      
		//        _chatRoom = chatRoom;
		//    }
		//
		/// <summary>
		/// 1. 이 클래스를 상속받아 구현시 또다른 FbsClassFactory를 상속받은 클래스 안에서 사용하고자 할 때는 생성자에서 위와 같이 base(fbb)로 플랫버퍼를 넘기는 구문을 넣어줘야 한다
		///    (그리고 생성하려는 FbsClassFactory의 _fbb를 넘겨서 생성한다. - 왜냐하면 클래스의 멤버로 추가는것이기 때문에 새로 _fbb를 생성하면 안됨)
		/// 2. static public Desrialize() 함수는 강제 사항은 아니지만, 일관성을 위해 편의상 같은이름으로 함께 구현한다
		/// </summary>
		/// <param name="fbb"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public FbsClassFactory(FlatBufferBuilder fbb)
		{
			if (fbb == null)
				_fbb = new FlatBufferBuilder(2048);
			else
				_fbb = fbb;                        // 어떤 FbsClassFactory<T> 안에서 생성하려고 할 때 fbb를 넘겨 받아야 됨
		}

		/// <summary>
		/// 항상 필요한 함수는 아니지만 편의상 구현한다
		/// </summary>
		/// <param name="strList"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static StringOffset[] GetArrStringOffset(FlatBufferBuilder fbb, IEnumerable<string> strList)
		{
			List<StringOffset> strOffsetList = new();
			foreach (string str in strList)
			{
				strOffsetList.Add(fbb.CreateString(str));
			}

			return strOffsetList.ToArray();

		}

		abstract public void StartFbsFuncCall();  // ex) FbsUserList.StartFbsUserList(_fbb); 와 같이 만들고자 하는 클래스의 StartFbs클래스를 Call 해야 함

		abstract public Offset<T> GetOffset();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] Serialize()
		{
			var os = GetOffset();
			StartFbsFuncCall();
			_fbb.Finish(os.Value);			

			return _fbb.SizedByteArray();
		}


		// 여기에 기술할 수는 없지만 편의상 Deserialize 하는 기능도 이 클래스를 구현하는 클래스에서 추가한다
		//public static T? Deserialize() { }        

	}
	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////    
	///

	public class FsEncryptKeyFactory : FbsClassFactory<FbsEncryptKey>
	{
		byte[] _key;
		byte[] _iv;

		public FsEncryptKeyFactory(byte[] key, byte[] iv) : base(null)
		{
			_key = key;
			_iv = iv;
		}

		public override Offset<FbsEncryptKey> GetOffset()
		{
			var keyOffset = FbsEncryptKey.CreateKeyVector(_fbb, _key);
			var ivOffset = (_iv == null) ? default(VectorOffset) : FbsEncryptKey.CreateIvVector(_fbb, _iv);
			return FbsEncryptKey.CreateFbsEncryptKey(_fbb, keyOffset, ivOffset);
		}

		public override void StartFbsFuncCall()
		{
			FbsEncryptKey.StartFbsEncryptKey(_fbb);
		}

		public static FbsEncryptKey? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;
			return FbsEncryptKey.GetRootAsFbsEncryptKey(new ByteBuffer(byteData));
		}
	}



	public class FsLoginOkFactory : FbsClassFactory<FbsLoginOk>
	{
		string _id;
		long _oid;
		long _serverFrequency;

		public FsLoginOkFactory(string id, long oid, long serverFrequency) : base(null)
		{
			_id = id;
			_oid = oid;
			_serverFrequency = serverFrequency;
		}
		public override Offset<FbsLoginOk> GetOffset()
		{
			return FbsLoginOk.CreateFbsLoginOk(_fbb, _fbb.CreateString(_id), _oid, _serverFrequency);
		}

		public override void StartFbsFuncCall()
		{
			FbsLoginOk.StartFbsLoginOk(_fbb);
		}

		public static FbsLoginOk? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;
			return FbsLoginOk.GetRootAsFbsLoginOk(new ByteBuffer(byteData));
		}
	}

	public class FsLoginFinFactory : FbsClassFactory<FbsLoginFin>
	{
		uint _pid;

		public FsLoginFinFactory(uint pid) : base(null)
		{
			_pid = pid;

		}
		public override Offset<FbsLoginFin> GetOffset()
		{
			return FbsLoginFin.CreateFbsLoginFin(_fbb, _pid);
		}

		public override void StartFbsFuncCall()
		{
			FbsLoginFin.StartFbsLoginFin(_fbb);
		}

		public static FbsLoginFin? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;
			return FbsLoginFin.GetRootAsFbsLoginFin(new ByteBuffer(byteData));
		}
	}


	public class FsServerTickFactory : FbsClassFactory<FbsServerTick>
	{

		public long _serverTick;
		public FsServerTickFactory(long serverTick) : base(null)
		{ _serverTick = serverTick; }

		public override Offset<FbsServerTick> GetOffset()
		{
			var os = FbsServerTick.CreateFbsServerTick(_fbb, _serverTick);
			return os;
		}

		public override void StartFbsFuncCall()
		{
			FbsServerTick.StartFbsServerTick(_fbb);
		}

		public static FbsServerTick? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;
			return FbsServerTick.GetRootAsFbsServerTick(new ByteBuffer(byteData));
		}
	}

	/// <summary>
	/// 메타의 헤더, 또는 라인 스트링 데이터를 전달 할 때 사용
	/// </summary>
	public class FsStrArrayFactory : FbsClassFactory<FbsStrArray>
	{
		IEnumerable<string> _strDataList;

		public FsStrArrayFactory(IEnumerable<string> strDataList, FlatBufferBuilder fbb = null) : base(fbb)    // FsStrArrayFactory를 상속받아 사용하기 때문에 FlatBufferBuilder fbb = null) : base(fbb)
		{
			_strDataList = strDataList;
		}

		public override Offset<FbsStrArray> GetOffset()
		{
			StringOffset[] arrOs = GetArrStringOffset(_fbb, _strDataList);

			var vos = FbsStrArray.CreateArrStrVector(_fbb, arrOs);
			var os = FbsStrArray.CreateFbsStrArray(_fbb, vos);

			return os;
		}

		/// <summary>
		/// FsStrArrayFactory는 범용적으로 사용할 가능성이 높은 클래스이므로 
		/// StringArray에 대한 List 데이터를 다른 FbsClassFactory<T> 상속받은 클래스에서 만들 때 쉽게 만들기 위해서 생성자의 strDataList는 null로 할당하고 아래 코드로 만들어 냄
		/// </summary>
		/// <param name="_strArrayLines"></param>
		/// <returns></returns>
		static public Offset<FbsStrArray>[] GetOffsetArray(FlatBufferBuilder fbb, IEnumerable<IEnumerable<string>> strArrayLines)
		{
			List<Offset<FbsStrArray>> strArrOsList = new List<Offset<FbsStrArray>>();
			foreach (var line in strArrayLines)
			{
				strArrOsList.Add(new FsStrArrayFactory(line, fbb).GetOffset());
			}

			return strArrOsList.ToArray();
		}

		public override void StartFbsFuncCall()
		{
			FbsStrArray.StartFbsStrArray(_fbb);
		}

		public static FbsStrArray? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;

			return FbsStrArray.GetRootAsFbsStrArray(new ByteBuffer(byteData));
		}
	}



	/// <summary>
	/// 메타의 테이블을 전달 할 때 사용
	/// </summary>
	public class FsMetaDataFactory : FbsClassFactory<FbsMetaData>
	{
		string _strKey;
		IEnumerable<string> _strListHeader;
		IEnumerable<IEnumerable<string>> _strDataLines;

		public FsMetaDataFactory(string strKey, IEnumerable<string> strListHeader, IEnumerable<IEnumerable<string>> strDataLines) : base(null)
		{
			_strKey = strKey;
			_strListHeader = strListHeader;
			_strDataLines = strDataLines;
			int k = 0;
		}

		public FsMetaDataFactory(MetaDataM metaM) : this(metaM.StrHeaderKey, metaM.GetHeaderList(), metaM.GetLineListAll())
		{
		}

		public override Offset<FbsMetaData> GetOffset()
		{
			StringOffset keyOs = _fbb.CreateString(_strKey);

			var arrStrOs = GetArrStringOffset(_fbb, _strListHeader);
			var arrStrVos = FbsMetaData.CreateHeaderVector(_fbb, arrStrOs);

			Offset<FbsStrArray>[] arrOsStrArray = FsStrArrayFactory.GetOffsetArray(_fbb, _strDataLines);

			var vos = FbsMetaData.CreateLineVector(_fbb, arrOsStrArray);
			var os = FbsMetaData.CreateFbsMetaData(_fbb, keyOs, arrStrVos, vos);

			return os;
		}

		public override void StartFbsFuncCall()
		{
			FbsMetaData.StartFbsMetaData(_fbb);
		}

		public static FbsMetaData? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;

			return FbsMetaData.GetRootAsFbsMetaData(new ByteBuffer(byteData));
		}

	}

	public class FsProgressBarFactory : FbsClassFactory<FbsProgressBar>
	{
		string _title;
		string _barText;
		bool _visible;
		ushort _gage;
		ushort _maxGage;
		byte _barType;
		ushort x;
		ushort y;
				
		public FsProgressBarFactory(bool visible, int barType, int x, int y, string title, string barText, int gage, int maxGage) : base(null)
		{
			_barType = (byte)barType;
			this.x = (ushort)x;
			this.y = (ushort)y;
			_title = title;
			_barText = barText;
			_gage = (ushort)gage;
			_visible = visible;
			_maxGage = (ushort)maxGage;
		}
		public override Offset<FbsProgressBar> GetOffset()
		{
			StringOffset osTitle = default;
			StringOffset osBarText = default;
			if(!string.IsNullOrEmpty(_title))
				osTitle = _fbb.CreateString(_title);
			if (!string.IsNullOrEmpty(_barText))
				osBarText = _fbb.CreateString(_barText);


			return FbsProgressBar.CreateFbsProgressBar(_fbb, _barType, _visible, osTitle, osBarText, _gage, _maxGage, x, y);
		}
		public override void StartFbsFuncCall()
		{
			FbsProgressBar.StartFbsProgressBar(_fbb);
		}
		public static FbsProgressBar? Deserialize(byte[] byteData)
		{
			if (byteData == null)
				return null;
			return FbsProgressBar.GetRootAsFbsProgressBar(new ByteBuffer(byteData));
		}
	}

	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 공용 패킷 관련
	/// </summary>
	public class DoPkDisconnectRequest : AbMemPkAction
	{
		public DoPkDisconnectRequest(PACKET_TYPE contentType) : base(contentType) { }
		public override Task MemPkAction(MemPacketM memPk)
		{
			memPk.U.RequestDisconnectForce();
			return Task.CompletedTask;
		}
	}


}
