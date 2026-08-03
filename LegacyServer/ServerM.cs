using CommunityToolkit.HighPerformance;
using EcsServerLibM;
using FbsClassM;
using log4net;
using Microsoft.CodeAnalysis.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static EcsServerLibM.ServerM;

namespace EcsServerLibM
{
	// 브릿지 모듈 //
	// AbLogM




	/// <summary>
	/// ServerM - 서버
	/// </summary>
	public abstract class ServerM : AbNetworkBase
	{
		static IPAddress udpLogIp = null;
		//static IPAddress udpIp = IPAddress.Parse("39.117.205.158");

		// log4net 로그 객체
		public static AbLogM<string> logM;

		// 글로벌 서버 타임 스케쥴러
		public static TimeEventSchedulerM gTimeScheduler;

		/// <summary>
		/// 버퍼링된 파일로그등을 최종적으로 Flush한다. 
		/// </summary>
		static void FlushLogs()
		{
			logM.FlushLogs();
		}

		static void Debug(string logMsg)
		{
			logM.Debug(logMsg);
		}


		/// <summary>
		/// 유니크 PID 생성 관련
		/// </summary>
		static int _uniquePkId;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void MakePkId(out uint pid)
		{
			pid = (uint)Interlocked.Increment(ref _uniquePkId); // 로컬변수에 대입하므로 각 쓰레드마다 다른 값을 가진다. 즉 unique 하다
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////        
		/// <summary>
		/// 패킷 처리 함수 - 등록
		/// </summary>
		/// <param name="sourceMemPkDispatcher">Add 함수를 호출해서 서버에서 처리할 패킷에 대한 액션을 등록한다</param>
		/// <returns></returns>
		protected abstract void AddMemPkDispatcher(MemPkDispatcher sourceMemPkDispatcher);
		MemPkDispatcher _MakeMemPkDispatcher()
		{
			// default dispatcher (기본 패킷 처리기)
			var memPkDispatcher = new MemPkDispatcher();
			memPkDispatcher.Add(new DoPkHeartBitAlive(PACKET_TYPE.PS_HEART_BIT_ALIVE));
			memPkDispatcher.Add(new DoPkDisconnectRequest(PACKET_TYPE.PSC_RQ_DISCONNECT));
			memPkDispatcher.Add(new DoPkRspServerTick(PACKET_TYPE.PS_RSP_SERVER_TICK));

			memPkDispatcher.Add(new DoPkVersionCheck(PACKET_TYPE.PS_VERSION_CHECK));
			memPkDispatcher.Add(new DoPkRSAForSever(PACKET_TYPE.PSC_RSA));
			memPkDispatcher.Add(new DoPkCompressAndEncryptForServer(PACKET_TYPE.PSC_COMP_ENC_CHANGE));
			memPkDispatcher.Add(new DoPkLogin(PACKET_TYPE.PS_LOGIN, this));
			memPkDispatcher.Add(new DoPkLoginFin(PACKET_TYPE.PS_LOGIN_FIN, this));

			// Add dispatcher
			// 패킷 처리기 만들기 (상속해서 만들어야 함)
			AddMemPkDispatcher(memPkDispatcher);

			return memPkDispatcher;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////        
		// Allowed 패킷 Man 만들기 - 서버
		// 상속해서 구현 해야 함
		protected abstract void AddAllowedPacketMan(AllowedPacketMan.AllowedPacketManBuilder allowedPacketManBuilder);
		public abstract ALLOWED_PACKET_STATE GetFirstUserPacketState();  // 서버유저의 최초 PacketAllowedState

		AllowedPacketMan _CreateAllowedPacketMan()
		{
			AllowedPacketMan.AllowedPacketManBuilder apmb = new();

			// 모든 ALLOWED_PACKET_STATE에서 받아줄 패킷 정의 
			apmb.AddPacketAllAllowed(PACKET_TYPE.PS_HEART_BIT_ALIVE);
			apmb.AddPacketAllAllowed(PACKET_TYPE.PSC_RQ_DISCONNECT);

			// 로그인 전에 받아줄 패킷 타입
			apmb.StartAllowedPkGroup(ALLOWED_PACKET_STATE.A_SC_NOT_LOGINED);
			apmb.AddPacketType(PACKET_TYPE.PSC_RSA); // 로그인 하기 전에 옴
			apmb.AddPacketType(PACKET_TYPE.PSC_COMP_ENC_CHANGE); // 로그인 하기 전에 옴
			apmb.AddPacketType(PACKET_TYPE.PS_LOGIN);
			apmb.AddPacketType(PACKET_TYPE.PS_VERSION_CHECK);
			apmb.EndAllowedPkGroup();

			// 최초 Start 패킷 타입 (PS_VERSION_CHECK, PS_LOGIN 패킷은 유저를 설정하기 전에 오기때문에 여기서 빠진다) 
			apmb.StartAllowedPkGroup(ALLOWED_PACKET_STATE.A_SC_START);
			apmb.AddPacketType(PACKET_TYPE.PS_LOGIN_FIN); // 로그인 후에 옴           
			apmb.EndAllowedPkGroup();

			//            

			///////////////////////////////////////////////////////////////////////
			// 상속 구현
			AddAllowedPacketMan(apmb);

			return apmb.Build();
		}

		
		//public void OnRequestRSA(in MemPacketM memPk)
		//{
		//	var publicKeyMadeByClient = Encoding.UTF8.GetString(memPk.ConData);  // 클라에서 보낸 공개키

		//	// 서버의 
		//	using (var rsa = new RSACryptoServiceProvider(2048))
		//	{
		//		var privateKeyMadeByServer = rsa.ToXmlString(true); // 개인키 저장
		//		var publicKey = rsa.ToXmlString(false); // 클라에 전달할 공개키
		//		var bytePublicKey = Encoding.UTF8.GetBytes(publicKey);

		//		// 인크립트 클래스 생성 후 등록
		//		var compEnc = new CompressAndEncryptM(privateKeyMadeByServer, publicKeyMadeByClient, default, default);  
		//		CompressAndEncryptManM.TryAdd(memPk.Tc, compEnc);

		//		SendPacketGroupM.SendPacket(0, memPk.Tc, 1, PACKET_TYPE.PSC_RSA, bytePublicKey, null); // 암호화 하지 않음, 클라로 공개키 보냄
		//	}
		//}


		///// <summary>
		///// PACKET_TYPE.PSC_COMP_ENC_CHANGE를 클라에서 받으면 불린다.
		///// 클라이언트 받은 AES 암호 Key, iv 설정
		///// </summary>
		///// <param name="memPk"></param>
		//public void CompressAndEncryptChangeForServer(in MemPacketM memPk)
		//{
		//	if (CompressAndEncryptManM.TryGetValue(memPk.Tc, out var compEnc))
		//	{
		//		// RSA 개인키로 푼다
		//		using (var rsa = new RSACryptoServiceProvider())
		//		{
		//			compEnc.CreateEncDecType(CompressAndEncryptM.ENCRYPT_TYPE.XOR, CompressAndEncryptM.ENCRYPT_TYPE.AES); // 서버는 Enc는 XOR, Dec는 AES			

		//			rsa.FromXmlString(compEnc.RSAPrivateKeyMadeByServer); 
		//			var claAesInfo = rsa.Decrypt(memPk.ConData, RSAEncryptionPadding.Pkcs1);					

		//			FbsEncryptKey encryptKey = FsEncryptKeyFactory.Deserialize(claAesInfo).Value;
		//			compEnc.SetAesKey(encryptKey.GetKeyArray(), encryptKey.GetIvArray());
		//		}

		//		using (RSA rsa = RSA.Create())
		//		{
		//			rsa.FromXmlString(compEnc.RSAPublicKeyMadeByClient);	// 클라에서 받은 공개키로 초기화
		//			var sendEcryptKeyData = new FsEncryptKeyFactory(compEnc.XorKey, null).Serialize();

		//			sendEcryptKeyData = rsa.Encrypt(sendEcryptKeyData, RSAEncryptionPadding.Pkcs1);  // 클라에서 받은 공개키로  XOR 키정보 암호화
		//			SendPacketGroupM.SendPacket(0, memPk.Tc, 1, PACKET_TYPE.PSC_COMP_ENC_CHANGE, sendEcryptKeyData, null); // 암호화 하지 않음
		//		}
		//	}
		//	else
		//	{
		//		throw new Exception("버그M: CompressAndEncryptChangeForServer, CompAndEnc 등록된 객체 없음)");
		//	}

		//}

		//public async Task Login(MemPacketM memPk)
		//{
		//	// 프로그램 넘버 체크
		//	if (ServerM.CheckUiqueProgramNumber(memPk.PkHead.Pid) == false)
		//	{
		//		if (memPk.Tc.Connected)
		//		{
		//			System.Diagnostics.Debug.WriteLine($"###유니크 프로그램 넘버가 다름 서버:{ServerM.clientVersion}:클라:{memPk.PkHead.Pid}####");
		//			memPk.Tc.Close();
		//		}
		//		return;
		//	}

		//	FbsLogInIdPw loginIdPw = PacketM.DeserializeLoginIdPw(memPk.ConData);

		//	////////////////////////////////////////////////////////
		//	//// 클라 버전 검증 - 
		//	////////////////////////////////////////////////////////                       

		//	// tc와 매칭된  Encrypt 얻기
		//	CompressAndEncryptManM.TryRemove(memPk.Tc, out CompressAndEncryptM compEnc);

		//	if (ServerM.clientVersion != loginIdPw.Version)
		//	{
		//		byte[] data = BitConverter.GetBytes(false);
		//		SendPacketGroupM.SendPacket(0, memPk.Tc, AbNetworkBase.uniqueProgramNumber, PACKET_TYPE.PC_VERSION_CHECK_RESULT, data, compEnc); // 암호화 해서 보낸다

		//		// PACKET_TYPE.PSC_RQ_DISCONNECT 호출 해야 될 수도 있음 추후 확인 (직접 memPk.Tc.Close()를 진행하면 패킷이 안갈듯?)
		//		return;
		//	}

		//	////////////////////////////////////////////////////////
		//	//// ip 비번 검증 - 추후 검증해서 자를것
		//	////////////////////////////////////////////////////////

		//	// 여기서 잘라 memPk.Tc.Close(); return;

		//	/////////////////////////////////////////////////////////
		//	var id = loginIdPw.Id;
		//	var pw = loginIdPw.Pw;

		//	ObjectId objectId = ObjectId.Empty;
		//	var resLoad = await LoadingUserAuthDbAsync(id, pw).ConfigureAwait(false);
		//	objectId = resLoad.objectId;
			
		//	if(resLoad.authResult == eAUTH_RESULT.WRONG_PW)
		//	{				
		//		ServerM.logM.Debug($"로그인 실패인데 로딩 그냥 해줌(추후변경 예정) - 비밀번호 틀림: {id}");
		//		// return; // 비밀번호 틀리면 패킷 보내야 됨 - 임시 주석

		//	}
		//	else if (resLoad.authResult == eAUTH_RESULT.SUCCESS)
		//	{
		//		ServerM.logM.Debug($"로그인 성공 비번도 맞음: {id}");
		//	}
		//	else if (resLoad.authResult == eAUTH_RESULT.ERROR)
		//	{
		//		throw new Exception("LoadingUserAuthDb Fail....");
		//	}

		//	var tc = memPk.Tc;
		//	InnerSrvUserM innerSrvUser = new InnerSrvUserM(tc);
		//	innerSrvUser.AllowedPkState = GetFirstUserPacketState();
		//	innerSrvUser.Id = id;
		//	innerSrvUser.DB_ID = objectId; // DB에 저장된 db 아이디

		//	//// 유니크 Packet ID 발급
		//	uint uniquePid;
		//	ServerM.MakePkId(out uniquePid);
		//	innerSrvUser.Pid = uniquePid;

		//	// 유니크 Oid 발급
		//	innerSrvUser.MakeOid();

		//	// 서버 유저 Encrypt 설정            
		//	innerSrvUser._compEnc = compEnc;

		//	// 서버유저 추가
		//	IncrementServerUserCnt();

		//	// 유저 등록                                 
		//	SrvGlobal.AddUser(tc, innerSrvUser);			

		//	var srvUser = new SrvUserM(innerSrvUser); // 서버 유저 객체 생성			

		//	// 최초 ServerM Start함수 콜 - 
		//	// 서버 상속받은 앱 시작 AppStartSrvUser() 펑션 호출 - override 한 경우만 실행됨 
		//	StartSrvUser(srvUser);

		//	var sendData = new FsLoginOkFactory(innerSrvUser.Id, innerSrvUser.Oid, Stopwatch.Frequency).Serialize();
		//	innerSrvUser.SerializeSendPacket(PACKET_TYPE.PC_LOGIN_OK, sendData);    // 암호화
		//}

		
		//public async Task<(bool bSaved, ObjectId objectId)> SaveUserAuthDbAsync(string id, string pw)
		//{
		//	var filter = Builders<SrvUserAuthM>.Filter.Eq(dt => dt.id, id);
		//	var bExists = await DBManagerM.Instance.DbMgr.HasAasync<SrvUserAuthM>(SrvGlobal.gSrvUserAuthTableName, filter).ConfigureAwait(false);
		//	if (bExists)
		//	{
		//		// 이미 존재하는 유저
		//		return (false, ObjectId.Empty);
		//	}
		//	else
		//	{
		//		var objectId = ObjectId.GenerateNewId();
		//		var hashedPw = AuthM.GetHashPassword(pw); // 비밀번호 해싱
		//		var newSrvUserAuth = new SrvUserAuthM(objectId, id, hashedPw); // 해시된 비밀번호로 저장
		//		await DBManagerM.Instance.DbMgr.InsertAsync(SrvGlobal.gSrvUserAuthTableName, newSrvUserAuth).ConfigureAwait(false);
		//		return(true, objectId);
		//	}
		//}
		
		public enum eAUTH_RESULT
		{
			SUCCESS,
			WRONG_PW,
			ERROR
		}
		/// <summary>
		/// DB에서 유저 검증
		/// </summary>
		/// <param name="id"></param>
		/// <param name="pw"></param>
		/// <returns></returns>
		public async Task<(eAUTH_RESULT authResult, ObjectId objectId)> LoadingUserAuthDbAsync(string id, string pw)
		{
			var filter = Builders<SrvUserAuthM>.Filter.Eq(dt => dt.id, id);			

			var updateDef = Builders<SrvUserAuthM>.Update
					.SetOnInsert(auth => auth.DB_OBJECT_ID, ObjectId.GenerateNewId())
					.SetOnInsert(auth => auth.id, id)
					.SetOnInsert(auth => auth.hashedPw, AuthM.GetHashPassword(pw));			

			var projection = Builders<SrvUserAuthM>.Projection.Include(dt => dt.DB_OBJECT_ID).Include(dt => dt.hashedPw);
			var srvUserAuth = await DBManagerM.Instance.DbMgr.GetOrCreateAsync<SrvUserAuthM>(SrvGlobal.gSrvUserAuthTableName, filter, projection, updateDef).ConfigureAwait(false);

			if (srvUserAuth != null)
			{
				//var projection = Builders<SrvUserAuthM>.Projection.Include(dt => dt.DB_OBJECT_ID).Include(dt => dt.hashedPw);
				//var srvUserAuth = await DBManagerM.Instance.DbMgr.GetAsync<SrvUserAuthM>(SrvGlobal.gSrvUserAuthTableName, filter, projection).ConfigureAwait(false);
				
				var hashedPw = srvUserAuth.hashedPw;
				if (AuthM.IsPassed(pw, hashedPw) == false)
				{
					
					//return (eAUTH_RESULT.WRONG_PW, ObjectId.Empty); // bjectId.Empty는 비밀번호가 틀린 경우
					return (eAUTH_RESULT.WRONG_PW, srvUserAuth.DB_OBJECT_ID); // 임시 처리 해줌
				}

				return (eAUTH_RESULT.SUCCESS, srvUserAuth.DB_OBJECT_ID);
			}

			return (eAUTH_RESULT.ERROR, ObjectId.Empty);
		}

		//public void LoginFin(in MemPacketM memPk)
		//{
		//	uint pid = memPk.PkHead.Pid;

		//	// pid가 보낸게 아니면 컷

		//	SrvUserM srvUser = memPk.U as SrvUserM;


		//	// 서버 App에서 서버 유저 스타트 - 로그인후에 처리되어야 할 내용들 기술하는데 씀
		//	AppStartSrvUserLoginFinish(srvUser);   // 여기서 srvUser.IsExist는 true다 


		//	// 유저 커넥션 끊는거 TimeScheduler에서 빼기 ??
		//}

		//public void VersionCheck(in MemPacketM memPk)  // memPk.U <== 값이 없으므로 주의
		//{
		//	// memPk.U <== 값이 없으므로 주의

		//	// 프로그램 넘버 체크
		//	if (ServerM.CheckUiqueProgramNumber(memPk.PkHead.Pid) == false)
		//	{
		//		System.Diagnostics.Debug.WriteLine($"###유니크 프로그램 넘버가 다름 서버:{ServerM.clientVersion}:클라:{memPk.PkHead.Pid}####");
		//		memPk.Tc.Close();
		//		//throw new ArgumentException($"유니크 프로그램 넘버가 다름다능~ 서버:{ServerM.clientVersion}:클라:{memPk.PkHead._pid}");
		//	}

		//	int version = BitConverter.ToInt32(memPk.ConData, 0);

		//	bool bVersionOk = false;
		//	if (ServerM.clientVersion == version)
		//	{
		//		bVersionOk = true;
		//	}

		//	CompressAndEncryptManM.TryGetValue(memPk.Tc, out CompressAndEncryptM compEnc);

		//	byte[] data = BitConverter.GetBytes(bVersionOk);
		//	SendPacketGroupM.SendPacket(0, memPk.Tc, AbNetworkBase.uniqueProgramNumber, PACKET_TYPE.PC_VERSION_CHECK_RESULT, data, compEnc); // 암호화 해서 보낸다.
		//}


		public async ValueTask SendEncMemPk(EncMemPacketM encMemPk, CancellationTokenSource cts)
		{
			var tc = encMemPk._tc;
			SrvUserM tmpSrvUser = SrvGlobal.GetUser(tc);
			CompressAndEncryptM compEnc = null;
			if (tmpSrvUser.IsExist)
			{
				compEnc = tmpSrvUser.CompEnc;
			}
			else
			{
				CompressAndEncryptManM.TryGetValue(tc, out compEnc);
			}

			var memPk = encMemPk.MakeMemPacket(compEnc);
			await SendMemPk(memPk, cts).ConfigureAwait(false);
		}

		public async ValueTask SendMemPk(MemPacketM memPk, CancellationTokenSource cts)
		{
			TcpClient tmpTc = memPk.Tc;

			SrvUserM tmpSrvUser = SrvGlobal.GetUser(tmpTc);
			var pkType = (PACKET_TYPE)memPk.ConHead.PacketType;

			if (tmpSrvUser.IsExist == true)
            {
                // 최종 패킷 시간 설정 - 허트비트 대신에 유저 킥하는데 사용
                tmpSrvUser.WritePkTimeNow();
                memPk.U = tmpSrvUser; // 유저 세팅

                //if (pkType == PACKET_TYPE.PS_LOGIN_FIN)
                //{
                //    LoginFin(memPk);
                //    return;
                //}
                SendPacketGroupM.SendMemPacket(tmpSrvUser.Oid, memPk);
                
            }
            else
			{
				if (IsAllowedPacketNotLogined(pkType) == false)   // 로그인전
				{
					System.Diagnostics.Debug.WriteLine("클라에서 보낸 패킷이 문제가 있어서 접속 종료!");
					tmpTc.Close();
					return;
				}

				//if(pkType == PACKET_TYPE.PSC_RSA)
				//{
				//	OnRequestRSA(memPk);
				//}
				//else if (pkType == PACKET_TYPE.PSC_COMP_ENC_CHANGE)
				//{
				//	CompressAndEncryptChangeForServer(memPk);
				//}
				//else if (pkType == PACKET_TYPE.PS_LOGIN) // 로긴 패킷일 경우
				//{
				//	await Login(memPk).ConfigureAwait(false);
				//}
				//else if (pkType == PACKET_TYPE.PS_VERSION_CHECK)
				//{
				//	VersionCheck(memPk);
				//}
				//else
				//{
				SendPacketGroupM.SendMemPacket(0, memPk); // Pid가 로그인 이후에 유니크하게 배정됨(로그인 때는 pid는 게임 식별자로 쓰임)
				//}
			}
		}


		////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////        
		/// <summary>
		/// ServerM 생성자
		/// </summary>
		//static public ActionBlock<MemPacketM> [] gMemPkActionBlockArray;

		public static int gCpuCore;  // cpu 코어 개수
		string _ipAdress;
		int _port;

		// 서버 틱 관련
		public bool BSendServerTick { get; set; } = false;

		public long _startServerTick;
		

		static public int clientVersion;

		/// <summary>
		/// 상속받은 앱 서버에서 클라이언트 버전을 잊을 수 있으므로 반드시 구현하도록 추상 클래스로 선언한다
		/// </summary>
		/// <returns></returns>
		public abstract int SetClientVersion();  // 상속 구현해야 함 (앱 테이블 로딩 후 불림)

		public abstract Task<SrvTableM> LoadAppTables(); // 앱 테이블 로딩

		IniSrvOptionM _iniSrvOption;

		public async Task<SrvTableM> LoadTables()
		{
			ServerM.Debug("--------테이블 로딩 시작-----------");
			
			var srvTable = await LoadAppTables().ConfigureAwait(false);
			clientVersion = SetClientVersion();    // 테이블에서 읽기 때문에 순서 중요

			return srvTable;
		}

		public ServerM(uint uniquePrgNum, string srvIp = null, int port = 0, string pathOptionFile = null) // uiqueProgramNumber 라이브러리로 클라/서버 만들때 최초 패킷에 VERSION_CHECK 패킷에 pid로 담아 보냄 !!! 
		{
			if (uniquePrgNum <= 0)
			{
				throw new ArgumentException("유니크 프로그램 넘버는 1이상이어야 함");
			}

			AbNetworkBase.uniqueProgramNumber = uniquePrgNum;
			_iniSrvOption = new(srvIp, port, pathOptionFile);

			
		}

		public async ValueTask LoadIniFile()
		{
			await _iniSrvOption.LoadIni().ConfigureAwait(false);

			_ipAdress = IniOptionM.gIpAddress;
			_port = IniOptionM.gPort;

			gCpuCore = Environment.ProcessorCount;

			System.Diagnostics.Debug.WriteLine($"############# Server IP :{_ipAdress} Port:{_port}#################");

		}
		public async ValueTask<bool> ServerStart()
		{
			// 로그 설정
			logM = new Log4NetM("ServerM", "log4net.config", udpLogIp);
			var srvTable = await LoadTables();

			// 서버 테이블 읽기
			if (srvTable == null)
			{
				System.Diagnostics.Debug.WriteLine("테이블 Loading 오류");
				System.Diagnostics.Debug.Assert(false, "테이블 Loading 오류");
			}

			// Ini 파일 로딩
			await LoadIniFile();


			// 패킷 allowedPacketMan 만들기
			_allowedPkMan = _CreateAllowedPacketMan();

			// 패킷 처리기 만들기 (상속해서 만들어야 함)
			_memPkDispatcher = _MakeMemPkDispatcher();
			_memPkDispatcher.LoadActions();

			//gMemPkActionBlockArray = new ActionBlock<MemPacketM>[_iCntCpuCore];
			//// gMemPkActionBlock 초기화
			//for (int i=0; i<_iCntCpuCore; i++)
			//{
			//    gMemPkActionBlockArray[i] = new ActionBlock<MemPacketM>(MemPkDispatcher.MemPkAction);
			//}

			// 스타트 서버틱 설정
			_startServerTick = TickTimeM.GTick;

			// 글로벌 타이머 시작
			gTimeScheduler = new(1000);
			gTimeScheduler.StartLongRunning(100);

			// 서버 시작
			AsyncServerReady();

			return true;
		}

		// 상속 받아 앱 종료 구현
		abstract protected ValueTask ServerAppClose();

		public async ValueTask ServerClose()
		{
			//Func<SrvUserM, CancellationToken, ValueTask> val;
			//Parallel.ForEachAsync(_dicUser.Values, val = (srvUser, tk) =>
			//{
			//    srvUser.RequestDisconnectForce();
			//    return ValueTask.CompletedTask;
			//});

			// LogManager가 초기화되어 있는지 확인
			if (LogManager.GetRepository() != null)
			{
				LogManager.Shutdown();
			}

			// 글로벌 타임 스케줄러 종료					
			// 스케쥴러에 등록된 것들의 리소스가 먼저 제거되면 안되니 우선적으로 정지시킨다.
			gTimeScheduler?.Dispose();


			_cts.Cancel();

			
			// 유저 정리
			Action<InnerSrvUserM> val;
			Parallel.ForEach(SrvGlobal.dicSrvUsers.Values, (srvUser) =>
			{
				srvUser.RequestDisconnectForce();
				//return ValueTask.CompletedTask;
				return;
			});

			SrvGlobal.dicSrvUsers.Clear();
						
			gDisconnectTimer.DisposeAllTimer();

			await ServerAppClose().ConfigureAwait(false); // 맵이 살아 있어야 유저를 빼기 때문에 여기서
						

			_cts.TryReset();  // 재사용
		}


		async Task AsyncServerReady()
		{
			TcpListener tcpListener = null;
			try
			{
				tcpListener = new TcpListener(IPAddress.Parse(_ipAdress), _port);
				tcpListener.Start();
				while (true)
				{
					try
					{
						//using var control = ExecutionContext.SuppressFlow(); // 추후 AsyncLocal<T> 사용시 추가 할 것
						TcpClient tcpCl = await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);

						// SetKeepAlive 5초 뒤, 5초 간격으로 확인
						//SetKeepAlive(tcpCl.Client, true, 5000, 5000); 

						// Nagle 알고리즘 비활성화
						tcpCl.NoDelay = true;

						Task.Run(async () => await IoPipelineSrvM.PipelineForServerAsync(tcpCl, this).ConfigureAwait(false));
					}
					catch (ObjectDisposedException ex)
					{
						System.Diagnostics.Debug.WriteLine("TcpListener has been stopped: " + ex.Message);
						break;
					}
					catch (InvalidOperationException ex)
					{
						System.Diagnostics.Debug.WriteLine("TcpListener is not started: " + ex.Message);
						break;
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine("Unexpected error while accepting TcpClient: " + ex.Message);
					}
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(ex.Message);
			}
			finally
			{
				if (tcpListener != null)
					tcpListener.Stop();
			}
		}

		// ServerM 상속받은 앱의 최초 실행 펑션 세팅 - 상속받으면 실행해준다
		protected virtual void AppStartSrvUser(SrvUserM srvUser) { }

		public virtual void AppStartSrvUserLoginFinish(SrvUserM srvUser) { }

		protected virtual void FinishSrvUser(SrvUserM srvUser) { }

		/// <summary>
		/// 유저가 Disconnect 되어서 서버에서 User관련 내용을 지우기 위해서 호출 됨
		/// </summary>
		/// <param name="srvUser"></param>
		public void AppUserFinish(SrvUserM srvUser) // srvUser.IsExsit는 true
		{
			FinishSrvUser(srvUser);
		}


		// 서버내에서 유저가 로그인 했을 때 실행 되는 함수
		public void StartSrvUser(SrvUserM srvUser)
		{
			AppStartSrvUser(srvUser);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="pgNumber"></param>
		/// <returns></returns>
		public static bool CheckUiqueProgramNumber(uint pgNumber)
		{
			return (AbNetworkBase.uniqueProgramNumber == pgNumber);
		}


		/// <summary>
		/// 서버 유저수 
		/// </summary>
		// 서버 유저 수
		public static int _iCntTotalServerUser;

		public int GetCntServerUsers() { return _iCntTotalServerUser; }

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void IncrementServerUserCnt()
		{
			var cntServerUsers = Interlocked.Increment(ref _iCntTotalServerUser);
			System.Diagnostics.Debug.WriteLine($"서버현재 유저수 : {cntServerUsers}");

			//if(_iCntTotalServerUser % 500 == 0)
			//    MessageBox.Show($"서버현재 유저수 : {_iCntTotalServerUser}", "제목", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void DecrementServerUserCnt()
		{
			Interlocked.Decrement(ref _iCntTotalServerUser);
		}



	}

	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 패킷 관련 처리기
	/// </summary>

	public class DoPkVersionCheck : AbMemPkAction
	{
		public DoPkVersionCheck(PACKET_TYPE ePacketType) : base(ePacketType) { }
		public override Task MemPkAction(MemPacketM memPk)
		{
			//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////    
			// memPk.U <== 값이 없으므로 주의

			// 프로그램 넘버 체크
			if (ServerM.CheckUiqueProgramNumber(memPk.PkHead.Pid) == false)
			{
				System.Diagnostics.Debug.WriteLine($"###유니크 프로그램 넘버가 다름 서버:{ServerM.clientVersion}:클라:{memPk.PkHead.Pid}####");
				memPk.Tc.Close();
				//throw new ArgumentException($"유니크 프로그램 넘버가 다름다능~ 서버:{ServerM.clientVersion}:클라:{memPk.PkHead._pid}");
			}

			int version = BitConverter.ToInt32(memPk.ConData, 0);

			bool bVersionOk = false;
			if (ServerM.clientVersion == version)
			{
				bVersionOk = true;
			}

			CompressAndEncryptManM.TryGetValue(memPk.Tc, out CompressAndEncryptM compEnc);

			byte[] data = BitConverter.GetBytes(bVersionOk);
			SendPacketGroupM.SendPacket(0, memPk.Tc, AbNetworkBase.uniqueProgramNumber, PACKET_TYPE.PC_VERSION_CHECK_RESULT, data, compEnc); // 암호화 해서 보낸다.

			return Task.CompletedTask;
		}
	}

	public class DoPkRSAForSever : AbMemPkAction
	{
		public DoPkRSAForSever(PACKET_TYPE ePacketType) : base(ePacketType) { }
		public override Task MemPkAction(MemPacketM memPk)
		{
			var publicKeyMadeByClient = Encoding.UTF8.GetString(memPk.ConData);  // 클라에서 보낸 공개키

			// 서버의 
			using (var rsa = new RSACryptoServiceProvider(2048))
			{
				var privateKeyMadeByServer = rsa.ToXmlString(true); // 개인키 저장
				var publicKey = rsa.ToXmlString(false); // 클라에 전달할 공개키
				var bytePublicKey = Encoding.UTF8.GetBytes(publicKey);

				// 인크립트 클래스 생성 후 등록
				var compEnc = new CompressAndEncryptM(privateKeyMadeByServer, publicKeyMadeByClient, default, default);
				CompressAndEncryptManM.TryAdd(memPk.Tc, compEnc);

				SendPacketGroupM.SendPacket(0, memPk.Tc, 1, PACKET_TYPE.PSC_RSA, bytePublicKey, null); // 암호화 하지 않음, 클라로 공개키 보냄
			}

			return Task.CompletedTask;
		}
	}

	public class DoPkCompressAndEncryptForServer : AbMemPkAction
	{
		public DoPkCompressAndEncryptForServer(PACKET_TYPE ePacketType) : base(ePacketType) { }
		public override Task MemPkAction(MemPacketM memPk)
		{

			if (CompressAndEncryptManM.TryGetValue(memPk.Tc, out var compEnc))
			{
				// RSA 개인키로 푼다
				using (var rsa = new RSACryptoServiceProvider())
				{
					compEnc.CreateEncDecType(CompressAndEncryptM.ENCRYPT_TYPE.XOR, CompressAndEncryptM.ENCRYPT_TYPE.AES); // 서버는 Enc는 XOR, Dec는 AES			

					rsa.FromXmlString(compEnc.RSAPrivateKeyMadeByServer);
					var claAesInfo = rsa.Decrypt(memPk.ConData, RSAEncryptionPadding.Pkcs1);

					FbsEncryptKey encryptKey = FsEncryptKeyFactory.Deserialize(claAesInfo).Value;
					compEnc.SetAesKey(encryptKey.GetKeyArray(), encryptKey.GetIvArray());
				}

				using (RSA rsa = RSA.Create())
				{
					rsa.FromXmlString(compEnc.RSAPublicKeyMadeByClient);    // 클라에서 받은 공개키로 초기화
					var sendEcryptKeyData = new FsEncryptKeyFactory(compEnc.XorKey, null).Serialize();

					sendEcryptKeyData = rsa.Encrypt(sendEcryptKeyData, RSAEncryptionPadding.Pkcs1);  // 클라에서 받은 공개키로  XOR 키정보 암호화
					SendPacketGroupM.SendPacket(0, memPk.Tc, 1, PACKET_TYPE.PSC_COMP_ENC_CHANGE, sendEcryptKeyData, null); // 암호화 하지 않음
				}
			}
			else
			{
				throw new Exception("버그M: CompressAndEncryptChangeForServer, CompAndEnc 등록된 객체 없음)");
			}

			return Task.CompletedTask;
		}
	}

	public class DoPkLogin : AbMemPkAction
	{
		ServerM _serverM;
		public DoPkLogin(PACKET_TYPE ePacketType, ServerM serverM) : base(ePacketType) 
		{ 
			_serverM = serverM;
		}
		public override async Task MemPkAction(MemPacketM memPk)
		{

			// 프로그램 넘버 체크
			if (ServerM.CheckUiqueProgramNumber(memPk.PkHead.Pid) == false)
			{
				if (memPk.Tc.Connected)
				{
					System.Diagnostics.Debug.WriteLine($"###유니크 프로그램 넘버가 다름 서버:{ServerM.clientVersion}:클라:{memPk.PkHead.Pid}####");
					memPk.Tc.Close();
				}
				return; 
			}

			FbsLogInIdPw loginIdPw = PacketM.DeserializeLoginIdPw(memPk.ConData);

			////////////////////////////////////////////////////////
			//// 클라 버전 검증 - 
			////////////////////////////////////////////////////////                       

			// tc와 매칭된  Encrypt 얻기
			CompressAndEncryptManM.TryRemove(memPk.Tc, out CompressAndEncryptM compEnc);

			if (ServerM.clientVersion != loginIdPw.Version)
			{
				byte[] data = BitConverter.GetBytes(false);
				SendPacketGroupM.SendPacket(0, memPk.Tc, AbNetworkBase.uniqueProgramNumber, PACKET_TYPE.PC_VERSION_CHECK_RESULT, data, compEnc); // 암호화 해서 보낸다

				// PACKET_TYPE.PSC_RQ_DISCONNECT 호출 해야 될 수도 있음 추후 확인 (직접 memPk.Tc.Close()를 진행하면 패킷이 안갈듯?)
				return; 
			}

			////////////////////////////////////////////////////////
			//// ip 비번 검증 - 추후 검증해서 자를것
			////////////////////////////////////////////////////////

			// 여기서 잘라 memPk.Tc.Close(); return;

			/////////////////////////////////////////////////////////
			var id = loginIdPw.Id;
			var pw = loginIdPw.Pw;

			ObjectId objectId = ObjectId.Empty;
			var resLoad = await _serverM.LoadingUserAuthDbAsync(id, pw).ConfigureAwait(false);
			objectId = resLoad.objectId;

			if (resLoad.authResult == eAUTH_RESULT.WRONG_PW)
			{
				ServerM.logM.Debug($"로그인 실패인데 로딩 그냥 해줌(추후변경 예정) - 비밀번호 틀림: {id}");
				// return; // 비밀번호 틀리면 패킷 보내야 됨 - 임시 주석

			}
			else if (resLoad.authResult == eAUTH_RESULT.SUCCESS)
			{
				ServerM.logM.Debug($"로그인 성공 비번도 맞음: {id}");
			}
			else if (resLoad.authResult == eAUTH_RESULT.ERROR)
			{
				throw new Exception("LoadingUserAuthDb Fail....");
			}

			var tc = memPk.Tc;
			InnerSrvUserM innerSrvUser = new InnerSrvUserM(tc);
			innerSrvUser.AllowedPkState = _serverM.GetFirstUserPacketState();
			innerSrvUser.Id = id;
			innerSrvUser.DB_ID = objectId; // DB에 저장된 db 아이디

			//// 유니크 Packet ID 발급
			uint uniquePid;
			ServerM.MakePkId(out uniquePid);
			innerSrvUser.Pid = uniquePid;

			// 유니크 Oid 발급
			innerSrvUser.MakeOid();

			// 서버 유저 Encrypt 설정            
			innerSrvUser._compEnc = compEnc;

			// 서버유저 추가
			_serverM.IncrementServerUserCnt();

			// 유저 등록                                 
			SrvGlobal.AddUser(tc, innerSrvUser);

			var srvUser = new SrvUserM(innerSrvUser); // 서버 유저 객체 생성			

			// 최초 ServerM Start함수 콜 - 
			// 서버 상속받은 앱 시작 AppStartSrvUser() 펑션 호출 - override 한 경우만 실행됨 
			_serverM.StartSrvUser(srvUser);

			var sendData = new FsLoginOkFactory(innerSrvUser.Id, innerSrvUser.Oid, Stopwatch.Frequency).Serialize();
			innerSrvUser.SerializeSendPacket(PACKET_TYPE.PC_LOGIN_OK, sendData);    // 암호화

			return;
		}
		
	}

	public class DoPkLoginFin : AbMemPkAction
	{
		ServerM _serverM;
		public DoPkLoginFin(PACKET_TYPE ePacketType, ServerM serverM) : base(ePacketType)
		{
			_serverM = serverM;
		}
		public override async Task MemPkAction(MemPacketM memPk)
		{
			uint pid = memPk.PkHead.Pid;

			// pid가 보낸게 아니면 컷

			SrvUserM srvUser = memPk.U as SrvUserM;


			// 서버 App에서 서버 유저 스타트 - 로그인후에 처리되어야 할 내용들 기술하는데 씀
			_serverM.AppStartSrvUserLoginFinish(srvUser);   // 여기서 srvUser.IsExist는 true다 


			// 유저 커넥션 끊는거 TimeScheduler에서 빼기 ??
		}
	}

	public class DoPkHeartBitAlive : AbMemPkAction
	{
		public DoPkHeartBitAlive(PACKET_TYPE ePacketType) : base(ePacketType) { }
		public override Task MemPkAction(MemPacketM memPk)
		{
			//SrvUserM srvUser = ServerM.GetUser(memPk.Tc);
			//srvUser._dicTimerM.ChangeTimer(eTimerMType.HEART_BIT_ALIVE_CHECK, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);    // 타이머 무기한 연장                

			//Debug.WriteLine($"\n허트비트 얼라이브 도착 - 타이머 연장:{srvUser.id}: {DateTime.Now}");

			return Task.CompletedTask;

		}
	}

	public class DoPkRspServerTick : AbMemPkAction
	{
		public DoPkRspServerTick(PACKET_TYPE contentType) : base(contentType) { }

		public override Task MemPkAction(MemPacketM memPk)
		{
			var srvUser = memPk.U as SrvUserM;
			srvUser.netDelay?.RecvServerTick();
			return Task.CompletedTask;
		}
	}


}

