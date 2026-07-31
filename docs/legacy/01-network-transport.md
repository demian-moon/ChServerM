# 01 — 네트워크 / 전송 계층

**대상**: `ServerM.cs`(983), `IoPipelineSrvM.cs`(416), `PublicLib/NetWorkM.cs`(100), `ServerGlobals.cs`(195), `UserSrvM.cs`(165), `SendPacketGroupM.cs`(110), `NetWorkDelayM.cs`(87), `TimerSrvM.cs`(130), `IniOptionSrvM.cs`(40), `PublicLib/CommonInterfaceM.cs`(25), `PublicLib/SrvClaFuncM/SrvClaFuncM.cs`(28)

**전량 정독 완료.** 판정은 확정이다.

## 이 계층의 전체 흐름

```
TcpListener.AcceptTcpClientAsync()            ServerM.AsyncServerReady()
   │ NoDelay = true
   ▼
Task.Run( IoPipelineSrvM.PipelineForServerAsync )   ← 커넥션당 fire-and-forget
   │
   ├─ SrvFillPipeAsync   : NetworkStream → PipeWriter
   └─ SrvReadPipeAsync   : PipeReader → 5단 상태 머신 프레이밍
        │  eToReadState { PK_HEAD → CONTENT_HEAD → CONTENT_DATA }
        │                 { ENC_PK_HEAD → ENC_PK_DATA }  (암호 모드 전환 후)
        ▼
     ServerM.SendMemPk(memPk) / SendEncMemPk(encMemPk)
        │  ├─ 유저 있음 → WritePkTimeNow(), memPk.U = srvUser
        │  └─ 유저 없음 → IsAllowedPacketNotLogined() 검사 실패 시 Tc.Close()
        ▼
     SendPacketGroupM.SendMemPacket(oid, memPk)
        │  idx = oid % _iCntIncomeBlock        ← 유저별 순서 보장의 핵심
        ▼
     _arrActBlockIncome[idx].Post(memPk)      ConcurSeqTaskContextExecLongRunM<MemPacketM>
        ▼
     MemPkDispatcher.MemPkAction → AbMemPkAction 파생 핸들러

송신은 대칭:
     InnerSrvUserM.SerializeSendPacket / FlushSendBuffer
        ▼
     SendPacketGroupM.SendPacket(oid, ...)   idx = oid % _iCntOutGoingBlock
        ▼
     _arrActBlockOutGoing[idx].Post(finalPkData) → PacketM.SendPacket
```

**핵심 구조 요약**: 수신·송신을 각각 **oid 기반 샤드 배열**로 나누고, 같은 유저의 패킷을 항상 같은 샤드에 넣어 순서를 보장한다. 샤드 개수는 `프로세서 수 × 설정 팩터`.

---

## `ServerM`

`ServerM.cs:31`, `abstract class ServerM : AbNetworkBase`

### 동작

서버의 부트스트랩과 생명주기를 담당하는 추상 기반 클래스. 앱은 이걸 상속해 구현한다 (**Template Method**).

**강제 구현(abstract)**
| 멤버 | 역할 |
|---|---|
| `AddMemPkDispatcher(MemPkDispatcher)` | 앱 패킷 핸들러 등록 |
| `AddAllowedPacketMan(AllowedPacketManBuilder)` | 상태별 허용 패킷 등록 |
| `GetFirstUserPacketState()` | 로그인 직후 유저의 초기 허용 상태 |
| `SetClientVersion()` | 클라이언트 버전 (테이블 로딩 후 호출) |
| `LoadAppTables()` | 앱 테이블 로딩 |
| `ServerAppClose()` | 앱 종료 처리 |

**선택 훅(virtual)**: `AppStartSrvUser`, `AppStartSrvUserLoginFinish`, `FinishSrvUser`

**`ServerStart()` 순서** (`:523`)
1. `logM = new Log4NetM("ServerM", "log4net.config", udpLogIp)`
2. `LoadTables()` → `LoadAppTables()` → `clientVersion = SetClientVersion()`
3. `LoadIniFile()` → `_ipAdress`, `_port`, `gCpuCore = Environment.ProcessorCount`
4. `_allowedPkMan = _CreateAllowedPacketMan()`
5. `_memPkDispatcher = _MakeMemPkDispatcher()` → `LoadActions()`
6. `_startServerTick = TickTimeM.GTick`
7. `gTimeScheduler = new TimeEventSchedulerM(1000)` → `StartLongRunning(100)`
8. `AsyncServerReady()` — **await 하지 않음**

**기본 등록 패킷 핸들러** (`_MakeMemPkDispatcher`, `:74`)
`PS_HEART_BIT_ALIVE`, `PSC_RQ_DISCONNECT`, `PS_RSP_SERVER_TICK`, `PS_VERSION_CHECK`, `PSC_RSA`, `PSC_COMP_ENC_CHANGE`, `PS_LOGIN`, `PS_LOGIN_FIN`

**기본 허용 패킷 상태 기계** (`_CreateAllowedPacketMan`, `:101`)
- 전 상태 허용: `PS_HEART_BIT_ALIVE`, `PSC_RQ_DISCONNECT`
- `A_SC_NOT_LOGINED`: `PSC_RSA`, `PSC_COMP_ENC_CHANGE`, `PS_LOGIN`, `PS_VERSION_CHECK`
- `A_SC_START`: `PS_LOGIN_FIN`

**RSA + AES/XOR 하이브리드 핸드셰이크** (`DoPkRSAForSever` `:762`, `DoPkCompressAndEncryptForServer` `:787`)
1. 클라 → 서버: 클라가 만든 RSA 공개키 (`PSC_RSA`, **평문**)
2. 서버: `new RSACryptoServiceProvider(2048)` 로 키쌍 생성. `CompressAndEncryptM` 생성 후 `CompressAndEncryptManM`에 `Tc` 키로 등록. 서버 공개키를 클라에 **평문** 전송
3. 클라 → 서버: AES key/iv를 서버 공개키로 암호화해 전송 (`PSC_COMP_ENC_CHANGE`)
4. 서버: RSA 개인키로 복호 → `FbsEncryptKey` 역직렬화 → `compEnc.SetAesKey(key, iv)`
5. 서버 → 클라: XOR 키를 **클라 공개키로** 암호화해 전송
6. 이후 방향별 알고리즘: **서버→클라 = XOR**, **클라→서버 = AES**
   (`compEnc.CreateEncDecType(ENCRYPT_TYPE.XOR, ENCRYPT_TYPE.AES)`)

**로그인 흐름** (`DoPkLogin` `:825`)
프로그램 넘버 검증 → `DeserializeLoginIdPw` → 클라 버전 검증 → `LoadingUserAuthDbAsync(id, pw)` → `InnerSrvUserM` 생성 → `MakePkId`(유니크 pid) → `MakeOid` → `compEnc` 이관 → `IncrementServerUserCnt` → `SrvGlobal.AddUser` → `StartSrvUser` → `PC_LOGIN_OK` 전송(`id`, `oid`, `Stopwatch.Frequency`)

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **비밀번호 검증 실패해도 로그인 통과.** `WRONG_PW`일 때 `return`이 주석 처리됨 — `// return; // 비밀번호 틀리면 패킷 보내야 됨 - 임시 주석` | `:878~882` | 🔴 치명 |
| 2 | **RSA 공개키 교환에 인증이 없다.** 양쪽 공개키가 평문으로 오간다 → **MITM 완전 노출**. 공격자가 중간에서 자기 키를 끼우면 전체 세션 복호 가능 | `:762~823` | 🔴 치명 |
| 3 | **서버→클라가 XOR.** 반복 키 XOR은 암호화가 아니다. 알려진 평문(패킷 헤더 구조가 고정)으로 즉시 키 복원 | `:798` | 🔴 치명 |
| 4 | **`RSAEncryptionPadding.Pkcs1`** — PKCS#1 v1.5는 Bleichenbacher 패딩 오라클 공격에 취약. OAEP를 써야 한다 | `:801`, `:812` | 🔴 높음 |
| 5 | **커넥션마다 2048비트 RSA 키쌍 생성** → 연결 폭주 시 CPU 고갈. **DoS 벡터** | `:770` | 🔴 높음 |
| 6 | `RSACryptoServiceProvider` + `ToXmlString(true)` — 레거시 CSP API, 개인키를 XML 문자열로 힙에 노출. 크로스 플랫폼 비권장 | `:770~772` | 🟠 중간 |
| 7 | **`AsyncServerReady()`를 await 하지 않음** — accept 루프의 예외가 관측되지 않음 (`CS4014`) | `:562` | 🟠 중간 |
| 8 | `Task.Run(async () => await PipelineForServerAsync(...))` — 커넥션당 fire-and-forget. 예외 삼킴, 커넥션 수 제한 없음 | `:633` | 🟠 중간 |
| 9 | **`MakePkId`가 `int`를 `Interlocked.Increment` 후 `uint` 캐스팅** — 2³¹ 패킷 후 음수 → 캐스팅으로 값 급변. 주석("각 쓰레드마다 다른 값을 가진다")은 uniqueness 근거를 잘못 설명 | `:59~65` | 🟠 중간 |
| 10 | **static 상태**: `uniqueProgramNumber`, `clientVersion`, `_iCntTotalServerUser`, `logM`, `gTimeScheduler`, `gCpuCore` → **프로세스당 서버 인스턴스 1개**. 테스트 격리 불가 | 전역 | 🟠 중간 |
| 11 | 하드코딩된 UDP 로그 IP가 주석으로 남아 있음 (`39.117.205.158`) | `:34` | 🟡 낮음 |
| 12 | `ServerClose()`에서 `_cts.TryReset()` — CTS 재사용. 취소 상태가 남은 채 재시작될 위험 | `:609` | 🟡 낮음 |
| 13 | `Parallel.ForEach` 앞에 `Action<InnerSrvUserM> val;` 선언 후 미사용 (죽은 코드) | `:594` | 🟡 낮음 |
| 14 | `using Microsoft.CodeAnalysis.Text;` — Roslyn 참조가 서버 코어에 유입 | `:5` | 🟡 낮음 |
| 15 | 로그인/버전체크/RSA 로직이 **주석으로 중복 보존** (`:132~300`, `:345~385`). 실제 구현은 `AbMemPkAction` 파생 클래스로 이동했으나 원본이 남아 혼란 | 다수 | 🟡 낮음 |
| 16 | `System.Diagnostics.Debug.WriteLine` 로깅 — Release에서 소멸. `logM`이 있는데도 혼용 | 전역 | 🟠 중간 |

### 개선점 (ChServerM)

- **Template Method 상속 강제를 폐기하고 `ServerBuilder` 조립으로 대체** (Phase 2). `abstract` 6개를 상속으로 강요하면 앱이 프레임워크에 묶인다. 옵션·델리게이트·DI 등록으로 바꾼다
- **핸드셰이크 전면 재설계** (Phase 9): TLS(`SslStream`)로 전송 보안을 위임하는 것이 1순위. 자체 프로토콜이 필요하면 인증된 키 교환(서버 인증서 + ECDHE) + 양방향 AEAD(AES-GCM/ChaCha20-Poly1305). XOR·PKCS#1 v1.5·커넥션당 RSA 생성은 전부 제거
- **인증을 미들웨어로 분리** (`IAuthenticator`, Phase 9). 검증 실패는 예외 없이 `TryXxx`로 처리하고 **반드시 연결을 끊는다**
- 부트스트랩 순서를 `ServerBuilder`의 검증 단계로 옮기고, **잘못된 조합은 시작 시점에 실패**시킨다 (Phase 2)
- accept 루프는 `IServerTransport` 구현으로 이동. 커넥션 상한·admission control 적용 (Phase 10)
- `MessageId`는 강타입 `readonly struct` + `ulong` 기반으로 (Phase 1 ID 타입)
- static 전역 제거 → DI 스코프. 테스트에서 서버 여러 개를 띄울 수 있어야 한다

### 판정

🟡 **개작**. 설계 골격(Template Method → Builder, 상태별 화이트리스트, 기본 패킷 세트)은 승계 가치가 높지만 **구현은 전량 재작성**. 보안 부분은 승계할 것이 없다.

→ Phase 2 (조립), Phase 5 (accept 루프), Phase 9 (보안·인증)

---

## `IoPipelineSrvM`

`IoPipelineSrvM.cs:84`, `static class`

### 동작

`System.IO.Pipelines` 기반 수신 파이프라인. 커넥션당 1회 호출된다.

**`PipelineForServerAsync(TcpClient, ServerM)`** (`:94`)
```csharp
PipeOptions pipeOption = new(null, null, null, -1, -1, -1, false);  // 스레드풀 실행
Pipe pipe = new(pipeOption);
Task pipeWritingTask = SrvFillPipeAsync(tc, pipe.Writer, cts, new ServerDisconnectProcess(serverM));
Task pipeReadingTask = SrvReadPipeAsync(tc, pipe.Reader, cts, serverM);
await Task.WhenAll(pipeWritingTask, pipeReadingTask);
tc?.Close(); tc?.Dispose();
```

**`SrvFillPipeAsync`** (`:112`) — `NetworkStream.ReadAsync(pipeWriter.GetMemory(512))` 루프. `nReadByte == 0`(EOF) 시 `cts.Cancel()` + `DisconnectProcess`. 종료 시 `Shutdown(Both)` → `netStream.Close/Dispose` → `CompressAndEncryptManM.TryRemove` → `pipeWriter.CompleteAsync()`

**`SrvReadPipeAsync`** (`:226`) — 5단 상태 머신
| 상태 | 읽는 것 | 다음 |
|---|---|---|
| `PK_HEAD` | `PacketM.gPkHeadLen` 바이트 → `DeserializePkHead` → `IsValidCheckSum` | `CONTENT_HEAD` (길이 = `_pkHead.ConHeadLen`) |
| `CONTENT_HEAD` | `DeserializeContentHead` | `CONTENT_DATA` (길이 = `_conHead.ConDataLen`). **길이 0이면 즉시 MemPk 생성 후 `PK_HEAD`로** |
| `CONTENT_DATA` | 페이로드 → `new MemPacketM(...)` → `serverM.SendMemPk` | `PACKET_TYPE.PSC_COMP_ENC_CHANGE`였으면 `ENC_PK_HEAD`, 아니면 `PK_HEAD` |
| `ENC_PK_HEAD` | `PacketM.gEncHeadLen` → `FbsEncryptHeadM.GetRootAsFbsEncryptHeadM` | `ENC_PK_DATA` (길이 = `EncDataLen`) |
| `ENC_PK_DATA` | 암호 페이로드 → `new EncMemPacketM(...)` → `serverM.SendEncMemPk` | `ENC_PK_HEAD` |

**`ServerDisconnectProcess : AbDisconnectProcess`** (`:45`) — 유저가 등록돼 있으면 즉시 정리(`DecrementServerUserCnt` → `DisconnectProcess` → `AppUserFinish` → `RemoveUser`), 아직 없으면 `gDisconnectTimer`에 `TimerM_SrvUser_Delay_Disconnect`를 1초 뒤로 등록.

> **이 지연 타이머는 실전에서 나온 대응이다.** 로그인 패킷 처리 중에 연결이 끊기면 유저가 딕셔너리에 없어 정리를 못 한다. 새 구현에도 **동등한 보장이 필요하다** — 없으면 유저 카운트가 새고 리소스가 남는다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`viewBuffer.ToArray()` — 매 패킷 힙 할당.** 4곳. `ReadOnlySequence`를 배열로 복사한다 | `:300`, `:317`, `:323`, `:343` | 🔴 높음 |
| 2 | **`ArrayPool` 반납 누수.** `pooledEncHeadBuf`는 `ENC_PK_HEAD`에서 Rent → `ENC_PK_DATA`에서 Return. **이터레이션을 넘겨 대여**하므로 사이에 `break`/예외 발생 시 미반납. `Return`이 `try/finally` 밖 | `:271~290` | 🔴 높음 |
| 3 | **프레이밍 desync.** 파싱 예외를 `catch`한 뒤 **`break` 없이 루프를 계속**한다. `_toReadState`가 어긋난 채로 스트림 파싱을 이어가 이후 모든 프레임이 깨진다 | `:379~390` | 🔴 높음 |
| 4 | **체크섬 실패를 `throw new Exception()`으로 처리** — 핫패스 예외. 게다가 위 #3 때문에 그 예외가 삼켜진다 | `:310` | 🔴 높음 |
| 5 | **최대 프레임 크기 상한이 없다.** `_toReadDataLen = _conHead.ConDataLen`을 그대로 신뢰 → 조작된 길이 필드로 **메모리 고갈 공격** 가능 | `:320` | 🔴 높음 |
| 6 | 백프레셔 미설정. `PipeOptions(null,null,null,-1,-1,-1,false)` — pause/resume 임계값을 기본값에 방치. `minBufferSize = 512`는 과소 | `:99`, `:114` | 🟠 중간 |
| 7 | `Debug.WriteLine` 로깅. 메시지도 프로덕션 부적합("뭔일이래!!", "룰루~~", "띠로리~~~링") | 전역 | 🟠 중간 |
| 8 | `finally`의 `pipeReader.AdvanceTo(buffer.Start, buffer.End)` — 예외 경로에서도 실행된다. `result.Equals(default)` 검사로 방어하지만 `ReadResult`가 struct라 `Equals` 박싱 발생 | `:395~398` | 🟡 낮음 |
| 9 | 암호 모드 전환 판정이 **`CONTENT_DATA` 처리 안에 하드코딩** (`_conHead.PacketType == PSC_COMP_ENC_CHANGE`). 프레이밍이 패킷 의미를 안다 = 계층 위반 | `:349` | 🟠 중간 |
| 10 | `MemPkFactoryForServer` 클래스 전체가 주석 처리된 채 남아 있음 | `:17~39` | 🟡 낮음 |

### 개선점 (ChServerM)

- **`ReadOnlySequence<byte>` → `Span` 직접 파싱**으로 `ToArray()` 전량 제거. 헤더는 고정 `struct` + `MemoryMarshal`(ADR-0002)
- **대여 소유권을 타입으로 표현.** `IMemoryOwner<T>` 또는 `ref struct` 스코프로 "누가 반납하는가"를 컴파일러가 강제하게 (Phase 3). DEBUG 빌드 누수 감지 병행
- **프레임 오류 = 커넥션 종료.** `TryXxx` 반환으로 처리하고 오류 시 즉시 닫는다. 예외로 흐름 제어하지 않는다 (Phase 4)
- **최대 프레임 크기 상한 필수** (Phase 4). 상한 없는 length-prefix는 공격 벡터
- 백프레셔 pause/resume 임계값 **명시적 설정** (Phase 5)
- **암호 모드 전환을 프레이밍에서 분리.** 프레이밍은 프레임 경계만 알고, 복호화는 미들웨어/데코레이터로 (Phase 4·9)
- 5단 상태 머신 구조 자체는 승계. 다만 `ENC_*` 경로는 AEAD 재설계에 맞춰 재구성
- 지연 disconnect 처리는 **커넥션 생명주기 상태 기계**로 승격 (Phase 5)

### 판정

🟡 **개작**. Pipelines 표준 패턴과 5단 프레이밍 상태 기계, 지연 종료 대응은 승계. 할당·누수·desync·상한 부재는 전부 재작성.

→ Phase 4 (프레이밍), Phase 5 (TCP 전송)

---

## `AbNetworkBase`

`PublicLib/NetWorkM.cs:17`, `abstract class`

### 동작

서버·클라이언트 공용 기반. 상태는 **전부 static**.

| 멤버 | 타입 | 역할 |
|---|---|---|
| `gDisconnectTimer` | `static TimerM<TcpClient>` | 종료 처리 타이머 |
| `uniqueProgramNumber` | `static uint` | 서버·클라가 공유하는 식별자. 다르면 접속 거부 |
| `_allowedPkMan` | `static protected AllowedPacketMan` | 패킷 화이트리스트 |
| `_memPkDispatcher` | `protected MemPkDispatcher` | 인스턴스 필드 |
| `_cts` | `public CancellationTokenSource` | |

`IsAllowedPacket(UserM, PACKET_TYPE)` / `IsAllowedPacketNotLogined(PACKET_TYPE)` — 화이트리스트 조회
`GetMyIP()` — `Dns.GetHostEntry(Dns.GetHostName())`에서 첫 IPv4
`SetKeepAlive(Socket, bool, int, int)` — `IOControlCode.KeepAliveValues`로 12바이트 구조체 설정

> `uniqueProgramNumber` 주석이 ADR-0002의 증거를 또 한 번 확인시킨다:
> *"pid에 담아 보내는데 FlatBuffer 0은 기록을 안해서 헤더 사이즈가 달라짐 : 쓰면 안됨!!!!"*

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **`SetKeepAlive`가 Windows 전용.** `IOControlCode.KeepAliveValues`는 Linux에서 `SocketException`. 크로스 플랫폼 배포 불가 (현재 호출부는 주석 처리 상태 — `ServerM.cs:628`) | 🟠 중간 |
| 2 | `_allowedPkMan`이 `static` — 같은 프로세스에서 서버와 클라이언트를 함께 띄우면 **화이트리스트가 서로를 덮어쓴다** | 🟠 중간 |
| 3 | `gDisconnectTimer`를 생성자에서 **매번 새로 할당** (`:50`). 두 번째 인스턴스가 첫 번째의 타이머를 버린다 | 🟠 중간 |
| 4 | `GetMyIP()` 동기 블로킹 DNS 조회 | 🟡 낮음 |
| 5 | `_cts`가 `public` 필드 — 외부에서 임의 취소 가능 | 🟡 낮음 |

### 개선점

- KeepAlive는 `Socket.SetSocketOption(SocketOptionLevel.Tcp, TcpKeepAliveTime/Interval/RetryCount, ...)` — .NET 5+ 크로스 플랫폼 API 사용 (Phase 5)
- static 전역 전부 인스턴스 상태로. 화이트리스트는 `IAuthorizationPolicy` 구현으로 DI 등록 (Phase 1·9)
- `uniqueProgramNumber`는 헤더의 **프로토콜 버전 필드**로 흡수 (Phase 4)

### 판정

🟡 **개작**. `IsAllowedPacket` 개념은 Phase 9로 승계, 나머지는 재작성.

---

## `SrvGlobal`

`ServerGlobals.cs:14`, `static class`

### 동작

서버 전역 상태와 설정. `SetSrvGloalVariable(SrvTableM)`에서 데이터 테이블을 읽어 채운다.

| 설정 키 (테이블) | 필드 | 용도 |
|---|---|---|
| `clientSettings.screenResolution` | `GlobalM.screenWidth/Height/Half*` | 화면 크기 |
| `serverConfig.netWorkDelayM_IQR_WindowSize` | `netWorkDelayM_IQR_WindowSize` | 지연 측정 윈도우 |
| `serverConfig.disConnectForceWaitMs` | `disConnectForceWaitMs` | 강제 종료 대기 |
| `serverConfig.srvUpdateDeltaMs` | `srvUpdateDeltaMs` | 스크립트 Update 간격 |
| `serverConfig.srvFixedUpdateDeltaMs` | `srvFixedUpdateDeltaMs` | 스크립트 FixedUpdate 간격 |
| `serverConfig.outGoingActBlockFactor` | `cntOutGoingPkActBlock` = `max(1, ProcessorCount × factor)` | 송신 샤드 수 |
| `serverConfig.incomeActBlockFactor` | `cntIncommingPkActBlock` = `max(1, ProcessorCount × factor)` | 수신 샤드 수 |

**유저 레지스트리**: `static ConcurrentDictionary<TcpClient, InnerSrvUserM> dicSrvUsers`
`GetUser(TcpClient)` (internal), `ExistUser`, `AddUser`, `RemoveUser`, `SendPacketToAllUsers(PACKET_TYPE, byte[], long oidExceptUser = 0)`

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **하드코딩된 DB 자격증명.** `gDbConnectionString = "mongodb://smck:smck4@localhost:27017"` — 소스에 비밀번호 | `:103` | 🔴 치명 |
| 2 | **설정 검증이 `Debug.Assert`.** Release 빌드에서 전부 제거된다 → 설정 누락이 **조용히 통과**하고 필드가 0으로 남는다 | `:38`,`57`,`66`,`75`,`84`,`93` | 🔴 높음 |
| 3 | **`GetUser`가 매 호출마다 `new SrvUserM(...)` 할당.** 핫패스(`SendMemPk`마다 호출)에서 힙 할당 | `:143~149` | 🟠 중간 |
| 4 | `SendPacketToAllUsers`가 `async Task`인데 `await`이 없다 — `Parallel.ForEach`는 동기. 컴파일 경고 + 호출자가 완료를 기다릴 수 없음 | `:177~189` | 🟠 중간 |
| 5 | `dicSrvUsers` 키가 `TcpClient` 객체 참조. 강타입 ID가 아니라 리소스 객체가 키 | `:134` | 🟡 낮음 |
| 6 | `SetSrvGloalVariable`에서 `float.Parse` / `int.Parse` — 컬처 의존, 형식 오류 시 예외 | `:80`,`:89` | 🟡 낮음 |
| 7 | 서버 설정 클래스가 클라이언트 화면 크기(`GlobalM.screen*`)를 설정한다 — 관심사 혼입 | `:28~39` | 🟡 낮음 |

### 개선점

- **비밀번호는 소스에서 제거.** 환경변수/시크릿 저장소 (Phase 9)
- **`Debug.Assert` → `IValidateOptions<T>`.** 잘못된 설정은 **시작 시점에 예외로 실패**해야 한다 (Phase 2). 이게 레거시의 가장 위험한 패턴 중 하나다
- `GetUser` 할당 제거: 세션을 `readonly struct` 핸들 + 조회 API로 (Phase 1 ID 타입, Phase 13 세션)
- 샤드 수 계산(`max(1, ProcessorCount × factor)`) 방식은 **승계** — `IExecutionModel` 옵션으로 (Phase 8)
- `Update`/`FixedUpdate` 이원 간격은 Phase 17 틱 루프 설계에 반영

### 판정

🟡 **개작**. 샤드 수 산정 방식과 설정 항목 목록은 승계 가치가 있다. 구현·검증 방식은 폐기.

→ Phase 2 (옵션 검증), Phase 8 (샤드), Phase 9 (시크릿)

---

## `InnerSrvUserM` / `SrvUserM`

`UserSrvM.cs:17`, `:129`

### 동작

**Inner/Wrapper 2계층 패턴.**
- `InnerSrvUserM : InnerUserM` — 실제 상태 보유 객체. 라이브러리 내부용
- `SrvUserM : UserM` — 얇은 래퍼. `_user`가 `null`이면 프로퍼티가 기본값을 반환하고 `IsExist == false`

> **설계 의도**: 앱 코드가 이미 삭제된 유저 핸들을 만져도 `NullReferenceException`이 나지 않는다. `null` 대신 `IsExist`로 표현. 라이브러리 사용자 보호 장치로 타당한 발상이다.

`InnerSrvUserM` 고유 멤버: `MetaDataDownloadOk`, `DB_ID`(`ObjectId`), `netDelay`(`NetWorkDelayM`)

**`FlushSendBuffer()`** (`:46`)
```csharp
dataToSend = ArrayPool<byte>.Shared.Rent(_sendBufferLength);
Buffer.BlockCopy(_sendBuffer, 0, dataToSend, 0, _sendBufferLength);
_sendBufferLength = 0;
if (Tc.Connected) {
    var finalPkData = new FinalPkDataM(Tc, dataToSend, dataToSendLenth);
    SendPacketGroupM.SendPacket(Oid, finalPkData);
}
```

**`RequestDisconnectForce()`** (`:108`) — 강제 종료 타이머(`TimerM_User_Disconnect_Force`, `disConnectForceWaitMs` 후) 등록 후 `Tc.Client.Shutdown(SocketShutdown.Send)`로 FIN 전송. **정상 종료 절차를 먼저 시도하고 타임아웃 시 강제**하는 2단 종료.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`ArrayPool.Rent` 후 반납 코드가 없다.** `finally`가 **빈 블록**. `Tc.Connected == false` 경로에서는 대여한 버퍼가 그대로 유실 | `:54`, `:72~75` | 🔴 높음 |
| 2 | **파이널라이저 `~InnerSrvUserM()`** — 모든 인스턴스가 종료 큐에 올라 GC 압력 증가. 관리되지 않는 리소스를 직접 들고 있지도 않다 | `:86~89` | 🟠 중간 |
| 3 | 래퍼가 **매번 새 객체** — `SrvGlobal.GetUser`가 호출마다 `new SrvUserM`. 핫패스 할당 | 구조적 | 🟠 중간 |
| 4 | `catch (Exception e) { Debug.WriteLine(...) }` — 송신 실패를 삼킴. 상위가 알 방법 없음 | `:67~71` | 🟠 중간 |
| 5 | 프로퍼티마다 `(_user as InnerSrvUserM)` 캐스팅 반복 — 타입 안전성 없이 런타임 캐스팅 | `:133~162` | 🟡 낮음 |
| 6 | `_disposed` 플래그가 `bool` 필드, 스레드 안전하지 않음 | `:21` | 🟡 낮음 |

### 개선점

- **Inner/Wrapper의 "삭제된 핸들 안전" 의도는 승계.** 단 래퍼를 `readonly struct SessionHandle`(세대 카운터 포함)로 만들어 **할당 0**으로 (Phase 1 ID 타입 + Phase 13)
- `ArrayPool` 반납을 `try/finally` 또는 소유권 타입으로 강제 (Phase 3)
- 파이널라이저 제거. `IAsyncDisposable`만
- 송신 실패는 삼키지 않고 `IServerLogger` + 메트릭으로 노출 (Phase 11)
- **2단 종료(FIN → 타임아웃 → 강제)는 승계.** 커넥션 생명주기 상태 기계로 (Phase 5)

### 판정

🟡 **개작**. Inner/Wrapper 안전 핸들 개념과 2단 종료 절차는 승계 가치가 높다.

→ Phase 1 (ID 타입), Phase 3 (풀 소유권), Phase 5 (종료 절차), Phase 13 (세션)

---

## `SendPacketGroupM`

`SendPacketGroupM.cs:12`, `static class`

### 동작

**이 파일이 레거시에서 가장 승계 가치가 높다.** 유저별 순서 보장의 실체.

```csharp
static readonly int _iCntOutGoingBlock = SrvGlobal.cntOutGoingPkActBlock;
static ConcurSeqTaskContextExecLongRunM<FinalPkDataM>[] _arrActBlockOutGoing;

static readonly int _iCntIncomeBlock = SrvGlobal.cntIncommingPkActBlock;
static ConcurSeqTaskContextExecLongRunM<MemPacketM>[] _arrActBlockIncome;
```

정적 생성자에서 두 샤드 배열을 채운다. 송신 샤드는 `PacketM.SendPacket`을, 수신 샤드는 `MemPkDispatcher.MemPkAction`을 소비자로 받는다.

| 메서드 | 샤드 선택 | 의미 |
|---|---|---|
| `SendPacket(long oid, in FinalPkDataM)` | `oid % _iCntOutGoingBlock` | **유저별 송신 순서 보장** |
| `SendPacket(long oid, TcpClient, uint pid, PACKET_TYPE, byte[], CompressAndEncryptM)` | 같음 | `TryMakeSendPacketData` 후 Post |
| `SendPacketRnd(...)` | `random.Next(0, n-1)` | 순서 무관 패킷 |
| `SendMemPacket(long oid, MemPacketM)` | `oid % _iCntIncomeBlock` | **유저별 수신 처리 순서 보장** |
| `SendMemPacketRnd(MemPacketM)` | `random.Next(0, n-1)` | 순서 무관 |

> **핵심 아이디어**: 유저 `oid`를 샤드 수로 나눈 나머지로 큐를 고르면, **같은 유저의 메시지는 언제나 같은 큐**에 들어가 FIFO로 처리된다. 큐끼리는 병렬이므로 전체 처리량은 샤드 수만큼 확장된다. 락 없이 순서 보장 + 병렬성을 동시에 얻는다.
>
> 주석에 남은 `ActionBlock<T>` 버전(`:17`,`:22`,`:32`,`:41`)이 자체 구현 `ConcurSeqTaskContextExecLongRunM`으로 **교체된 흔적**이다. TPL Dataflow에서 갈아탔다는 뜻이므로, 새 구현에서도 **채널 기반 자체 구현 vs Dataflow를 벤치마크로 비교**해야 한다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **static 초기화 순서 함정.** `_iCntOutGoingBlock`은 `SrvGlobal.cntOutGoingPkActBlock`을 static 필드 초기자로 복사한다. `SrvGlobal.SetSrvGloalVariable()`(테이블 로딩 후)보다 먼저 `SendPacketGroupM`이 터치되면 **0이 들어가고 `oid % 0` → `DivideByZeroException`**. 타이밍 의존 버그 | `:15`, `:20` | 🔴 높음 |
| 2 | **`SendPacketRnd`가 `async void`** — 예외가 관측 불가능한 곳으로 새어나간다. 게다가 본문에 `await`이 없다 | `:76` | 🔴 높음 |
| 3 | **`random.Next(0, n - 1)`** — 상한이 배타적이므로 **마지막 샤드가 절대 선택되지 않는다**. off-by-one | `:78`, `:105` | 🟠 중간 |
| 4 | **`static Random random`은 스레드 안전하지 않다.** 동시 호출 시 내부 상태 손상 → 0 반복 반환 가능. .NET 6+는 `Random.Shared` | `:14` | 🟠 중간 |
| 5 | `oid % n`에서 `oid`가 음수면 결과도 음수 → `IndexOutOfRangeException`. `oid` 생성 경로 검증 필요 | `:48`,`:60`,`:94` | 🟠 중간 |
| 6 | 큐 포화 시 동작 미정의 — `Post` 실패 처리 없음. 백프레셔 부재 | 전역 | 🟠 중간 |
| 7 | `internal` 접근자 없이 `static class`가 `internal`(기본) — 의도인지 불명확 | `:12` | 🟡 낮음 |

### 개선점 (ChServerM)

- **샤딩 아이디어를 `IExecutionModel`의 기본 전략으로 승계** (Phase 1 계약 / Phase 8 구현). 다만:
  - 샤드 수는 **옵션 + 시작 시점 검증**으로. static 초기자 의존 제거 (Phase 2)
  - 파티션 키를 `oid`가 아니라 강타입 `SessionId`의 안정 해시로 (`XxHash3`). 음수·오버플로 제거 (Phase 1)
  - `async void` 전면 금지. `ValueTask` + 관측 가능한 오류 경로
  - 무작위 샤드는 `Random.Shared.Next(0, n)` — 상한 수정
  - **큐 포화 시 백프레셔 또는 admission control** (Phase 10)
- 송신/수신 샤드 배열을 **분리 유지** — 한쪽 포화가 다른 쪽을 막지 않는다. 이 분리는 승계
- `ConcurSeqTaskContextExecLongRunM` vs `System.Threading.Channels` vs Dataflow **3자 벤치마크** (Phase 8)

### 판정

🟢 **승계** (아이디어). 구현은 재작성하지만 **설계는 그대로 가져간다.** 레거시에서 가장 값어치 있는 파일.

→ Phase 1 (`IExecutionModel` 계약), Phase 8 (구현·벤치마크)

---

## `NetWorkDelayM`

`NetWorkDelayM.cs:11`

### 동작

RTT 기반 단방향 지연 추정. 서버 틱을 보정해서 클라에 전달한다.

- `SendServerTick()` — 현재 `Stopwatch.GetTimestamp()`에서 **평균 지연을 뺀 값**을 반환. 평균은 `delays` 큐를 정렬 후 `InterQuartileM<long>.RemoveOutliersAndAverage`로 **IQR 이상치 제거** 후 계산. 단조 증가 보장(`curSendTick > lastSendServerTick`일 때만 갱신)
- `RecvServerTick()` — `(now - lastSendServerTick) / 2`를 지연으로 기록. 큐가 `windowSize` 초과 시 가장 오래된 값 제거

> **IQR로 이상치를 제거하는 발상은 좋다.** 네트워크 지연은 스파이크가 흔해서 단순 평균은 쓸 수 없다. 이 아이디어는 승계 가치가 있다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`sortedArray`가 인스턴스 필드인데 락 없이 쓴다.** `SendServerTick()`이 동시 호출되면 정렬 중 배열이 덮어써져 **쓰레기 값** | `:14`, `:46~47` | 🔴 높음 |
| 2 | **`_locker`를 선언하고 한 번도 쓰지 않는다.** 동기화 필요성을 인지했으나 구현하지 않은 흔적 | `:16` | 🔴 높음 |
| 3 | `delays.Count`를 읽고 `CopyTo`하는 사이에 큐가 변할 수 있다 — `ConcurrentQueue.CopyTo`는 원자적 스냅샷이 아니다. `Array.Sort(sortedArray, 0, delaysCount)` 범위 불일치 | `:40~47` | 🟠 중간 |
| 4 | `lastSendServerTick`이 `public long`, 비동기화 접근. 메모리 가시성 보장 없음 | `:13` | 🟠 중간 |
| 5 | `_leftProcessCnt`를 증가만 하고 읽는 곳이 없다 (죽은 코드) | `:22`, `:73` | 🟡 낮음 |
| 6 | RTT/2 = 단방향 지연 가정. 비대칭 경로에서 부정확 (알고리즘 한계, 문서화 필요) | 설계 | 🟡 낮음 |

### 개선점

- **IQR 이상치 제거 알고리즘은 승계.** 구현은 락 없는 방식으로 재작성 — 링 버퍼 + 스레드별 로컬 정렬 버퍼, 또는 세션당 단일 스레드 접근 보장(`IExecutionModel`의 유저별 직렬 실행을 활용하면 동기화가 불필요해진다)
- 시간 소스를 `IClock` 추상화로 (Phase 1) — 테스트 가능성
- RTT 추정 정확도를 높이려면 NTP식 4-타임스탬프 방식 검토 (Phase 17)

### 판정

🟡 **개작**. IQR 아이디어 승계, 동시성 구현 전면 재작성.

→ Phase 1 (`IClock`), Phase 17 (틱·시간 동기화)

---

## `TimerSrvM` (타이머 액션들)

`TimerSrvM.cs`

### 동작

`ITimerActionM` 구현 3종.

**`TimerM_SrvUser_Delay_Disconnect`** (`:14`) — 로그인 처리 완료 전에 클라가 FIN을 보낸 경우의 후처리.
`SrvGlobal.GetUser(_tc).IsExist`가 `true`가 될 때까지 **지수 백오프 재시도**(`_dueTimeSec *= 2`, 최대 10회). 등록이 확인되면 정상 정리 절차 수행.

**`TimerM_HeartBitSend`** (`:74`) — `PC_HEART_BIT` 전송
**`TimerM_HeartBitCheck`** (`:92`) — `RequestDisconnectForce()`

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **주석·코드 불일치.** 주석은 "4번 다시 시도"인데 코드는 `_exeTimes <= 10` | `:51` | 🟡 낮음 |
| 2 | `async Task DoAction()`에 `await`이 없다 (3개 모두) — 컴파일 경고, 동기 실행 | `:30`,`:82`,`:100` | 🟠 중간 |
| 3 | `srvUser.DisconnectProcess()`를 await하지 않음 — fire-and-forget | `:43` | 🟠 중간 |
| 4 | 로그 메시지 품질 — `"대박사건(SrvUser:...)"`. 실패 상황인데 원인·조치가 없다 | `:59` | 🟠 중간 |
| 5 | 지수 백오프 상한이 없다. 10회면 `1+2+4+...+512` ≈ 17분간 타이머 유지 | `:53` | 🟡 낮음 |

### 개선점

- **"등록 완료를 기다리는 지수 백오프 재시도" 패턴은 승계.** 근본 원인은 *로그인 처리와 커넥션 정리가 경쟁*하는 것이므로, 새 구현에서는 **커넥션 상태 기계**로 해결한다 — `Connecting → Authenticating → Active → Draining → Closed` 상태를 두고 각 상태에서의 정리 책임을 명시하면 재시도가 불필요해진다 (Phase 5)
- 단, 상태 기계로도 못 막는 경합이 남으면 이 백오프를 안전망으로 유지
- 하트비트는 TCP keepalive + 애플리케이션 하트비트 이중화 여부를 ADR로 (Phase 5)

### 판정

🟢 **승계** (문제 인식과 대응 패턴). 구현은 상태 기계로 대체 시도.

→ Phase 5 (커넥션 생명주기)

---

## `IniSrvOptionM`

`IniOptionSrvM.cs:9`, `IniSrvOptionM : IniOptionM`

### 동작

`OptionServerM.ini` 파일명만 지정. `SaveOptionSetting()`/`LoadOptionSetting()` 오버라이드는 **둘 다 빈 구현**(실제 내용은 전부 주석 처리).

### 판정

🔴 **폐기**. INI 설정 방식 자체를 `IConfiguration` + Options 패턴으로 대체한다 (Phase 2). 이 파일에 승계할 내용이 없다.

---

## `CommonInterfaceM`

`PublicLib/CommonInterfaceM.cs`

### 동작

인터페이스 4종. `IExecutableM { void Execute(); }`, `ICancelM { void Cancel(); }`, `IExecutableValueAsyncM { ValueTask Execute(); }`, `IExecutableAsyncM { Task Execute(); }`

동기/`Task`/`ValueTask` 3종을 **별도 인터페이스로 분리**한 점이 눈에 띈다. 실행기(`ConcurSeqTaskExecM` 등)가 오버헤드에 맞는 것을 고르게 한 설계.

### 판정

🔵 **참고**. ChServerM은 `ValueTask` 단일 규약으로 간다 (CLAUDE.md 코드 컨벤션). 다만 "동기 경로에 `Task` 할당을 강요하지 않는다"는 문제의식은 Phase 1 에러 모델·생명주기 계약 설계 시 참고한다.

---

## `SrvClaFuncM`

`PublicLib/SrvClaFuncM/SrvClaFuncM.cs`

### 동작

`SimulPos(float angle, long elapsedTick, float speed, ref float x, ref float y)` 하나뿐.
각도·속도·경과 틱으로 위치를 적분한다. 분모가 `Stopwatch.Frequency`.

> **의도**: 서버와 클라이언트가 **동일한 함수**로 위치를 계산해 예측 불일치를 없애려는 시도. 클라 예측 + 서버 검증 구조의 기초.

### 문제점

- `double`로 계산해 `float`에 저장 — 플랫폼·컴파일러에 따라 결과가 달라질 수 있다. **결정론적 시뮬레이션에는 부적합**
- `Stopwatch.Frequency`가 머신마다 다르다. 서버가 `PC_LOGIN_OK`로 자기 `Frequency`를 클라에 보내지만(`ServerM.cs:922`), 클라가 자기 시계로 적분하면 미세 오차가 누적
- 함수 하나만 있는 "서버/클라 공용" 폴더 — 구조만 잡히고 채워지지 않았다

### 개선점

- 결정론이 필요하면 **고정소수점 또는 정수 연산**으로. `float`/`double` 혼용은 재현성을 깬다
- 시간 단위를 `Stopwatch.Frequency`가 아닌 **고정 틱(예: 밀리초 정수)**으로 정규화 (Phase 17)

### 판정

🔵 **참고**. "서버·클라 공용 시뮬레이션 함수" 개념은 Phase 17에서 다시 다룬다. 코드는 옮기지 않는다.

---

## 이 계층의 종합 판정

| 항목 | 판정 | 이유 |
|---|---|---|
| **oid 기반 샤딩(순서 보장)** | 🟢 승계 | 락 없이 순서+병렬성. 레거시 최고 자산 |
| **2단 종료(FIN→타임아웃→강제)** | 🟢 승계 | 실전 검증된 절차 |
| **지연 disconnect 재시도** | 🟢 승계(안전망) | 근본 해결은 상태 기계로 |
| **상태별 패킷 화이트리스트** | 🟢 승계 | Phase 9 보안 미들웨어 |
| **Inner/Wrapper 안전 핸들** | 🟢 승계(개념) | struct 핸들로 재구현 |
| **IQR 지연 이상치 제거** | 🟢 승계(알고리즘) | 구현은 재작성 |
| **샤드 수 = 코어 × 팩터** | 🟢 승계 | 옵션화 |
| Pipelines 5단 프레이밍 | 🟡 개작 | 구조 승계, 할당·상한·오류처리 재작성 |
| Template Method 상속 강제 | 🟡 개작 | Builder 조립으로 |
| RSA/XOR 핸드셰이크 | 🔴 폐기 | 승계할 것 없음. 전면 재설계 |
| INI 설정 | 🔴 폐기 | Options 패턴 |
| static 전역 상태 | 🔴 폐기 | DI 스코프 |

### 즉시 조치가 필요한 발견 (새 코드에 절대 옮기면 안 되는 것)

1. `ServerGlobals.cs:103` — **소스에 DB 비밀번호**
2. `ServerM.cs:881` — **비밀번호 틀려도 로그인 통과** (주석 처리된 `return`)
3. `ServerM.cs:798` — **서버→클라 XOR "암호화"**
4. `SendPacketGroupM.cs:15,20` — **static 초기화 순서에 따른 `DivideByZeroException` 가능**
5. `UserSrvM.cs:54` — **`ArrayPool` 대여 후 반납 코드 없음**
6. `IoPipelineSrvM.cs:320` — **최대 프레임 크기 상한 없음** (메모리 고갈 공격)
7. `ServerGlobals.cs` 전역 — **`Debug.Assert`로 설정 검증** (Release에서 무력화)
