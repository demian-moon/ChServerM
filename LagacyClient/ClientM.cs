using EcsServerLibM;
using FbsClassM;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace EcsClientLibM
{

	public enum CLIENT_CONNECT_MODE { VERSION_CHECK, WITHOUT_VERSION_CHECK }

	/// <summary>
	/// ClientM - 클라이언트
	/// </summary>
	public abstract class ClientM : AbNetworkBase    // T는 실제 앱 클래스 (Form 등)
	{
		private TcpClient _tc;
		private int _clientVersion;

		IPEndPoint _serverIp;
		public TcpClient Tc { get => _tc; set => _tc = value; }


		static IPAddress udpLogIp = null;
		//static IPAddress udpIp = IPAddress.Parse("39.117.205.158");

		// log4net 로그 객체
		public static AbLogM<string> logM;


		// PC_SERVER_TICK 패킷에서 설정됨        
		public long _lastUpdateServerTick; // 마지막 도착한 서버 틱 시간
		public long _clientTickWhenLastUpdateServerTick; // 마지막 도착한 서버틱때의 클라시간

		/// <summary>
		/// 패킷 도착할 때마다 갱신되는 _lastUpdateServerTick,  _clientTickWhenLastUpdateServerTick을 가지고 현재 서버시간을 유추함
		/// </summary>                
		public long ServerTickCurrent
		{

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				long elapsedClientTick = Stopwatch.GetTimestamp() - _clientTickWhenLastUpdateServerTick;
				return _lastUpdateServerTick + (long)(elapsedClientTick * ClientTimeM.gClientTickWeight);
			}
		}

		// 클라이언트 시작 틱
		public long _clientStartTick;

		/// <summary>
		/// 클라이언트 틱 
		/// </summary>        
		public long ClientTick { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Stopwatch.GetTimestamp(); }

		// 유저 정보 
		static ConcurrentDictionary<TcpClient, InnerUserM> _dicUser = new ConcurrentDictionary<TcpClient, InnerUserM>();
		static public UserM GetUser(TcpClient tc)
		{
			InnerUserM user;
			if (_dicUser.TryGetValue(tc, out user) == false)
			{
				//Debug.WriteLine("최초 추가일수 있으니 놀라지 말것 : 헐 유저가 널이야!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
			}

			return new UserM(user);
		}

		static public void AddUser(TcpClient tc, InnerUserM user)
		{
			if (_dicUser.TryAdd(tc, user) == false)
			{
				Debug.WriteLine($"버그M: 클라 유저추가 안됨: tc가 같음:{user.Id}");
			}
		}

		static public UserM RemoveUser(TcpClient tc)
		{
			if (_dicUser.TryRemove(tc, out InnerUserM user) == false)
			{
				Debug.WriteLine($"워닝M: 클라 유저없는데 지우려고함:");
			}
			return new UserM(user);
		}

		// 상속 구현 해야 하는 것들 - 스크린 사이즈       
		abstract public ScreenResolutionM GetScreenResolution();
		public struct ScreenResolutionM
		{
			public ScreenResolutionM(int width, int height)
			{
				screenWidth = width;
				screenHeight = height;
			}
			public int screenWidth;
			public int screenHeight;
		}


		IniClntOptionM _iniClntOption;
		/// <summary>
		/// 클라이언트 M 생성자
		/// </summary>
		public ClientM(uint uniquePrgNum, int clientVersion, string srvIp = null, int port = 0, string fileSavePath = null)
		{

#if UNITY_EDITOR
			if (uniquePrgNum <= 0)
			{
				Debug.Log("유니크 프로그램 넘버는 1이상이어야 함");
			}
#endif
			AbNetworkBase.uniqueProgramNumber = uniquePrgNum; ;     // EcsServerLibM을 이용하는 앱의 고유 넘버 최초 pid에 넣어 보내서 서버에서 체크함
			_iniClntOption = new IniClntOptionM(srvIp, port, fileSavePath);

			_clientVersion = clientVersion;

			// 스크린 사이즈 
			var screenResolution = GetScreenResolution();
			GlobalM.screenWidth = (int)screenResolution.screenWidth;
			GlobalM.screenHeight = (int)screenResolution.screenHeight;

			logM = new Log4NetM("ClientM", "log4netCla.config", udpLogIp);

		}

		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// 상속 구현해야 될 패킷 Action들 
		/// </summary>
		/// <param name="sourceMemPkDispatcher"> Add 함수를 호출해서 클라이언트에서 처리할 패킷에 대한 액션을 등록한다</param>
		/// <returns></returns>

		protected abstract void AddMemPkDispatcher(MemPkDispatcher sourceMemPkDispatcher);
		MemPkDispatcher _CreateMemPkDispatcher()
		{
			// default dispatcher
			var memPkDispatcher = new MemPkDispatcher();
			memPkDispatcher.Add(new DoPkHeartBit(PACKET_TYPE.PC_HEART_BIT));
			memPkDispatcher.Add(new DoPkDisconnectRequest(PACKET_TYPE.PSC_RQ_DISCONNECT));
			memPkDispatcher.Add(new DoPkServerTick(PACKET_TYPE.PC_SERVER_TICK, this));

			memPkDispatcher.Add(new DoPkVersionCheckResult(PACKET_TYPE.PC_VERSION_CHECK_RESULT, this));
			memPkDispatcher.Add(new DoPkRSA(PACKET_TYPE.PSC_RSA, this));
			memPkDispatcher.Add(new DoPkCompressAndEncrypt(PACKET_TYPE.PSC_COMP_ENC_CHANGE, this));
			memPkDispatcher.Add(new DoPkLoginOk(PACKET_TYPE.PC_LOGIN_OK, this));

			// add dispatcher            
			AddMemPkDispatcher(memPkDispatcher);

			return memPkDispatcher;
		}


		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////        
		// 패킷 지도 만들기
		// 상속해서 구현 해야 함 AddAllowedPacketMan
		//

		// 클라 필수 구현사항은 아님 기본적으로 그냥 두면 되고 선택적으로 패킷그룹별로 등록을 제한해야 할 때 사용한다.
		protected virtual void AddAllowedPacketMan(AllowedPacketMan.AllowedPacketManBuilder allowedPacketManBuilder)
		{
			;
		}
		public virtual ALLOWED_PACKET_STATE GetFirstUserPacketState()  // 클라이언트는 반드시 오버라이드 할 필요가 없다 선택 사항 (디폴트는 A_SC_ANY_STATE 모든  패킷을 받는다)
		{
			return ALLOWED_PACKET_STATE.A_SC_ANY_STATE;
		}
		AllowedPacketMan _CreateAllowedPacketMan()
		{

			AllowedPacketMan.AllowedPacketManBuilder apmb = new AllowedPacketMan.AllowedPacketManBuilder();

			// 모든 ALLOWED_PACKET_STATE 패킷 스테이트에서 받아주는 패킷들
			apmb.AddPacketAllAllowed(PACKET_TYPE.PC_HEART_BIT);        // 허트비트     
			apmb.AddPacketAllAllowed(PACKET_TYPE.PSC_RQ_DISCONNECT);   // 디스커넥트
			apmb.AddPacketAllAllowed(PACKET_TYPE.PC_SERVER_TICK);      // 서버틱

			// 로그인 하기전에 오는 패킷들 (유저의 ALLOWED_PACKET_STATE 스테이트가 아님)
			apmb.StartAllowedPkGroup(ALLOWED_PACKET_STATE.A_SC_NOT_LOGINED);
			apmb.AddPacketType(PACKET_TYPE.PSC_RSA); // 로그인 하기 전에 옴
			apmb.AddPacketType(PACKET_TYPE.PSC_COMP_ENC_CHANGE); // 로그인 하기 전에 옴
			apmb.AddPacketType(PACKET_TYPE.PC_VERSION_CHECK_RESULT); // 로그인 하기 전에 옴
			apmb.AddPacketType(PACKET_TYPE.PC_LOGIN_OK);
			apmb.EndAllowedPkGroup();

			// 최초 Start 패킷 타입
			apmb.StartAllowedPkGroup(ALLOWED_PACKET_STATE.A_SC_START);
			apmb.AddPacketType(PACKET_TYPE.PC_LOGIN_OK);
			apmb.EndAllowedPkGroup();

			///////////////////////////////////////////////////////////////////////
			// 상속 구현
			AddAllowedPacketMan(apmb);

			return apmb.Build();

		}

		public void VersionCheckResult(in MemPacketM memPk) // memPk.U는 없으니 주의
		{
			bool bVersionOk = BitConverter.ToBoolean(memPk.ConData, 0);

			if (bVersionOk == false)  // 현재 클라버전이 최신이 아니면
			{
				AppUpdateClient();         // 여기서 클라 닫고 업데이트 받아야 됨
			}

		}


		// 압축 암호화 된 패킷을 Decrypt해서 보낸다
		public async ValueTask SendEncMemPk(EncMemPacketM encMemPk, CancellationTokenSource cts)
		{
			UserM tmUser = ClientM.GetUser(Tc);
			CompressAndEncryptM compEnc = null;

			if (tmUser.IsExist)
			{
				compEnc = tmUser.CompEnc;
			}
			else
			{
				CompressAndEncryptManM.TryGetValue(Tc, out compEnc);
			}

			var memPk = encMemPk.MakeMemPacket(compEnc);
			await SendMemPk(memPk, cts).ConfigureAwait(false);
		}

		public async ValueTask SendMemPk(MemPacketM memPk, CancellationTokenSource cts)
		{
			UserM tmUser = ClientM.GetUser(Tc);
			var pkType = (PACKET_TYPE)memPk.ConHead.PacketType;

			if (tmUser.IsExist == true)
			{
				memPk.U = tmUser; // 유저 세팅
				if (memPk.IsUIThread())
				{
					SendPacketM.SendMemPacketUI(memPk);
				}
				else
				{
					SendPacketM.SendMemPacket(memPk);
				}
			}
			else
			{
				if (IsAllowedPacketNotLogined(pkType) == false)
				{
					Debug.WriteLine("서버에서 보낸 패킷이 문제가 있어서 접속 종료!");
					Tc.Close();
					return;
				}
				//if (pkType == PACKET_TYPE.PSC_RSA)
				//{
				//	ArriveServerRSAPublicKey(memPk);
				//}
				//else if (pkType == PACKET_TYPE.PSC_COMP_ENC_CHANGE)
				//{
				//	CompressAndEncryptChangeForClient(memPk);
				//}
				//else if (pkType == PACKET_TYPE.PC_LOGIN_OK)
				//{
				//	LogInOk(memPk);
				//}
				//else if (pkType == PACKET_TYPE.PC_VERSION_CHECK_RESULT)
				//{
				//	VersionCheckResult(memPk);
				//}
				//else
				//{
					SendPacketM.SendMemPacket(memPk);
				//}

			}
		}

		/// <summary>
		/// 옵션 파일 읽기
		/// </summary>
		/// <returns></returns>
		async ValueTask LoadIniFile()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				await _iniClntOption.LoadIni().ConfigureAwait(false);
			}

			string __ipAdress = IniOptionM.gIpAddress;
			int __port = IniOptionM.gPort;
			_serverIp = new IPEndPoint(IPAddress.Parse(__ipAdress), __port);
		}


		/// <summary>
		/// 커넥션 함수
		/// </summary>
		/// <param name="connectMode"></param>
		/// <returns></returns>
		public async ValueTask<bool> ClientConnect(CLIENT_CONNECT_MODE connectMode)
		{
			// ini파일 읽기
			await LoadIniFile().ConfigureAwait(false);


			// AllowedPaketMan 만들기
			_allowedPkMan = _CreateAllowedPacketMan();

			// 패킷 처리기 만들기
			_memPkDispatcher = _CreateMemPkDispatcher();
			_memPkDispatcher.LoadActions();

			bool rtn = false;
			try
			{
				rtn = await _Connect().ConfigureAwait(false);
			}
			catch (SocketException se)
			{
				Debug.WriteLine("연결 에러: 대상컴퓨터 거부:" + se.ToString());
				return false;
			}

			if (connectMode == CLIENT_CONNECT_MODE.VERSION_CHECK)
			{
				byte[] sendData = BitConverter.GetBytes(_clientVersion);
				SendPacketM.SendPacket(Tc, AbNetworkBase.uniqueProgramNumber, PACKET_TYPE.PS_VERSION_CHECK, sendData); // 암호화 하지 않는다
			}

			return rtn;
		}


		private async ValueTask<bool> _Connect()
		{
			try
			{
				Tc = new TcpClient();

				Debug.WriteLine($"client 접속하는 서버 아이피정보 - srvIp:{_serverIp} port:{IniOptionM.gPort}");
				await Tc.ConnectAsync(_serverIp.Address, _serverIp.Port).ConfigureAwait(false);

				// 네이글 알고리즘 비활성화
				Tc.NoDelay = true;
			}
			catch (SocketException se)
			{
				Debug.WriteLine($"소켓 에러!!! 뭐야 대체!! : {se.Message}");
				return false;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"TC Connection 에러!!! 뭐야 대체!! : {ex.Message}");
				return false;
			}

			if (Tc != null || Tc.Connected)
			{
				// SetKeepAlive 5초뒤 5초 간격으로 확인 - 클라는 일단 하지 말자
				// SetKeepAlive(Tc.Client, true, 5000, 5000);

				IoPipelineClaM.PipelineForClientAsync(Tc, this).ConfigureAwait(false);    // 여기서 await 하면 ClientConnect 함수가 호출한 _Connect() 함수가 종료되지 않아 다음 구문을 실행하지 않는다
			}
			return true;
		}


		// 접속 종료
		public void Disconnect()
		{
			var user = GetUser(Tc);

			Debug.WriteLine($"클라 디스커넥트 추적자{user.Id}");

			user.RequestDisconnectForce();
		}




		/// <summary>
		/// 클라이언트 버전이 다를 때 클라이언트 업데이트 받는 로직 (클라 닫고 업데이트 받는 로직 구현해야 함) 
		/// </summary>
		protected abstract void UpdateClient();

		/// <summary>
		/// 클라닫고 업데이트 받아야 함
		/// </summary>
		public void AppUpdateClient()
		{
			UpdateClient();
		}


		/// <summary>
		/// ClientM 상속받은 앱의 최초 실행 펑션 세팅 - 상속받으면 실행해준다
		/// </summary>
		/// <param name="user"></param>
		protected virtual void StartAfterLogin(UserM user) { }
		public void AppStart(UserM user)
		{
			// 클라이언트 tick
			_clientStartTick = ClientTick;
			

			StartAfterLogin(user);
		}

		/// <summary>
		/// 유저 종료시 처리해야 할 일 클라 Override 
		/// </summary>
		/// <param name="user"></param>
		protected virtual void FinishUser(UserM user) { }
		public async ValueTask AppFinish(UserM user)
		{
			FinishUser(user);
		}

		///////////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// 로그인 함수
		/// </summary>
		/// <param name="id"></param>
		/// <param name="pw"></param>
		/// 

		public string _id;
		public string _pw;
		public string _privateKeyMadeByClient;
		public void LoginFunc(string id, string pw)
		{
			_id = id;
			_pw = pw;


			using (var rsa = new RSACryptoServiceProvider(2048))
			{
				_privateKeyMadeByClient = rsa.ToXmlString(true); // 개인키 저장

				var publicKey = rsa.ToXmlString(false); // 서버에 전달할 공개키

				var bytePublicKey = Encoding.UTF8.GetBytes(publicKey);
				uint pid = AbNetworkBase.uniqueProgramNumber; // 스타트 pid(프로그램 고유넘버) - flatbuffer는 0은 생성하지 않으므로 0을 넣으면 에러난다 - 헤더사이즈가 달라짐 
				SendPacketM.SendPacket(Tc, pid, PACKET_TYPE.PSC_RSA, bytePublicKey);    // 공개키 서버 전달 
			}
		}

		///// <summary>
		///// 서버에서 PACKET_TYPE.PSC_RSA 도착 했을 때 실행
		///// </summary>
		///// <param name="memPk"></param>
		//public void ArriveServerRSAPublicKey(in MemPacketM memPk)
		//{
		//	var publicKeyMadeByServer = Encoding.UTF8.GetString(memPk.ConData); // 서버에서 받은 공개키

		//	// 암호화 클래스 등록
		//	var compEnc = new CompressAndEncryptM(default, default, _privateKeyMadeByClient, publicKeyMadeByServer);
		//	CompressAndEncryptManM.TryAdd(Tc, compEnc);
		//	_LoginFuncStep1();
		//}


		/// <summary>
		/// 서버에서 보내준 RSA 공개키를 가지고 암호화 해서 보낸다
		/// </summary>
		public void _LoginFuncStep1(TcpClient tc)
		{
			if (CompressAndEncryptManM.TryGetValue(tc, out var compEnc))
			{
				compEnc.CreateEncDecType(CompressAndEncryptM.ENCRYPT_TYPE.AES, CompressAndEncryptM.ENCRYPT_TYPE.XOR); // DisconnectProcess Dispose() 함
				var encKeyData = new FsEncryptKeyFactory(compEnc.AesKey, compEnc.AesIV).Serialize();

				// RSA 공개키로 암호화 해서 보냄
				using (var rsa = new RSACryptoServiceProvider())
				{
					rsa.FromXmlString(compEnc.RSAPublicKeyMadeByServer);
					encKeyData = rsa.Encrypt(encKeyData, RSAEncryptionPadding.Pkcs1); // 공개키로 암호화	
				}

				uint pid = AbNetworkBase.uniqueProgramNumber; // 스타트 pid(프로그램 고유넘버) - flatbuffer는 0은 생성하지 않으므로 0을 넣으면 에러난다 - 헤더사이즈가 달라짐 
				SendPacketM.SendPacket(tc, pid, PACKET_TYPE.PSC_COMP_ENC_CHANGE, encKeyData);
			}
		}

		/// <summary>
		/// PACKET_TYPE.PSC_COMP_ENC_CHANGE를 서버에서 받으면 불린다.
		/// 서버에서 받은 XOR 암호 Key 설정
		/// </summary>
		/// <param name="memPk"></param>
		//public void CompressAndEncryptChangeForClient(in MemPacketM memPk)
		//{
		//	if (CompressAndEncryptManM.TryGetValue(Tc, out var compEnc))
		//	{
		//		using (var rsa = new RSACryptoServiceProvider())
		//		{
		//			rsa.FromXmlString(compEnc.RSAPrivateKeyMadeByClient);
		//			var xorKeyInfo = rsa.Decrypt(memPk.ConData, RSAEncryptionPadding.Pkcs1);

		//			FbsEncryptKey encryptKey = FsEncryptKeyFactory.Deserialize(xorKeyInfo).Value;
		//			compEnc.SetXorKey(encryptKey.GetKeyArray());

		//			_LoginFuncStep2(compEnc); // 실제 로그인 여기서 이루어 진다
		//		}
		//	}
		//	else
		//	{
		//		throw new Exception($"CompressAndEncryptChangeForClient : 등록된 compEnc가 없음");
		//	}
		//}

		/// <summary>
		/// 암호화 키 교환 후 실제 로긴 함수를 부른다
		/// </summary>
		public void _LoginFuncStep2(TcpClient tc, CompressAndEncryptM compEnc)
		{
			
			uint pid = AbNetworkBase.uniqueProgramNumber; // 스타트 pid(프로그램 고유넘버) - flatbuffer는 0은 생성하지 않으므로 0을 넣으면 에러난다 - 헤더사이즈가 달라짐 
			var loginIdPw = PacketM.SerializeLoginIdPw(_id, _pw, _clientVersion);

			SendPacketM.SendPacket(tc, pid, PACKET_TYPE.PS_LOGIN, loginIdPw, compEnc); // 지금 부터 암호화 해서 보냄
		}


		//public void LogInOk(in MemPacketM memPk)
		//{

		//	var loginOk = FsLoginOkFactory.Deserialize(memPk.ConData);
		//	uint pid = memPk.PkHead.Pid;

		//	string id = loginOk.Value.Id;

		//	// 서버에서 전달된 서버의 StopWatch.Frequency
		//	ClientTimeM.gServerFrequency = (loginOk.Value.ServerFrequency <= 0) ? 1 : loginOk.Value.ServerFrequency; // 예외 처리 0으로 나누면 안됨
		//	ClientTimeM.gClientTickWeight = (double)ClientTimeM.gServerFrequency / (double)Stopwatch.Frequency;

		//	//MessageBox.Show($"유저아이디는 너잖아 발급받은 pid{pid}");
		//	//Debug.WriteLine($"유저 아이디는 너자신 {id} - 발급받은 pid{pid} : 현재 서버 유저수 : {loginOk.Value.CntServerUsers}");


		//	//ObjM.MakeOid(out long oid); // 자신이 아니라 서버에서 보내준 oid로 세팅한다
		//	InnerUserM user = new InnerUserM(memPk.Tc, id, pid, loginOk.Value.Oid);
		//	user.AllowedPkState = GetFirstUserPacketState();   // 패킷 스테이트를 먼저 정함

		//	ClientM.AddUser(memPk.Tc, user);

		//	// 유저 Encrypt 설정
		//	CompressAndEncryptManM.TryRemove(memPk.Tc, out CompressAndEncryptM compEnc);
		//	user._compEnc = compEnc;

		//	// 로그인 - 마지막 LoginFin 패킷 보내기
		//	var sendData = new FsLoginFinFactory(pid).Serialize();
		//	user.SerializeSendPacket(PACKET_TYPE.PS_LOGIN_FIN, sendData);

		//	// 클라이언트 상속받은 앱 시작 Start() 펑션 호출 - override 한 경우만 실행됨 - 순서 중요 LOGIN_FIN보다 늦게 보내야 됨
		//	AppStart(new UserM(user));

		//}
		/// <summary>
		/// 클라이언트 close 버튼 누를 때 호출 
		/// </summary>
		// 
		public void ClosingFunc()
		{
			if (Tc?.Connected ?? false) // 접속을 안한 상태면 false
				Disconnect();
		}

	}


	/// <summary>
	/// 패킷 처리들 
	/// </summary>

	// 허트 비트 받으면 허트비트 얼라이브 보내기
	public class DoPkHeartBit : AbMemPkAction
	{
		public DoPkHeartBit(PACKET_TYPE ePacketType) : base(ePacketType) { }
		public override Task MemPkAction(MemPacketM memPk)
		{
			var tc = memPk.Tc;

			UserM user = memPk.U;
			user.SerializeSendPacket(PACKET_TYPE.PS_HEART_BIT_ALIVE, null);
			Debug.WriteLine($"허트비트---두둥:{user.Id}: {DateTime.Now} \n");
			return Task.CompletedTask;
		}

	}

	public class DoPkServerTick : AbMemPkAction
	{
		ClientM _clientM;
		public DoPkServerTick(PACKET_TYPE ePacketType, ClientM clientM) : base(ePacketType)
		{
			_clientM = clientM;
		}

		public override Task MemPkAction(MemPacketM memPk)
		{
			//var fbsServerTick = FsServerTickFactory.Deserialize(memPk.ConData);

			// 서버틱에 바로 응답함 (네트워크 딜레이 계산)
			UserM user = memPk.U;
			user.SerializeSendPacket(PACKET_TYPE.PS_RSP_SERVER_TICK, null); // 바로 응답함


			// 도착한 마지막 서버 업데이트 tick 시간 저장해 놓음
			_clientM._lastUpdateServerTick = BitConverter.ToInt64(memPk.ConData, 0);
			_clientM._clientTickWhenLastUpdateServerTick = Stopwatch.GetTimestamp();   // 클라의 현재 틱

			//Debug.WriteLine($"서버틱 델타: {_clientM._serverTickDelta} 서버틱: {fbsServerTick.Value._serverTick} ");
			return Task.CompletedTask;
		}

	}

	public class DoPkVersionCheckResult : AbMemPkAction
	{
		ClientM _clientM;
		public DoPkVersionCheckResult(PACKET_TYPE ePacketType, ClientM clientM) : base(ePacketType)
		{
			_clientM = clientM;
		}

		public override Task MemPkAction(MemPacketM memPk)
		{

			bool bVersionOk = BitConverter.ToBoolean(memPk.ConData, 0);

			if (bVersionOk == false)  // 현재 클라버전이 최신이 아니면
			{
				_clientM.AppUpdateClient();         // 여기서 클라 닫고 업데이트 받아야 됨
			}
			return Task.CompletedTask;
		}
	}

	public class DoPkRSA : AbMemPkAction
	{
		ClientM _clientM;
		public DoPkRSA(PACKET_TYPE ePacketType, ClientM clientM) : base(ePacketType)
		{
			_clientM = clientM;
		}

		public override Task MemPkAction(MemPacketM memPk)
		{
			var publicKeyMadeByServer = Encoding.UTF8.GetString(memPk.ConData); // 서버에서 받은 공개키

			var tc = memPk.Tc;
			
			// 암호화 클래스 등록
			var compEnc = new CompressAndEncryptM(default, default, _clientM._privateKeyMadeByClient, publicKeyMadeByServer);
			CompressAndEncryptManM.TryAdd(tc, compEnc);
			_clientM._LoginFuncStep1(tc);
			return Task.CompletedTask;

		}
	}

	public class DoPkCompressAndEncrypt : AbMemPkAction
	{
		ClientM _clientM;
		public DoPkCompressAndEncrypt(PACKET_TYPE ePacketType, ClientM clientM) : base(ePacketType)
		{
			_clientM = clientM;
		}

		public override Task MemPkAction(MemPacketM memPk)
		{
			var tc = memPk.Tc;

			if (CompressAndEncryptManM.TryGetValue(tc, out var compEnc))
			{
				using (var rsa = new RSACryptoServiceProvider())
				{
					rsa.FromXmlString(compEnc.RSAPrivateKeyMadeByClient);
					var xorKeyInfo = rsa.Decrypt(memPk.ConData, RSAEncryptionPadding.Pkcs1);

					FbsEncryptKey encryptKey = FsEncryptKeyFactory.Deserialize(xorKeyInfo).Value;
					compEnc.SetXorKey(encryptKey.GetKeyArray());

					_clientM._LoginFuncStep2(tc, compEnc); // 실제 로그인 여기서 이루어 진다
				}
			}
			else
			{
				throw new Exception($"CompressAndEncryptChangeForClient : 등록된 compEnc가 없음");
			}
			return Task.CompletedTask;
		}
	}


	public class DoPkLoginOk : AbMemPkAction
	{
		ClientM _clientM;
		public DoPkLoginOk(PACKET_TYPE ePacketType, ClientM clientM) : base(ePacketType)
		{
			_clientM = clientM;
		}

		public override Task MemPkAction(MemPacketM memPk)
		{
			var loginOk = FsLoginOkFactory.Deserialize(memPk.ConData);
			uint pid = memPk.PkHead.Pid;

			string id = loginOk.Value.Id;

			// 서버에서 전달된 서버의 StopWatch.Frequency
			ClientTimeM.gServerFrequency = (loginOk.Value.ServerFrequency <= 0) ? 1 : loginOk.Value.ServerFrequency; // 예외 처리 0으로 나누면 안됨
			ClientTimeM.gClientTickWeight = (double)ClientTimeM.gServerFrequency / (double)Stopwatch.Frequency;

			//MessageBox.Show($"유저아이디는 너잖아 발급받은 pid{pid}");
			//Debug.WriteLine($"유저 아이디는 너자신 {id} - 발급받은 pid{pid} : 현재 서버 유저수 : {loginOk.Value.CntServerUsers}");


			//ObjM.MakeOid(out long oid); // 자신이 아니라 서버에서 보내준 oid로 세팅한다
			InnerUserM user = new InnerUserM(memPk.Tc, id, pid, loginOk.Value.Oid);
			user.AllowedPkState = _clientM.GetFirstUserPacketState();   // 패킷 스테이트를 먼저 정함

			ClientM.AddUser(memPk.Tc, user);

			// 유저 Encrypt 설정
			CompressAndEncryptManM.TryRemove(memPk.Tc, out CompressAndEncryptM compEnc);
			user._compEnc = compEnc;

			// 로그인 - 마지막 LoginFin 패킷 보내기
			var sendData = new FsLoginFinFactory(pid).Serialize();
			user.SerializeSendPacket(PACKET_TYPE.PS_LOGIN_FIN, sendData);

			// 클라이언트 상속받은 앱 시작 Start() 펑션 호출 - override 한 경우만 실행됨 - 순서 중요 LOGIN_FIN보다 늦게 보내야 됨
			_clientM.AppStart(new UserM(user));
			return Task.CompletedTask;
		}
	}
}