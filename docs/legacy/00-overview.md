# 00 — 레거시 종합 개요

`LegacyServer/`(111 파일 / 약 26,000줄) + `LagacyClient/`(6 파일 / 1,298줄) 전수 분석 결과 요약.
**세부는 문서 01~12를 본다.** 이 문서는 전체 그림과 결론만 담는다.

---

## 1. 정체 — 무엇인가

**Arch ECS 기반의 실시간 게임 서버·클라이언트 프레임워크**다. 네임스페이스 `EcsServerLibM`의 "Ecs"가 Entity Component System이다.

| | 서버 | 클라이언트 |
|---|---|---|
| 프로젝트 | `ServerM.csproj` | `ClientM.csproj` |
| 타깃 | `.net9` (표기 오류, `net9.0`이어야 함) | **`.NET Framework 4.8`** |
| 최종 소비자 | 콘솔/WinForms | **Unity** (`#if UNITY_EDITOR`) |
| 코드 공유 | — | **서버 소스 36개를 파일 링크로 컴파일** |

**ECS는 전면 도입이 아니다.** `Arch`를 실제로 쓰는 파일은 `HierachyM.cs`, `BoxColliderM.cs` **2개뿐**이고, 네트워크·패킷·세션 계층은 전통 OOP다. ECS 시스템 스케줄링 계층은 `Unused/EcsSystemM.cs`(20.9K)로 폐기됐다.

### 의존성

`Arch`(ECS) · `Google.FlatBuffers` · `K4os.Compression.LZ4` · `System.IO.Pipelines` · `Microsoft.Extensions.DependencyInjection` · `Microsoft.Extensions.Identity.Core` · `MongoDB.Driver` · `log4net` · `Microsoft.CodeAnalysis.CSharp`(Roslyn) · **`System.Windows.Forms`** · `Collections.Pooled` · `CommunityToolkit.HighPerformance`

> **`Microsoft.Extensions.DependencyInjection`이 이미 참조돼 있다.** DI는 ChServerM의 새 시도가 아니라 **레거시가 시작하고 완성하지 못한 방향**이다(실제 코드는 static 전역).

---

## 2. 데이터 흐름 (서버)

```
TcpListener.AcceptTcpClientAsync                        ServerM.AsyncServerReady
  │ NoDelay = true, Task.Run(fire-and-forget)
  ▼
IoPipelineSrvM.PipelineForServerAsync                   커넥션당 Pipe 1개
  ├─ SrvFillPipeAsync   NetworkStream → PipeWriter
  └─ SrvReadPipeAsync   PipeReader → 5단 상태 머신
        PK_HEAD(28B) → CONTENT_HEAD(24B) → CONTENT_DATA
        ENC_PK_HEAD(32B) → ENC_PK_DATA        (암호 모드 전환 후)
  ▼
ServerM.SendMemPk / SendEncMemPk                        화이트리스트 1차 검증
  ▼
SendPacketGroupM.SendMemPacket(oid, memPk)
  │ idx = oid % cntIncommingPkActBlock                  ★ 유저별 순서 보장
  ▼
ConcurSeqTaskContextExecLongRunM<MemPacketM>[idx]       샤드별 전용 스레드
  ▼
MemPkDispatcher.MemPkAction                             화이트리스트 2차 검증
  │ Dictionary<PACKET_TYPE, AbMemPkAction>
  ▼
AbMemPkAction 파생 핸들러 → 앱 코드

송신은 대칭: PkObjM.WriteSendBuffer → FlushSendBuffer
          → SendPacketGroupM.SendPacket(oid) → idx = oid % cntOutGoingPkActBlock
          → PacketM.SendPacket → NetworkStream.WriteAsync
```

**시간 축**: `TimeEventSchedulerM`(5단 계층적 타이밍 휠)이 전역 하나. `HashM` 만료, `BaseGameObjM`/`MapObjM`의 예약 작업이 여기 올라간다.

---

## 3. 🟢 승계할 자산 (설계)

문서 01~12에서 확정한 것. **설계를 승계하고 구현은 재작성한다.**

| # | 자산 | 출처 | Phase |
|---|---|---|---|
| 1 | **`oid % n` 샤딩** — 락 없이 유저별 순서 보장 + 선형 병렬성. 송신·수신·스케줄러 3곳에서 독립 반복 | `SendPacketGroupM`, `ConcurrentSchedulerGroupM` | 1·8 |
| 2 | **5단 계층적 타이밍 휠** — 삽입 O(1), 틱당 O(1). Treiber 스택 슬롯 + 원자적 배치 추출 + 상위→하위 캐스케이딩 | `TimeEventSchedulerM` | 8·17 |
| 3 | **상태 기반 패킷 화이트리스트** — Composite + Builder. 인증 전 패킷 차단 | `AllowedPacketMan` | 2·9 |
| 4 | **단일 소유자 선출 + ThreadLocal 재진입 예약** — 액터 메일박스의 재귀·교착 해법 | `ExecutableTaskDispatcherM` | 8 |
| 5 | **무할당 비동기 시그널** — `RunContinuationsAsynchronously` + static `ValueTask` 빠른 경로 | `AsyncManualResetEventM` | 8 |
| 6 | **2단 종료** — FIN → 타임아웃 → 강제 close | `InnerSrvUserM`, `TimerM_User_Disconnect_Force` | 5 |
| 7 | **지연 disconnect 재시도** — 로그인 처리 중 종료 레이스 대응(지수 백오프) | `TimerM_SrvUser_Delay_Disconnect` | 5 |
| 8 | **Inner/Wrapper 안전 핸들** — 삭제된 세션을 만져도 크래시하지 않음 | `UserM` / `InnerUserM` | 1·13 |
| 9 | **델타 전송 dirty 플래그** — 5종 변경 유형 구분 | `NeedPkSendM` | 18 |
| 10 | **이동 이중적용 방지** — 즉시 이동과 고정 틱의 충돌 해소 | `LastMoveTickM` | 17 |
| 11 | **Z-order 공간 인덱싱** (모튼 코드) | `MortonCodeM` | 18 |
| 12 | **Enter/Stay/Exit 집합 차분 + Stay 스로틀** | `BoxColliderM` | 18 |
| 13 | **맵 단위 브로드캐스트 계약** | `MapObjM` | 18 |
| 14 | **IQR 이상치 제거 + 정렬 활용 필터링** | `InterQuartileM` | 11·17 |
| 15 | **서버 주파수 전달 → 클라 틱 정규화 + 시각 외삽** | `ClientTimeM`, `ClientM.ServerTickCurrent` | 17 |
| 16 | **`PasswordHasher` 채택** (PBKDF2 + 솔트) | `AuthM` | 9 |
| 17 | **MongoDB 연결 풀 튜닝값 + `FindOneAndUpdate` 원자적 get-or-create** | `MongoDBManagerM` | 13 |
| 18 | **송신 배칭** (syscall 절감) + `ReadOnlySequence` 쓰기 경로 | `PkObjM` | 5 |
| 19 | **인터벌 게이트** (주기 실행) | `ElapsedTimeManM` | 17 |
| 20 | **문자열 테이블 → 강타입 매핑 3단 + 서버 테이블을 클라에 전송** | `MetaDataM`, `SrvTableM` | 7·14 |
| 21 | **`PACKET_TYPE` ID 공간 분리 + 방향 접두어 규약** | `PacketM` | 4·7 |
| 22 | **Observer로 유저 종료 전파 + `DISCONNECTING` 상태** | `InnerUserM` | 13·18 |

---

## 4. 🔴 치명 결함 — 유형별 분류

문서 01~12에서 식별한 40건 이상을 **원인별로** 묶었다. 개별 항목은 각 문서의 "새 코드에 절대 옮기면 안 되는 것"에 있다.

### (A) 리소스 소유권이 타입이 아니라 주석에만 있다

`ArrayPool` 미반납(01·02·06), `PooledList` 미Dispose(04), 무제한 `ObjectPoolM`(04·12), 대여 소유권이 `out bool`에 실림(07).
**주석은 "누가 반납한다"고 적혀 있으나 코드가 강제하지 않는다.**

→ **Phase 3에서 소유권을 타입으로 표현하는 것이 이 계열을 구조적으로 없애는 유일한 방법이다.**

### (B) `try/finally` 부재로 락-프리 상태가 복원되지 않는다

`ExecutableTaskDispatcherM`(04) — 작업 예외 하나로 카운터와 ThreadLocal이 복원되지 않아 **디스패처와 해당 스레드가 영구 정지**한다.

→ 락 없는 카운터·플래그를 승계할 때 **상태 복원을 `finally`로 강제**하고, 가능하면 `ref struct` 스코프 가드로.

### (C) 조용히 실패한다 — 관측 부재와 직결

| 기능 | 실제 상태 | 문서 |
|---|---|---|
| 체크섬 검증 | `return true` 한 줄. 검증이 존재하지 않는다 | 02 |
| LZ4 압축 | 조기 반환 조건이 항상 참. **한 번도 실행되지 않는다** | 07 |
| MongoDB 재시도 | `throw`가 `if` 밖. 백오프만 하고 재시도하지 않는다 | 08 |
| `IHasTimeEventsM` | `TimeEvents`에 추가하는 코드가 없다. 타이머 취소가 죽어 있다 | 07 |
| `HashM` 만료 | 작업 ID가 전역 충돌. 두 번째 오브젝트부터 만료 무동작 | 07 |
| 백프레셔 | `FullMode.Wait` + `TryWrite` 조합. 설정만 되고 동작하지 않는다 | 04 |
| 콜라이더 비활성화 | `_enabled`를 아무도 검사하지 않는다 | 03 |

**원인**: 로그 레벨이 없고(09), 설정 파일이 없으면 로깅이 통째로 사라지며(09), `Debug.WriteLine`이 Release에서 소멸하고(전역), **메트릭이 전혀 없다**.

→ **Phase 11은 "실패가 관측되는가"를 설계 목표로 삼는다.** 조용한 실패가 가능한 지점마다 카운터를 두고 0이 아니면 경보한다.

### (D) 보안 — `AuthM`을 제외하면 전량 재설계

인증되지 않은 RSA 키 교환(MITM 노출), 서버→클라 XOR "암호화", AES-128 + 세션 전체 IV 고정 + 인증 없는 CBC, PKCS#1 v1.5, 커넥션마다 RSA 2048 생성(DoS), 비밀번호 검증 결과 무시, 소스에 DB 자격증명, 와이어 값으로 배열 할당(메모리 고갈), 최대 프레임 크기 상한 부재.

→ **Phase 9의 위협 모델 문서는 이 목록을 출발점으로 쓴다.** 1순위는 TLS 위임.

### (E) 크로스 플랫폼 불가 — 4개 요인

| 의존 | 위치 | 문서 |
|---|---|---|
| `System.Windows.Forms` | `ServerM.csproj`. UI 스레드 코드·`ScreenLibM`·`SparseSetM`·`MongoDBManagerM`까지 확산 | 03·04·08 |
| `System.Drawing` (`PointF`) | `MathM.cs` — .NET 6+ Windows 전용 | 03 |
| `IOControlCode.KeepAliveValues` | `NetWorkM.cs` | 01 |
| `@"SysTable\ServerConfig.smt"` | `SrvTableM.cs` — 경로 구분자 하드코딩 | 11 |

→ ChServerM의 **ubuntu + windows CI 매트릭스**는 이 문제의 구조적 재발 방지 장치다.

### (F) 검증된 적 없는 코드 — 충돌 계층

미수정 버그 8건이 겹쳐 **회전 미적용 · 위치 영구 고정 · 축정렬 quad 충돌 항상 false · 접촉점 무의미**. 공통 원인은 **struct 복사본을 수정하고 버리는 것**(인터페이스 언박싱, `Nullable<T>.Value`).

→ Phase 18 재작성 시 **단위 테스트를 먼저 쓴다.**

### (G) 중복

`TryMakeSendPacketData` 로직 4벌(02), 콜라이더 2종 240줄×2(03), `IoPipelineSrvM`/`ClaM` 400줄(05), 스케줄러 4종(04), `SparseSet` 4종(04), `ConcurrentQueueExecutorM` 쌍둥이(04).

→ **프레이밍은 Core에 한 벌만.** 서버·클라가 같은 `IFrameDecoder`를 쓴다.

---

## 5. 정량 근거 (ADR·Phase 목표용)

| 항목 | 레거시 | ChServerM 목표 | 근거 문서 |
|---|---|---|---|
| 평문 패킷 헤더 | **52 B** (FlatBuffers, 실제 데이터 13 B) | **13~16 B** (고정 struct) | 02 |
| 패킷당 힙 할당 (평문) | **최소 5개** | **0개** | 02 |
| 패킷당 힙 할당 (암호) | **8개 이상** | **0개** | 02 |
| 커넥션당 송신 버퍼 | **64 KB 고정** (1만 커넥션 = 640 MB) | 풀 대여, 유휴 시 0 | 02 |
| 화이트리스트 조회 | O(n) + 가상 호출 n회 | **O(1) 비트맵** | 02 |
| 디스패치 조회 | 딕셔너리 해시 | 생성 스위치 테이블 | 02 |
| `RectM` 1개 생성 | **힙 할당 2회** | **0회** (16 B struct) | 03 |
| 충돌 검사 1회 | 인터페이스 박싱 + 배열 접근 | 제네릭 특수화, 무할당 | 03 |
| 타이머 | 커넥션당 스레드풀 `Timer` | 타이밍 휠 O(1) | 04·10 |
| 지연 통계 | `IConvertible` 박싱 (원소마다) | `INumber<T>` 또는 전용 오버로드 | 09 |

---

## 6. 코드 구성 통계

| 분류 | 줄수 | 비율 |
|---|---:|---:|
| 승계·개작 대상 | 약 11,600 | 45% |
| **미참조 코드** | 약 9,000 | **35%** |
| 주석 처리된 코드 (활성 파일 내) | 약 2,900 | 11% |
| 명시적 폐기 (`Unused/`, Jira, Unity) | 약 2,500 | 9% |

**실제로 동작하며 승계 가치가 있는 코드는 절반 이하다.** 그 절반 안에서도 치명 버그 40건 이상이 있다.

→ **"레거시를 개선해서 옮긴다"가 아니라 "설계를 승계하고 구현은 재작성한다"가 옳다.** ADR-0000의 방향과 일치한다.

---

## 7. ROADMAP·인벤토리에 반영해야 할 정정

정독 과정에서 **파일명·주석 기반 초기 판정이 틀린 것**이 여럿 확인됐다.

| 대상 | 초기 기재 | 실제 | 문서 |
|---|---|---|---|
| `QuadTreeM.cs` | AOI 승계 후보 | **빈 파일.** QuadGrid 구현이 존재하지 않음 | 03 |
| `FileWatcherSystemM` | 핫 리로드 승계 후보 | **참조 0** | 11 |
| `MemoryPoolM`, `StackMemAllocM`, `UnsafeCopyBlock` | Phase 3 버퍼 승계 후보 | **참조 0 / 전체 주석** | 12 |
| `HashM` | 보안·해시 | **만료 지원 KV 저장소** (Redis HSET+EXPIRE 대응) | 03·07 |
| `ProgressBarM` | 콘솔 UI, 폐기 | 풀링되는 **게임 오브젝트 컴포넌트** (그래도 폐기) | 03·12 |
| 체크섬 검증 | 승계 자산 | **검증이 존재하지 않음** (`return true`) | 02 |
| 비밀번호 | 평문 직렬화 | **AES 암호화됨.** 단 키 교환 미인증으로 MITM 노출 | 02·05 |
| AES 강도 | AES-256 | **AES-128** | 07 |
| `MongoDBManagerM` | ECS 사용 | **잉여 `using`.** ECS 사용 파일은 2개뿐 | 03·08 |
| `HashM` 스레드 안전성 | 안전하지 않음 | `ConcurrentDictionary` 사용. 주석은 **더 오래된 경로**를 가리킴 | 04·07 |
| `InIFileM.cs` | 참조 0 | **사용 중.** 클래스명이 `IniFileM`이라 파일명 검색이 놓침 | 11 |
| `MembersM` 멤버 그룹 | 브로드캐스트 승계 후보 | **전량 주석.** 활성 코드 없음 | 06 |
| 레이팅 시스템 | ADR-0003 근거 | **참조 0.** 사용되지 않는 준비 코드 | 12 |
| 스크립트 시스템 | 오브젝트 모델과 분리 불가 | **`RoslynCompilerM` 미배선.** 제거 비용 낮음 | 03·12 |

### 반영 작업 목록

| 파일 | 작업 |
|---|---|
| `docs/LEGACY-INVENTORY.md` | 이 문서(`docs/legacy/`)로 대체하고 **포인터만 남긴다** |
| ROADMAP Phase 3 | 버퍼 "레거시 승계" 전제 삭제 — 처음부터 설계 |
| ROADMAP Phase 14 | `FileWatcherSystemM` 핫 리로드 승계 기재 삭제. Excel 임포트를 **빌드 타임 도구**로 확정 |
| ROADMAP Phase 18 | QuadGrid/AOI 승계 기재 삭제 (설계부터). 콜라이더 **단위 테스트 선행** 명시 |
| ROADMAP Phase 19 | 레이팅을 `Samples/` 배치로 명시 |
| ROADMAP Phase 1 | **`ObjectId`에 노드 성분 필수** — 전역 카운터는 Phase 15를 막는다 (문서 06) |
| ROADMAP Phase 9 | 위협 모델 출발점으로 문서 07의 목록 사용 |
| ROADMAP Phase 11 | "실패가 관측되는가"를 설계 목표로 추가 |

---

## 8. 문서 색인

| 문서 | 범위 | 정독 |
|---|---|---|
| [01-network-transport](01-network-transport.md) | 부트스트랩, Pipelines, 전송 샤딩 | 2,231줄 |
| [02-packet-framing](02-packet-framing.md) | 패킷 조립, 프레이밍, 화이트리스트 | 2,619줄 |
| [03-ecs-object-model](03-ecs-object-model.md) | ECS 컴포넌트, 공간·충돌, 모튼 코드 | 2,371줄 |
| [04-concurrency](04-concurrency.md) | 실행기, 타이밍 휠, 스케줄러 4종, SparseSet | 3,331줄 |
| [05-client](05-client.md) | 클라이언트 전량, 소스 공유 모델, 핸드셰이크 | 1,298줄 |
| [06-session-user](06-session-user.md) | 유저 모델, 옵저버, 전역 OID | 653줄 |
| [07-security](07-security.md) | 암호·압축, 인증, 만료 KV | 439줄 |
| [08-persistence](08-persistence.md) | MongoDB 파사드, 재시도, 스키마 | 776줄 |
| [09-observability](09-observability.md) | 로깅, 로그 수집기, IQR 통계 | 564줄 |
| [10-time](10-time.md) | 틱, 타이머, 시간 표현 3종 | 462줄 + 판정 |
| [11-data-table](11-data-table.md) | 메타 테이블, INI, 파일, Excel | 판정 + 참조 분석 |
| [12-domain-util-discarded](12-domain-util-discarded.md) | 도메인, 유틸, 미참조 9,000줄 | 참조 분석 |
