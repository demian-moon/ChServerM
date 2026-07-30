# 레거시 자산 인벤토리

`LegacyServer/`(132 files, C# 119)는 이 프로젝트의 전신이다. **커밋하지 않고 로컬 참조 전용**으로 둔다(`.gitignore`에 `/LegacyServer/`). 코드를 그대로 옮기지 않고, 비교 대상으로 읽어 새로 작성하거나 개선해 승계한다.

## 판정 상태 표기

| 표기 | 의미 |
|---|---|
| **승계** | 설계 의도가 옳다. 개선해서 새 코드로 옮긴다 |
| **개작** | 아이디어는 쓰되 구현은 다시 쓴다 (하드 룰 위반 있음) |
| **폐기** | 새 아키텍처와 충돌하거나 불필요 |
| **미판정** | 아직 읽지 않았다. 파일명·경로 기준 후보일 뿐 |

---

## 1. 확정된 사실 — 목표 워크로드

레거시 구성이 워크로드를 사실상 특정한다. **실시간 게임 서버 + 매치메이킹**:

- `RatingSystem/GlickoM.cs`, `WengLinM.cs` — Glicko / Weng-Lin 레이팅 (매치메이킹)
- `QuadTreeM.cs`, `BoxColliderM.cs`, `MathM.cs`, `HierachyM.cs` — 공간 분할 · 충돌 · 씬 계층
- `FbsClassM/FbsServerTick.cs`, `FbsLoginOk.serverFrequency` — 서버 틱 주기 동기화
- `NetWorkDelayM.cs` — 네트워크 지연 보정
- `DBManager/MongoDBManagerM.cs` — MongoDB 영속화

→ ROADMAP 우선순위에 반영: TCP 상시 연결(Phase 4)이 HTTP 무상태(Phase 8)보다 우선. 사용자 확인 필요.

---

## 2. 전송 / 프레이밍 — 정독 완료

### `IoPipelineSrvM.cs` (14.3K) — **개작**

정석적인 `System.IO.Pipelines` 구조다. 설계 의도는 승계하되 구현은 다시 쓴다.

**승계할 것**
- `FillPipe` / `ReadPipe` 분리 + `Task.WhenAll` — Pipelines 표준 패턴
- 상태 머신 프레이밍: `eToReadState { PK_HEAD, CONTENT_HEAD, CONTENT_DATA, ENC_PK_HEAD, ENC_PK_DATA }`
- 3단 헤더 구조 — 패킷 헤더 → 콘텐츠 헤더 → 데이터. 헤더 길이를 앞 단계에서 얻는 방식
- 평문 → 암호 모드 전환을 제어 패킷(`PSC_COMP_ENC_CHANGE`)으로 처리
- `AbDisconnectProcess` 추상화로 서버/클라 종료 로직 분기 (Template Method)
- 로그인 완료 전 종료 레이스를 1초 지연 타이머로 처리 — 실전에서 나온 대응. 새 구현에도 동등한 장치 필요
- `ConfigureAwait(false)` 일관 적용

**하드 룰 위반 — 반드시 고칠 것**

| # | 문제 | 위치 | 우리 룰 |
|---|---|---|---|
| 1 | `viewBuffer.ToArray()` — 매 패킷 힙 할당 | 300, 317, 323, 343행 | 메시지당 할당 0. `ReadOnlySequence`→`Span` 직접 파싱으로 대체 |
| 2 | `ArrayPool` 반납 누수 — `Return`이 `try/finally` 밖. `pooledEncHeadBuf`는 이터레이션을 넘겨 대여(ENC_PK_HEAD→ENC_PK_DATA)하므로 중간 `break`/예외 시 미반납 | 271~290행 | 풀 누수 감지 진단 (Phase 3) |
| 3 | 체크섬 실패를 `throw new Exception()`으로 처리 | 310행 | 핫패스에 예외 금지. `TryXxx` 패턴 |
| 4 | 파싱 예외를 잡고 루프 계속 — 상태 머신이 어긋난 채 스트림 계속 파싱 (프레이밍 desync) | 379~390행 | 프레임 오류는 커넥션 종료가 정답 |
| 5 | `Debug.WriteLine` 로깅 — Release 빌드에서 전부 소멸 | 전역 | `IServerLogger` |
| 6 | 백프레셔 미설정. `PipeOptions(...,-1,-1,-1,false)`, `minBufferSize = 512` | 99, 114행 | pause/resume 임계값 명시 (Phase 4) |

### `FlatbufferM/PacketM.fbs` (3.2K) — **핵심 발견: 헤더에 FlatBuffers를 쓴 것이 설계 오류**

`FbsPkHeadM`, `FbsContentHeadM`, `FbsEncryptHeadM` 모두 FlatBuffers `table`이다. 그런데 스키마 주석이 문제를 자백하고 있다:

```
byteCheckSum : byte = -1;   // 0을 쓰면 안된다. 헤더 길이 달라짐
packetType : ushort;        // 디폴트 값은 저장이 안되니까(패킷 사이즈가 달라짐)해서 1부터 쓴다
conDataLen : int = -1;      // 0은 저장이 안되니까 -1로 설정 (헤더값 달라짐)
gage : ushort = 65535;      // 65535는 변경이 없다는 의미
```

**원인**: FlatBuffers는 기본값과 같은 필드를 직렬화하지 않는다. 그래서 헤더가 **가변 길이**가 되고, 고정 길이를 전제로 하는 프레이밍이 깨진다. 이를 `-1`, `65535` 같은 sentinel 값으로 우회하고 있다.

**결론**: FlatBuffers 자체의 문제가 아니라 **용도 오배치**다. `docs/ARCHITECTURE.md`의 "프레이밍과 직렬화를 분리한다" 원칙이 정확히 이 문제를 해결한다.

- **헤더(프레이밍)** → 고정 크기 `struct` + `MemoryMarshal`. 파싱 비용 0, 크기 컴파일 타임 확정
- **페이로드(직렬화)** → FlatBuffers 유지 가능. 역직렬화 없는 랜덤 접근이 게임 패킷에 유리

→ **ADR-0002 근거 확보.** "FlatBuffers 탈락"이 아니라 "FlatBuffers를 페이로드 전용으로 격리"가 결론. 사용자 확인 필요.

### `ADR-0001` 관련 — Kestrel 재사용 쪽으로 기울었다

레거시는 `TcpClient.GetStream()` → `NetworkStream.ReadAsync` 노선이다. Kestrel Socket Transport는 `SocketAsyncEventArgs` 풀링 + 전용 IO 스케줄러를 쓰므로 `NetworkStream` 계층이 없다. 즉 레거시 노선은 **성능 상한이 더 낮다**. 다만 마이그레이션 비용이 있으니 Phase 4에서 양쪽 프로토타입 벤치마크로 확정한다.

### 기타 프레이밍 자산 — **미판정**

`PublicLib/PacketM.cs`(26K), `MemPacketM.cs`(9.3K), `SendPacketM.cs`, `PkObjM.cs`, `AllowedPacketM.cs`, `SendPacketGroupM.cs`, `NetWorkM.cs`(3.8K)

`AllowedPacketM`은 패킷 화이트리스트로 보이며 보안 미들웨어 후보다. `SendPacketGroupM`은 송신 배칭 후보 — 둘 다 성능·보안에 직결되므로 우선 정독 대상.

---

## 3. 디스패치 / 실행 모델 — UML 기준 판정

`ServerLib구조uml.txt`에서 파악한 기존 구조:

```
IoPipelineM → AbMakeMemPkFromPipe (StoreMemPk)
                ├─ MakeMemPkForServer
                └─ MakeMemPkForClient
                      ↓
              MemPkDispatcher  Dictionary<E_PACKET_TYPE, AbMemPkAction>
                      ↓
              AbMemPkAction ─ DoPkLogIn, SomePacketActions...

NetworkM.gMemPkActionBlock  ← 글로벌 패킷 처리
UserM.MemPkActionBlock      ← 유저별 패킷 처리 (UserM : IObservable)
ServerM (abstract) ─ ServerStart() / AppStart()  ← 상속해서 비즈니스 서버 구현
```

| 자산 | 판정 | 사유 |
|---|---|---|
| `ServerM` abstract + `AppStart()` 상속 모델 | **개작** | Template Method는 옳지만, 우리는 상속 대신 `ServerBuilder` 조립(Phase 2)으로 간다. 상속 강제는 확장성을 제약 |
| `MemPkDispatcher` `Dictionary<E_PACKET_TYPE, ...>` | **개작** | 딕셔너리 조회 → 소스 생성 스위치 테이블(Phase 6). 중복/누락 패킷 ID를 컴파일 타임에 검출 |
| `AbMemPkAction` 패킷별 핸들러 (Command) | **승계** | `IMessageHandler<T>`로 직결 |
| **`UserM.MemPkActionBlock` — 유저별 ActionBlock** | **승계 (중요)** | TPL Dataflow. **한 유저의 패킷을 순서대로 처리하는 보장**. 게임 서버 필수 요건이며 `IExecutionModel`(Phase 7) 설계에 반드시 반영해야 한다 |
| `NetworkM.gMemPkActionBlock` 글로벌 처리 | **승계** | 글로벌/유저별 처리 분리는 옳은 축 |
| `UserM : IObservable` | **미판정** | Observer. 우리 이벤트 버스와 겹치는지 확인 필요 |
| `IniOptionM` / `IniSrvOptionM` / `IniClntOptionM` (INI 설정) | **폐기** | Options 패턴 + `IValidateOptions<T>`로 대체 |

---

## 4. 나머지 축 — 미판정 (파일명 기준 후보)

아직 읽지 않았다. 승계 여부는 정독 후 판정한다.

| ROADMAP | 후보 파일 |
|---|---|
| Phase 3 버퍼 | `BasicLibM/Pool/MemoryPoolM.cs`, `ObjectPoolM.cs`, `StackMemAllocM.cs`, `Memory/UnsafeCopyBlock.cs` |
| Phase 7 동시성 | `BasicLibM/Concurrent/ConcurrentQueueExecutorM.cs`, `ExecutableTaskDispatcherM.cs`, `Scheduler/`(3종), `MultiThreadM.cs`, `PublicLib/ConcurSeqTaskExecM.cs`, `Signal/AsyncManualResetEventM.cs` |
| 압축/보안 | `PublicLib/CompressAndEncryptM.cs` — 스키마 주석 기준: 1024B 미만 무압축, LZ4 압축, 서버→클라 XOR / 클라→서버 AES256 |
| Phase 9 관측 | `PublicLib/Logger/LogM.cs`, `BasicLibM/Log4Net/TcpLogRecieverM.cs`, `PublicUtil/StatisticsM.cs` |
| Phase 10 상태 | `DBManager/DBManagerM.cs`, `MongoDBManagerM.cs`, `Table/SrvTableM.cs`, `AbSrvTableM.cs` |
| 게임 도메인 | `QuadTreeM.cs`, `BoxColliderM.cs`, `RatingSystem/`(2종), `MathM.cs` — 프레임워크가 아닌 애플리케이션 계층. `Samples/`로 갈 후보 |
| 유틸 | `BasicLibM/HangulM/`, `ExcelLibM/`, `JiraLibM/`, `CsvParser.cs`, `BigIntM.cs` — 프레임워크 범위 밖 |

## 5. 폐기 확정

| 자산 | 사유 |
|---|---|
| `RoslynCompilerM.cs`, `Script/ScriptM.cs`, `ScriptUtilM.cs` | 런타임 동적 컴파일. 하드 룰 "리플렉션·동적 컴파일 금지" 정면 위반. Native AOT 불가 |
| `Unused/` 전체 (`EcsSystemM.cs`, `CmdMachineM.cs`, `CryptM.cs` 등 8개) | 레거시에서 이미 폐기된 코드 |
| `FlatbufferM/flatc.exe` | 빌드 도구 바이너리. 저장소에 넣지 않는다. 필요하면 패키지 참조로 |
| `PublicUtil/ScreenLibM/`, `BasicLibM/UI/ProgressBarM.cs` | 콘솔 UI. 서버 프레임워크 범위 밖 |
| `BasicLibM/etc/unity관련` | 클라이언트 전용 |

## 6. 참고 — 레거시 빌드 환경

- `LegacyServer/ServerM.csproj` — `<TargetFrameworks>.net9</TargetFrameworks>`. **표기 오류**(`net9.0`이어야 함)
- `LegacyServer/BasicLibM/JiraLibM/JiraLibM.csproj` — `v4.8` (.NET Framework)
- 우리는 `net10.0` 단일 타깃. 레거시 솔루션(`ServerM.sln`)은 `ChServerM.sln` 빌드 대상에서 제외한다
