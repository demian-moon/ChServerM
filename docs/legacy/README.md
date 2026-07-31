# 레거시 코드 정밀 분석 — 인덱스

`LegacyServer/`(111개 `.cs` / 약 26,000줄, `Unused/` 제외)의 **클래스 단위 정밀 분석**.

## 이 문서의 목적

**코드를 다시 읽지 않고 이 문서만으로 작업할 수 있도록** 한다. 각 클래스마다 다음을 기술한다.

1. **동작** — 무엇을 어떻게 하는가. 시그니처와 핵심 로직 포함
2. **문제점** — 버그, 하드 룰 위반, 보안 결함. 행 번호 포함
3. **개선점** — ChServerM에서 어떻게 다시 쓸 것인가
4. **판정** — 승계 / 개작 / 폐기 / 참고, 그리고 대응 ROADMAP Phase

## 판정 표기

| 표기 | 의미 |
|---|---|
| 🟢 **승계** | 설계 의도가 옳다. 개선해서 새 코드로 옮긴다 |
| 🟡 **개작** | 아이디어는 쓰되 구현은 다시 쓴다 (하드 룰 위반 있음) |
| 🔵 **참고** | 옮기지 않지만 설계 판단의 근거로 읽을 값이 있다 |
| 🔴 **폐기** | 새 아키텍처와 충돌하거나 불필요 |
| ⚪ **빈 파일** | 코드가 없다 (주석만 / 전체 주석 처리) |

## 문서 목록

| 문서 | 범위 | 상태 |
|---|---|---|
| [00-overview.md](00-overview.md) | 전체 구조, 데이터 흐름, 의존 관계, 발견 요약 | 작성 중 |
| [01-network-transport.md](01-network-transport.md) | 서버 부트스트랩, 소켓 수락, Pipelines, 전송 샤딩 | ✅ |
| [02-packet-framing.md](02-packet-framing.md) | 패킷 구조, 프레이밍, FlatBuffers 래퍼, 화이트리스트 | ✅ |
| [03-ecs-object-model.md](03-ecs-object-model.md) | ECS 컴포넌트, 공간·충돌, 모튼 코드 | ✅ |
| [04-concurrency.md](04-concurrency.md) | 실행기, 스케줄러(4종), 타이밍 휠, 시그널, SparseSet | ✅ |
| [05-client.md](05-client.md) | **레거시 클라이언트** — 소스 공유 모델, 핸드셰이크 전체, 시각 동기화 | ✅ |
| [06-session-user.md](06-session-user.md) | 유저 모델, Inner/Wrapper, 옵저버, 전역 OID | ✅ |
| [06-data-table.md](06-data-table.md) | 메타데이터, INI, 파일, CSV/Excel | 예정 |
| [07-security.md](07-security.md) | 압축·암호화, 인증, 만료 KV | ✅ |
| [08-persistence.md](08-persistence.md) | MongoDB 파사드, 재시도, 스키마 | ✅ |
| [09-observability.md](09-observability.md) | 로깅(log4net), 로그 수집기, IQR 통계 | ✅ |
| [10-time.md](10-time.md) | 틱, 타이머, 시간 유틸 | 예정 |
| [11-domain-util.md](11-domain-util.md) | 계층 구조, 충돌, 수학, 레이팅, 유틸 | 예정 |
| [12-discarded.md](12-discarded.md) | 폐기 확정 + 빈 파일 | 예정 |

---

## 전체 파일 인덱스

**정독 완료** 항목만 판정이 확정이다. `⏳`는 아직 읽지 않았으므로 파일명 기준 추정일 뿐이며 신뢰하지 않는다.

### 루트 — 서버 부트스트랩 / 도메인

| 줄수 | 파일 | 판정 | 문서 |
|---:|---|---|---|
| 983 | `ServerM.cs` | 🟡 개작 | [01](01-network-transport.md#serverm) |
| 416 | `IoPipelineSrvM.cs` | 🟡 개작 | [01](01-network-transport.md#iopipelinesrvm) |
| 195 | `ServerGlobals.cs` | 🟡 개작 | [01](01-network-transport.md#srvglobal) |
| 165 | `UserSrvM.cs` | 🟡 개작 | [01](01-network-transport.md#innersrvuserm--srvuserm) |
| 130 | `TimerSrvM.cs` | 🟢 승계 | [01](01-network-transport.md#timersrvm-타이머-액션들) |
| 110 | `SendPacketGroupM.cs` | 🟢 승계 | [01](01-network-transport.md#sendpacketgroupm) |
| 87 | `NetWorkDelayM.cs` | 🟡 개작 | [01](01-network-transport.md#networkdelaym) |
| 40 | `IniOptionSrvM.cs` | 🔴 폐기 | [01](01-network-transport.md#inisrvoptionm) |
| 1631 | `HierachyM.cs` | ⏳ | 11 |
| 567 | `BoxColliderM.cs` | ⏳ | 11 |
| 198 | `RoslynCompilerM.cs` | 🔴 폐기 | 12 |
| 173 | `MathM.cs` | ⏳ | 11 |
| 8 | `MultiThreadM.cs` | ⚪ 빈 파일 | [12](12-discarded.md) |
| 7 | `AbSrvTableM.cs` | ⚪ 빈 파일 | [12](12-discarded.md) |
| 1 | `AssemblyInfo.cs` | 🔴 폐기 | 12 |

### `PublicLib/` — 서버·클라이언트 공용

| 줄수 | 파일 | 판정 | 문서 |
|---:|---|---|---|
| 823 | `PacketM.cs` | 🟡 개작 | [02](02-packet-framing.md#packetm-struct) |
| 470 | `UserM.cs` | 🟡 개작 | [06](06-session-user.md#inneruserm) |
| 426 | `AllowedPacketM.cs` | 🟢 승계 | [02](02-packet-framing.md) |
| 333 | `PkObjM.cs` | 🟡 개작 | [02](02-packet-framing.md#pkobjm) |
| 295 | `MemPacketM.cs` | 🟡 개작 | [02](02-packet-framing.md) |
| 273 | `CompressAndEncryptM.cs` | 🔴 폐기 | [07](07-security.md#compressandencryptm) |
| 172 | `IniOptionM.cs` | ⏳ | 06 |
| 111 | `MembersM.cs` | 🔵 참고 | [06](06-session-user.md#membersmcs--대부분이-주석이다) |
| 100 | `NetWorkM.cs` | 🟡 개작 | [01](01-network-transport.md#abnetworkbase) |
| 72 | `GlobalM.cs` | 🟡 개작 | [06](06-session-user.md#globalm--compressandencryptmanm) |
| 66 | `SendPacketM.cs` | 🔴 폐기 | [02](02-packet-framing.md#sendpacketm) |
| 25 | `CommonInterfaceM.cs` | 🔵 참고 | [01](01-network-transport.md#commoninterfacem) |
| 28 | `SrvClaFuncM/SrvClaFuncM.cs` | 🔵 참고 | [01](01-network-transport.md#srvclafuncm) |
| 5 | `AbTableBaseM.cs` | ⚪ 빈 파일 | [12](12-discarded.md) |
| 870 | `ConcurSeqTaskExecM.cs` | 🟡 개작 | [04](04-concurrency.md) |
| 1073 | `FileM/InIFileM.cs` | ⏳ | 06 |
| 740 | `FileM/MetaDataM.cs` | ⏳ | 06 |
| 349 | `FileM/FileM.cs` | ⏳ | 06 |
| 193 | `FileM/StringAnalyzerM.cs` | ⏳ | 06 |
| 170 | `FileM/LoadableDataInStructM.cs` | ⏳ | 06 |
| 84 | `FileM/FileWatcherSystemM.cs` | ⏳ | 06 |
| 165 | `Logger/LogM.cs` | 🔴 폐기 | [09](09-observability.md#ablogmt--log4netm) |

### `BasicLibM/` — 범용 라이브러리

| 줄수 | 파일 | 판정 | 문서 |
|---:|---|---|---|
| 2166 | `ExcelLibM/ExcelLibM.cs` | ⏳ | 06 |
| 927 | `ExcelLibM/ExcelODBCM.cs` | ⏳ | 06 |
| 876 | `Scheduler/TimeEventSchedulerM.cs` | 🟢 승계 | [04](04-concurrency.md#timeeventschedulerm--5단-계층적-타이밍-휠) |
| 696 | `Concurrent/DataStructure/SparseSetM.cs` | 🟡 개작 | [04](04-concurrency.md#sparsesetmt-계열-4종) |
| 610 | `DateTimeStartEndM.cs` | ⏳ | 10 |
| 307 | `BigIntM.cs` | ⏳ | 11 |
| 245 | `Scheduler/ExpireEventConCurSchedulerM.cs` | 🔴 폐기 | [04](04-concurrency.md) |
| 248 | `Log4Net/TcpLogRecieverM.cs` | 🔵 참고 | [09](09-observability.md) |
| 226 | `HangulM/HangulM.cs` | ⏳ | 11 |
| 186 | `JobSystemM.cs` | 🔵 참고 | [04](04-concurrency.md) |
| 183 | `TimeM.cs` | ⏳ | 10 |
| 182 | `CsvParser.cs` | ⏳ | 06 |
| 148 | `Signal/AsyncManualResetEventM.cs` | 🟢 승계 | [04](04-concurrency.md#asyncmanualreseteventm--무할당-비동기-시그널) |
| 134 | `Pool/MemoryPoolM.cs` | ⏳ | 03 |
| 130 | `HashM.cs` | 🟡 개작 | [07](07-security.md#hashm--expirehasheventm--만료-지원-kv-저장소) |
| 124 | `Concurrent/ConcurrentQueueExecutorM.cs` | 🟢 승계 | [04](04-concurrency.md) |
| 94 | `Concurrent/ExecutableTaskDispatcherM.cs` | 🟢 승계 | [04](04-concurrency.md#executabletaskdispatcherm--락-없는-단일-소유자-디스패처) |
| 92 | `Scheduler/ConcurrentSchedulerM.cs` | 🔴 폐기 | [04](04-concurrency.md) |
| 79 | `RegM.cs` | ⏳ | 11 |
| 76 | `SerializeM.cs` | ⚪ 빈 파일(참고) | [02](02-packet-framing.md#serializem) |
| 72 | `StringBuilderM.cs` | ⏳ | 11 |
| 49 | `StackMemAllocM.cs` | ⏳ | 03 |
| 36 | `AuthM/AuthM.cs` | 🟢 승계 | [07](07-security.md#authm--유일하게-올바른-보안-컴포넌트) |
| 32 | `Pool/ObjectPoolM.cs` | 🟡 개작 | [03](03-buffer-memory.md#objectpoolmt) |
| 29 | `Memory/UnsafeCopyBlock.cs` | ⚪ 빈 파일 | [12](12-discarded.md) |
| 8 | `QuadTreeM.cs` | ⚪ 빈 파일 | [12](12-discarded.md) |
| 424 | `UI/ProgressBarM.cs` | 🔴 폐기 | 12 |
| ~700 | `JiraLibM/` (14 파일) | 🔴 폐기 | 12 |
| ~900 | `etc/unity관련/` (7 파일) | 🔴 폐기 | 12 |

### 그 외

| 줄수 | 파일 | 판정 | 문서 |
|---:|---|---|---|
| 714 | `DBManager/MongoDBManagerM.cs` | 🟡 개작 | [08](08-persistence.md#mongodbmanagerm) |
| 38 | `DBManager/DBManagerM.cs` | 🔴 폐기 | [08](08-persistence.md#dbmanagerm-싱글턴) |
| 24 | `DBManager/SrvUserAuthM.cs` | 🟡 개작 | [08](08-persistence.md#srvuserauthm) |
| 626 | `RatingSystem/WengLinM.cs` | ⏳ | 11 |
| 301 | `RatingSystem/GlickoM.cs` | ⏳ | 11 |
| 299 | `PublicUtil/TickTimeM.cs` | ⏳ | 10 |
| 163 | `PublicUtil/TimerM.cs` | ⏳ | 10 |
| 151 | `PublicUtil/StatisticsM.cs` | 🟢 승계 | [09](09-observability.md#interquartilemt--iqr-이상치-제거) |
| 205 | `PublicUtil/ScreenLibM/ScreenLibM.cs` | 🔴 폐기 | 12 |
| 457 | `Script/ScriptM.cs` | 🔴 폐기 | 12 |
| 85 | `Script/ScriptUtilM.cs` | 🔴 폐기 | 12 |
| 76 | `Table/SrvTableM.cs` | ⏳ | 06 |
| ~600 | `FbsClassM/` (9 파일) | 🔴 폐기/🔵 참고 | [02](02-packet-framing.md) |
| — | `Unused/` (8 파일) | 🔴 폐기 | 12 |

### `LagacyClient/` — 레거시 클라이언트 (로컬 참조 전용)

`.NET Framework 4.8` 타깃. 서버 소스 36개를 **파일 링크로 공유**한다 → [05-client.md](05-client.md)

| 줄수 | 파일 | 판정 | 문서 |
|---:|---|---|---|
| 749 | `ClientM.cs` | 🟡 개작 | [05](05-client.md#clientm) |
| 408 | `IoPipelineClaM.cs` | 🔴 폐기(중복) | [05](05-client.md#iopipelineclam) |
| 66 | `ClientTimeM.cs` | 🟢 승계 | [05](05-client.md#clienttimem--시각-동기화-유틸) |
| 59 | `TimerClaM.cs` | 🟡 개작 | [05](05-client.md) |
| 16 | `IniOptionClaM.cs` | 🔴 폐기 | [05](05-client.md) |
