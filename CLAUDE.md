# ChServerM — 모듈형 초고성능 서버/클라이언트 프레임워크

C#(.NET 10 / C# 14) 기반의 **상업용 고성능 서버·클라이언트 프레임워크**를 만든다.
단일 게임/서비스용 서버가 아니라, **기능 단위를 골라 조립해서 서버를 구성하는 프레임워크**가 최종 산출물이다.

## 1. 프로젝트 목표

1. **성능 최우선** — 모든 영역에서 현시점 최고 성능 라이브러리/알고리즘을 선택한다. 성능 주장은 항상 벤치마크로 증명한다.
2. **조립 가능(Composable)** — 직렬화, 전송(transport), 로깅, 상태 저장 등 모든 축을 옵션/빌더로 교체할 수 있다.
   예: `.UseProtobuf()` ↔ `.UseFlatBuffers()` ↔ `.UseMemoryPack()`, `.UseTcp()` ↔ `.UseHttp()`
3. **스케일러블** — 단일 프로세스 스케일업과 다중 노드 스케일아웃을 모두 설계에 반영한다.
   스케일업의 기준은 **"코어 수 대비 처리량이 선형에 근접"** 이다. 스레드를 많이 쓰는 것이 아니라 **코어가 서로를 방해하지 않게** 만드는 것이 목표다 → **9. 병렬성 규약**
4. **점진적 구현** — 처음부터 전부 구현하지 않는다. **먼저 추상화 경계를 정확히 긋고**, 구현체는 하나씩 채운다. 확장 지점이 나중에 뚫리는 일이 없도록 하는 것이 1차 목표다.

## 2. 아키텍처 원칙 (하드 룰)

이 규칙들은 편의를 위해 깨지 않는다. 깨야 한다면 `docs/DECISIONS.md`에 ADR을 남긴다.

- **Core는 무의존(zero third-party dependency).** `ChServerM.Core`는 추상화·인터페이스·값 타입만 담는다. 벤더 타입(`Google.Protobuf.*`, `StackExchange.Redis.*` 등)이 Core의 public API에 절대 노출되지 않는다.
- **벤더는 어댑터 패키지에 격리한다.** 구현체는 `ChServerM.<축>.<벤더>` 어셈블리에만 존재한다. 어댑터를 삭제해도 Core는 컴파일된다.
- **리플렉션 대신 소스 제너레이터.** 런타임 리플렉션/`Expression.Compile`/동적 프록시 금지. Native AOT 호환성과 콜드스타트를 위해 컴파일 타임 코드 생성을 쓴다.
- **핫패스는 무할당(zero-allocation).** 커넥션당·메시지당 힙 할당 0을 목표로 한다. `Span<T>`/`Memory<T>`, `ArrayPool<T>`, `struct` + `in`/`ref readonly`, `ValueTask`를 기본으로 쓴다.
- **공유하지 않는 것이 1순위, 락 금지는 2순위.** 공유 상태를 어떻게 보호할지 고민하기 전에 **키 기반 파티셔닝으로 공유 자체를 없앤다**. 핫패스에 `lock` 금지, 채널/큐/CAS 기반 무락 구조를 쓴다. 락이 필요하면 왜 필요한지 주석으로 남긴다. → 상세: **9. 병렬성 규약**
- **락-프리 상태는 `finally`로 복원한다.** `Interlocked` 카운터·플래그로 소유권을 선출했으면 해제를 `finally`에 넣는다. 이것을 빠뜨리면 예외 하나가 서버를 영구 정지시킨다 (레거시의 실제 사례).
- **`async void` 금지, `Task.Run` 남용 금지, `async` 경로에서 블로킹 금지.** `.Result`/`.Wait()`/`Thread.Sleep`은 스레드풀 고갈로 전체를 멈춘다. 소켓 루프는 전용 스케줄러 위에서 돈다.
- **무제한 큐 금지.** 유계 채널 + 백프레셔 또는 명시적 거부. **거부가 붕괴보다 낫다.**
- **인터페이스는 최소 표면.** 확장 지점은 "교체 가능한 축" 단위로만 만든다. 추상화가 늘어나는 것 자체가 비용이다.
- **측정 없는 최적화 금지.** BenchmarkDotNet 결과 없이 "빠르다"고 쓰지 않는다.

## 3. 교체 가능한 축(Pluggable Axes)

각 축은 Core의 인터페이스 + N개의 어댑터 패키지로 구성된다.

| 축 | Core 추상화 | 후보 구현체 |
|---|---|---|
| 직렬화 | `IMessageSerializer` | MemoryPack, Google.Protobuf, protobuf-net, FlatSharp(FlatBuffers), MessagePack-CSharp |
| 전송 | `IServerTransport` / `IClientTransport` | Kestrel Socket Transport(raw TCP), 순수 `Socket`+Pipelines, Kestrel HTTP/1.1·2·3, WebSocket, QUIC(`System.Net.Quic`), UDP 신뢰 전송 |
| 프레이밍 | `IFrameDecoder` / `IFrameEncoder` | length-prefix(varint / fixed32), 델리미터, 고정 길이 |
| 디스패치 | `IMessageDispatcher` | 소스 생성 스위치 테이블, 핸들러 레지스트리, 미디에이터 |
| 동시성 모델 | `IExecutionModel` | **키 기반 샤딩(기본)**, 스레드-퍼-코어(SPSC), 액터, 채널 워커 풀 |
| 세션 상태 | `ISessionStore` | 인메모리, Redis, Garnet, Tsavorite(로컬 KV) |
| 로깅 | `IServerLogger` | ZLogger(무할당), Serilog, `Microsoft.Extensions.Logging` |
| 관측 | `IMetricsSink` | OpenTelemetry + Prometheus, `System.Diagnostics.Metrics` |
| 압축 | `IPayloadCodec` | LZ4, Zstd, Brotli, none |
| 보안 | `ITransportSecurity` | TLS(`SslStream`), 없음, 커스텀 |
| DI | (호스팅 계층) | `Microsoft.Extensions.DependencyInjection`, Pure.DI/Jab(컴파일 타임, AOT용) |
| 클러스터 | `IClusterMembership` | 정적 목록, Consul/etcd, Orleans, Proto.Actor |

**축을 추가할 때의 순서:** Core 인터페이스 → 참조 구현 1개 → 벤치마크 → 두 번째 구현. 두 번째 구현이 나오기 전까지 추상화는 "가설"로 취급한다.

### 워크로드는 전제가 아니라 프로필이다 (ADR-0004)

**이 프레임워크에 목표 워크로드는 없다.** 축 구현체의 조합이 워크로드를 만든다.
"게임 서버용"이나 "API 서버용" 같은 전제를 Core에 넣지 않는다 — 넣는 순간 축 교체 가능성이라는 존재 이유가 사라진다.

대신 조립 가능성을 증명하는 **참조 프로필 2개**를 상시 유지한다 (`Samples/`).

| 프로필 | 축 조합 |
|---|---|
| `realtime-stateful` | TCP + 고정 헤더 프레이밍 + 유저별 순서 보장 실행 모델 + 인메모리 세션 |
| `stateless-web` | HTTP/Kestrel + 무상태 + 외부 세션 저장소 + 병렬 실행 모델 |

**두 프로필이 같은 핸들러 코드로 동작해야 한다.** 이것이 조립 가능성의 합격 기준이다. 한쪽만 도는 추상화는 추상화가 아니다.

워크로드 특화 기능(틱 루프, 룸/AOI, 매치메이킹, 데이터 테이블)은 **선택 축**으로 별도 패키지에 격리한다. Core는 그 존재를 알지 않으며, 전부 빼도 프레임워크가 성립해야 한다.

## 4. 적용 디자인 패턴

기능 조립 방식을 결정하는 패턴들. 새 코드는 여기에 맞춘다.

- **Builder** — `ServerBuilder`/`ClientBuilder` 플루언트 API. 프레임워크의 정면 출입구.
- **Options** — 각 축의 설정은 `XxxOptions` POCO + 검증(`IValidateOptions<T>`).
- **Strategy / Abstract Factory** — 직렬화·전송 등 축의 구현 교체.
- **Chain of Responsibility (Middleware Pipeline)** — 메시지 처리 파이프라인. ASP.NET Core 미들웨어와 동일한 멘탈 모델.
- **Decorator** — 메트릭·트레이싱·압축·암호화 등 횡단 관심사는 데코레이터로 감싼다. 코어 로직을 오염시키지 않는다.
- **Object Pool / Flyweight** — 커넥션, 버퍼, 메시지 컨텍스트 재사용.
- **Mediator / Command** — 메시지 ID → 핸들러 디스패치.
- **Observer / Event Bus** — 생명주기 이벤트(연결·해제·에러).
- **Adapter** — 모든 서드파티 통합 지점.
- **Template Method** — 전송 구현체가 공유하는 커넥션 수락 루프 골격.

## 5. 솔루션 레이아웃

```
ChServerM/
├─ ChServerM.sln
├─ CLAUDE.md
├─ Directory.Build.props            # 공통 컴파일 옵션(nullable, AOT, unsafe, analyzer)
├─ Directory.Packages.props         # 중앙 패키지 버전 관리
├─ docs/
│  ├─ ARCHITECTURE.md               # 계층·의존 방향·확장 지점 상세
│  ├─ ROADMAP.md                    # Phase 0~22 계획 (체크박스 = 진행 상황의 근원)
│  ├─ DECISIONS.md                  # ADR 로그 (라이브러리/설계 선택 근거)
│  ├─ BENCHMARKS.md                 # 측정 결과 기록
│  ├─ LEGACY-INVENTORY.md           # (구) 초판 인벤토리 → docs/legacy/ 로 이전, 포인터만 유지
│  ├─ legacy/                       # 레거시 정밀 분석 14종. 인덱스: legacy/00-overview.md
│  ├─ THREAT-MODEL.md               # 위협 모델 (Phase 9에서 작성)
│  └─ standup/
│     ├─ STANDUP.md                 # 최신 스탠드업 (항상 이 파일 하나만 최신)
│     └─ history/YYYY-MM-DD.md      # 일별 아카이브
├─ Server/
│  ├─ ChServerM.Core/               # 추상화 전용. 서드파티 의존 0
│  ├─ ChServerM.Buffers/            # 풀링, 슬랩 할당, Pipelines 유틸
│  ├─ ChServerM.Hosting/            # ServerBuilder, DI, 옵션, 생명주기
│  ├─ ChServerM.Transport.Tcp/
│  ├─ ChServerM.Transport.Http/
│  ├─ ChServerM.Serialization.MemoryPack/
│  ├─ ChServerM.Serialization.Protobuf/
│  ├─ ChServerM.Serialization.FlatBuffers/
│  ├─ ChServerM.Concurrency/        # 스케줄러, 채널, 액터 런타임
│  ├─ ChServerM.Persistence.Redis/
│  ├─ ChServerM.Observability/
│  ├─ ChServerM.Cluster/
│  └─ ChServerM.SourceGen/          # 디스패치·직렬화 코드 생성기 (Roslyn)
├─ Client/
│  ├─ ChServerM.Client.Core/
│  └─ ChServerM.Client.Tcp/
├─ Samples/                         # 축 조합별 최소 예제
├─ Tests/                           # xUnit 단위·통합
└─ Bench/                           # BenchmarkDotNet(마이크로) + NBomber(부하)
```

**의존 방향은 한 방향이다:** `Samples/Tests` → `Hosting` → `어댑터` → `Core`. Core는 아무것도 참조하지 않는다.

## 6. 성능 기술 스택

### 런타임 설정 (`Directory.Build.props`에 고정)
- `net10.0`, `LangVersion=14`, `Nullable=enable`, `AllowUnsafeBlocks=true`
- `ServerGarbageCollector=true`, `ConcurrentGarbageCollector=true`, `TieredPGO=true`
- 라이브러리 전체 `IsAotCompatible=true` — 리플렉션 유입을 컴파일 타임에 차단하는 장치로 쓴다
- `InvariantGlobalization=true`, 배포 대상은 Native AOT 또는 R2R

### I/O
- `System.IO.Pipelines` — 버퍼 관리와 백프레셔의 기본 축
- **Kestrel Socket Transport** (`Microsoft.AspNetCore.Connections.Abstractions`)를 raw TCP에 재사용 검토 — 검증된 소켓 엔진을 HTTP 없이 쓴다. 참고 구현: Bedrock Framework
- `SocketAsyncEventArgs` 재사용, `ValueTask` 기반 수신 루프
- 스레드-퍼-코어 + CPU 어피니티, NUMA 인식은 후순위 옵션

### 메모리
- `ArrayPool<T>` / `MemoryPool<T>`, 커스텀 슬랩 할당기
- `stackalloc` + `Span<T>`, 고정 크기 struct 메시지 헤더
- 대형 객체 힙 회피, `RecyclableMemoryStream`
- `System.Runtime.Intrinsics` SIMD, `System.IO.Hashing.XxHash3`

### 동시성
- `System.Threading.Channels`(무락 큐), Disruptor-net(링 버퍼) 후보
- `Interlocked` CAS 구조, false sharing 회피용 패딩
- 액터 모델은 Orleans 9 / Proto.Actor를 어댑터로 검토 (Core에 침투 금지)

### 직렬화 (성능 순 초기 가설, 벤치마크로 확정)
1. **MemoryPack** — C#↔C# 최속, zero-encoding, 소스 제너레이터
2. **FlatSharp** — FlatBuffers, 역직렬화 없는 랜덤 접근
3. **Google.Protobuf / protobuf-net** — 크로스 언어 상호운용이 필요할 때
4. **MessagePack-CSharp** — 균형형 대안

### 관측·검증
- **ZLogger** — 무할당 구조적 로깅
- OpenTelemetry(트레이스·메트릭) + Prometheus 익스포터
- **BenchmarkDotNet** — 모든 핫패스 마이크로 벤치마크
- **NBomber** — 종단 부하 테스트
- xUnit + Testcontainers(Redis 등 통합 테스트)

## 7. 작업 방식 / 연속성

작업은 git과 **standup 스킬**로 이어간다.

- **`/standup`** — 세션 시작 시 실행. `docs/ROADMAP.md` + `docs/standup/STANDUP.md` + git 로그를 읽어 "지난 작업 / 오늘 할 일 / 블로커"를 정리한다.
- **`/standup wrap`** — 세션 종료 시 실행. 실제로 한 일을 `STANDUP.md`에 갱신하고 `docs/standup/history/YYYY-MM-DD.md`로 아카이브, ROADMAP 체크박스를 갱신한다.
- 스킬 정의: `.claude/skills/standup/SKILL.md`

### 진행 상황의 근원(source of truth)
| 파일 | 역할 |
|---|---|
| `docs/ROADMAP.md` | 무엇을 해야 하는가. 체크박스가 유일한 진행률 기준 |
| `docs/standup/STANDUP.md` | 지금 어디까지 왔는가. 항상 최신 1개 |
| `docs/standup/history/` | 일별 기록. 추가만 하고 수정하지 않는다 |
| `docs/DECISIONS.md` | 왜 그렇게 정했는가. ADR 추가만, 뒤집을 때는 새 ADR로 supersede |
| `docs/BENCHMARKS.md` | 성능 주장의 근거 |
| `docs/legacy/` | 레거시 정밀 분석(14종). 승계 대상 구현 전에 해당 문서를 읽는다. 각 문서의 "새 코드에 절대 옮기면 안 되는 것"이 회귀 방지 체크리스트 |
| `git log` | 사실 관계의 최종 근거 |

### 커밋 규칙
Conventional Commits. 스코프는 어셈블리 축 이름을 쓴다.
```
feat(transport.tcp): Pipelines 기반 수신 루프 추가
perf(buffers): 슬랩 할당기 커넥션당 할당 제거 (48B → 0B)
docs(adr): ADR-00NN 기본 직렬화로 MemoryPack 채택
chore(standup): 2026-07-30
```
- 커밋/푸시는 사용자가 요청할 때만 한다. 단, `/standup wrap`은 스탠드업 문서 커밋을 포함한다.
- `perf(...)` 커밋은 본문에 before/after 수치를 반드시 포함한다.

## 8. 코드 컨벤션

- 파일 스코프 네임스페이스, `var`는 타입이 우변에서 자명할 때만
- public API 전부 XML 문서 주석 — 프레임워크가 산출물이므로 API 문서가 제품의 일부다
- 인터페이스 `I` 접두, 어댑터는 `<벤더><축>` (예: `MemoryPackMessageSerializer`)
- 주석은 "왜"만 쓴다. "무엇"은 코드가 말한다. 성능 트릭에는 근거 수치나 링크를 남긴다
- 예외는 예외적 상황에만. 핫패스 제어 흐름에 예외를 쓰지 않는다 (`TryXxx` 패턴)
- `ConfigureAwait(false)` — 라이브러리 코드 전역 적용

## 9. 병렬성 규약 — 멀티코어를 최대한 활용한다

**목표는 "코어 수 대비 처리량이 선형에 근접"이다.** 단순히 스레드를 많이 쓰는 것이 아니라 **코어가 서로를 방해하지 않게** 만드는 것이 목표다.

`docs/legacy/`의 전수 분석에서 승계한 자산과 확인한 실패 원인을 규약으로 고정한다.
각 항목의 근거는 링크를 따라간다.

### 9.1 ⚠ 1순위 — 락이 아니라 파티셔닝 (share-nothing)

**공유 상태를 어떻게 보호할지 고민하기 전에, 공유하지 않는 방법을 먼저 찾는다.**

```
파티션 키(SessionId 등)의 안정 해시 → 샤드 선택
같은 키의 작업은 항상 같은 샤드 → 단일 소비자 → 순서 보장 + 동기화 불필요
다른 키는 완전 독립 → 코어 수만큼 선형 확장
```

- **기본 전략은 키 기반 샤딩이다.** 레거시가 `oid % n`으로 송신·수신·스케줄러 3곳에서 독립적으로 재발명한 패턴이며, **락 없이 순서 보장과 병렬성을 동시에 얻는다** ([01-network-transport](docs/legacy/01-network-transport.md#sendpacketgroupm))
- 파티션 키는 **강타입 ID의 안정 해시**(`XxHash3`)로. `oid % n`처럼 원시 정수 나머지를 쓰지 않는다 — 음수·오버플로·분포 편향이 생긴다
- 샤드 수는 **옵션 + 시작 시점 검증**. static 초기자로 다른 static을 읽지 않는다 (레거시는 이 때문에 `DivideByZeroException` 가능 상태였다)
- **샤드 안에서는 동기화가 불필요하다는 사실을 계약으로 표현한다.** 그러면 그 안의 자료구조는 락도 `Concurrent*`도 쓰지 않는다. 레거시는 이 보장에 암묵적으로 의존해 `PkObjM`의 락을 제거했다가, 계약이 코드에 없어 앱이 깰 수 있는 상태가 됐다 ([02-packet-framing](docs/legacy/02-packet-framing.md#pkobjm))

### 9.2 ⚠ 락-프리 코드는 `finally`로 상태를 복원한다

**이것을 지키지 않으면 예외 하나가 서버를 영구 정지시킨다.**

- `Interlocked` 카운터·플래그로 소유권을 선출했다면, **해제를 `finally`에 넣는다.** 가능하면 `ref struct` 스코프 가드로 타입이 강제하게 한다
- 근거: 레거시 `ExecutableTaskDispatcherM`은 단일 소유자 선출 + ThreadLocal 재진입 예약이라는 **정교한 설계**인데, `try/finally`가 없어 작업 예외 하나로 **카운터가 0으로 복원되지 않아 디스패처와 해당 스레드가 영구 정지**한다 ([04-concurrency](docs/legacy/04-concurrency.md#executabletaskdispatcherm--락-없는-단일-소유자-디스패처))
- 소비자 루프는 **항목별 `try/catch`**로 감싼다. 나쁜 항목 하나가 큐 전체를 죽이지 않게

### 9.3 원자적 접근을 일관되게 쓴다

- 여러 스레드가 읽는 필드는 **`Volatile.Read`/`Volatile.Write` 또는 `Interlocked`를 일관 적용**한다. 한 곳만 `Volatile`이고 다른 곳은 평문 접근이면 안 된다 (레거시 `TimingWheelSlotM`이 `AddJob`은 `Volatile.Read`, `IsEmpty`는 평문)
- CAS 재시도 루프의 `SpinWait`는 **재시도 시에만**. 첫 시도 전에 무조건 스핀하지 않는다
- **경합하는 원자 연산도 비용이다.** 여러 코어가 같은 캐시 라인을 두고 CAS하면 락보다 느려질 수 있다 → 9.1의 파티셔닝으로 경합 자체를 없애는 것이 우선

### 9.4 false sharing을 회피한다

- 서로 다른 스레드가 자주 쓰는 필드를 **같은 캐시 라인(64B)에 두지 않는다.** 샤드별 카운터·상태는 패딩하거나 `[StructLayout]`로 정렬
- 배열 기반 샤드는 **요소 크기를 캐시 라인에 맞추거나** 샤드별 객체를 따로 할당한다

### 9.5 스레드 수를 통제한다

- **스레드 수는 옵션 + 상한이 있어야 한다.** 레거시는 송신·수신 각각 `ProcessorCount × 팩터`개의 전용 스레드를 만들어, 16코어·팩터 2.0이면 **64개 전용 스레드**가 된다 ([04-concurrency](docs/legacy/04-concurrency.md#concurseqtaskcontextexeclongrunmt--실제-사용되는-실행기))
- **커넥션당 스레드/타이머를 만들지 않는다.** 타이머는 계층적 타이밍 휠 하나로 통합한다 (`System.Threading.Timer`를 커넥션마다 만들면 스레드풀 작업이 그만큼 늘어난다, [10-time](docs/legacy/10-time.md#timermt--itimeractionm))
- `Parallel.For`/`Parallel.ForEach`를 **틱마다 호출하지 않는다.** 태스크·클로저 할당이 반복된다. 고정 워커 세트를 유지하는 편이 낫다
- **`async` 경로에서 블로킹하지 않는다.** `.Result`/`.Wait()`/`Thread.Sleep` 금지 — 스레드풀 고갈(starvation)로 전체가 멈춘다 (레거시는 `Dispose`에 `Thread.Sleep(1000)`이 있다)

### 9.6 큐는 유계로, 포화는 관측한다

- **무제한 채널을 쓰지 않는다.** 소비자가 느리면 메모리가 무한히 증가해 OOM으로 죽는다. **거부가 붕괴보다 낫다**
- `BoundedChannelFullMode.Wait`를 설정했으면 **`WriteAsync`를 쓴다.** `TryWrite`는 그 설정을 무시하고 **조용히 버린다** — 레거시가 이 조합으로 부하 시 패킷을 유실했다 ([04-concurrency](docs/legacy/04-concurrency.md#concurseqtaskcontextexecmt))
- **큐 깊이와 드롭 수를 메트릭으로 노출한다.** 조용한 유실은 관측되지 않으면 존재하지 않는 것과 같다 (Phase 11)

### 9.7 스레드 안전성을 이름과 타입으로 드러낸다

- 스레드 안전 여부가 다른 두 구현을 **같은 이름 계열로 만들지 않는다.** 레거시는 `ConcurrentQueueExecutorM`(안전)과 `QueueExecutorM`(비안전)이 **동일 API**인데 이름·문서로 구분되지 않아 오용을 유도한다
- 스레드 안전하지 않은 타입은 **XML 문서에 명시**하고, 가능하면 "어느 실행 컨텍스트에서만 접근 가능한가"를 API로 강제한다
- 공유 자원의 **소유권을 타입으로 표현**한다 — 풀 대여 버퍼는 `ref struct` 스코프나 `IMemoryOwner<T>`로. "누가 반납하는가"를 주석에만 적으면 반드시 샌다 (레거시 반납 누수 다수)

### 9.8 데이터 병렬성은 SIMD로

- 대량 동일 연산(체크섬, 압축, 좌표 변환, 브로드캐스트 대상 필터링)은 **`System.Runtime.Intrinsics`** 를 검토한다. 하드웨어 지원 검사 + 스칼라 폴백을 함께 둔다
- `Vector2`/`Vector3`/`Vector<T>`(`System.Numerics`)는 자동 SIMD 가속을 받는다 — 직접 만든 좌표 struct보다 우선한다

### 9.9 병렬성 주장은 측정으로 방어한다

- **코어 수 대비 확장성 곡선**을 벤치마크로 남긴다(1·2·4·8·16코어). "병렬화했다"가 아니라 "선형에 근접한다"를 증명한다
- 동시성 버그는 **반복 실행 테스트**로 잡는다. 단발 테스트는 경합을 재현하지 않는다
- 락·원자 연산을 도입·제거할 때 **before/after 수치**를 `perf(...)` 커밋 본문에 남긴다

## 10. AI 에이전트 작업 지침

- 코드를 쓰기 전에 **어느 축에 속하는지, 어느 어셈블리에 들어가는지** 먼저 확정한다. Core에 들어갈 후보라면 서드파티 의존이 없는지 먼저 확인한다.
- 라이브러리를 새로 도입할 때는 `docs/DECISIONS.md`에 ADR을 남긴다. 대안과 탈락 이유를 반드시 적는다.
- 성능 관련 변경은 벤치마크 코드를 함께 만든다. 수치 없는 최적화는 하지 않는다.
- **동시성 코드를 쓸 때는 9절을 먼저 읽는다.** 특히: 공유 대신 파티셔닝(9.1), `finally`로 상태 복원(9.2), 무제한 큐 금지(9.6). 이 셋은 레거시에서 실제로 서버를 멈추거나 데이터를 유실시킨 항목이다.
- 승계 대상을 구현할 때는 `docs/legacy/`의 해당 문서를 읽고, 그 문서의 **"새 코드에 절대 옮기면 안 되는 것"** 을 체크리스트로 쓴다.
- 요청 범위를 임의로 넓히지 않는다. "확장 가능하게" 만드는 것과 "지금 다 구현하는 것"은 다르다.
- 구현이 막히거나 설계 선택이 갈리면, 판단을 대신하지 말고 대안과 트레이드오프를 제시한다.
