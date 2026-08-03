# ChServerM 로드맵

체크박스가 **진행률의 유일한 기준**이다. `/standup wrap`이 이 파일을 갱신한다.

## 완료 기준 (Definition of Done)

항목을 `[x]`로 바꾸려면 **전부** 충족해야 한다. 하나라도 빠지면 `(진행 중: 남은 것)`으로 남긴다.

1. 코드 + 단위 테스트 통과
2. public API에 XML 문서 주석 (프레임워크가 산출물이므로 문서가 제품의 일부)
3. 성능에 영향이 있으면 **벤치마크 수치**가 `docs/BENCHMARKS.md`에 있을 때
4. 라이브러리·아키텍처 선택이 있었으면 **ADR**이 `docs/DECISIONS.md`에 있을 때
5. 축을 추가했으면 **두 번째 구현체 또는 교체 테스트**가 있을 때 (추상화가 실제로 교체 가능함을 증명)

## 검증용 참조 프로필 (ADR-0004)

**이 프레임워크에는 목표 워크로드가 없다.** 축 구현체의 조합이 워크로드를 만든다.
Phase 순서는 워크로드가 아니라 **"무엇이 추상화를 먼저 증명하는가"**로 정한다.

대신 조립 가능성을 증명하는 참조 프로필 2개를 상시 유지한다. `Samples/`의 산출물이다.

| 프로필 | 축 조합 | 증명하는 것 |
|---|---|---|
| `realtime-stateful` | TCP + 고정 헤더 프레이밍 + 유저별 순서 보장 실행 모델 + 인메모리 세션 | 상시 연결, 순서 보장, 커넥션 생명주기 |
| `stateless-web` | HTTP/Kestrel + 무상태 + 외부 세션 저장소 + 병렬 실행 모델 | 세션 외부화, 수평 확장, 전송 교체 |

**두 프로필이 같은 핸들러 코드로 동작해야 한다. 이것이 조립 가능성의 합격 기준이다.**
한쪽만 도는 추상화는 추상화가 아니다.

`realtime-stateful`을 먼저 세우는 이유는 선호가 아니라 검증 효율이다 — 프레이밍·순서 보장·
커넥션 생명주기를 모두 통과해야 하므로 추상화를 더 강하게 압박한다. `stateless-web`은
상당 부분 그 부분집합이다.

## Part 진행 규칙

- Part 안에서는 Phase 순서를 지킨다. Part 사이는 게이트가 열리면 병행 가능하다.
- **Part III(프로덕션 필수)를 건너뛰고 Part IV 이후로 가지 않는다.** 보안·복원력 없는 기능 추가는 부채다.
- 각 Phase의 **게이트**는 다음 Phase로 넘어갈 최소 조건이다. 전 항목 완료가 아니라 게이트 충족이 기준이다.
- ⚠ 표시 항목은 되돌리기 비용이 큰 결정이다. ADR 없이 진행하지 않는다.
- **Part V(실시간 프리미티브)는 선택 축이다.** 전부 빼도 프레임워크가 성립해야 한다.
  성립하지 않으면 Core가 도메인에 오염된 것이므로 Core를 고친다.

## 2026-07-31 Phase 재배치

초판(Phase 0~11)은 보안·복원력·API 안정성·개발자 경험·게임 프리미티브가 백로그이거나
누락돼 있었다. 상업용 기준으로 전면 재구성해 Phase 0~22 + Part 구조가 됐다.
`docs/standup/history/2026-07-30.md`의 Phase 번호는 **구 번호**다. 대응은 다음과 같다.

| 구 | 신 | |
|---|---|---|
| Phase 3 버퍼 | Phase 3 | 동일 |
| Phase 4 TCP | **Phase 4 프레이밍 + Phase 5 TCP** | 프레이밍이 ADR-0002로 독립 축이 되어 분리 |
| Phase 5 직렬화 | Phase 6 | |
| Phase 6 디스패치 | Phase 7 | |
| Phase 7 동시성 | Phase 8 | |
| Phase 8 HTTP | Phase 16 대체 전송 | 프로덕션 필수(Part III) 뒤로 이동 |
| Phase 9 관측 | Phase 11 | |
| Phase 10 상태·클러스터 | **Phase 13 세션 + Phase 15 클러스터** | 분리 |
| Phase 11 패키징 | **Phase 21 릴리스 + Phase 22 출시** | 분리 |
| (없음) | Phase 9·10·12·14·17~20 | 신규 |

---

# Part I — 기반

## Phase 0 — 빌드 기반 & 품질 게이트

빌드 규약과 자동 검증 장치. 여기서 정한 컴파일 옵션과 게이트가 이후 모든 작업의 전제가 된다.
품질 게이트는 **초기에 켜야** 축적된다. 나중에 켜면 위반이 쌓여 못 켠다.

- [ ] `ChServerM.sln` 생성, `Server/` `Client/` `Tests/` `Bench/` `Samples/` 솔루션 폴더 구성 (진행 중: `Server/`·`Tests/`·`Samples/`·`Bench/` 존재. **`Client/` 만 남았다.** `dotnet sln add` 가 프로젝트 없이 폴더를 만들지 못하므로 `ChServerM.Client.*` 어셈블리를 만들 때 함께 추가한다 — 현재 클라이언트는 `ChServerM.Hosting` 의 `ClientBuilder` 와 전송 어셈블리를 그대로 쓴다)
- [x] `Directory.Build.props` — `net10.0`, C# 14, nullable, `AllowUnsafeBlocks`, `IsAotCompatible`, ServerGC, TieredPGO
- [x] `Directory.Packages.props` — 중앙 패키지 버전 관리 활성화
- [x] `.editorconfig` — 코드 스타일 + 분석기 심각도 (Performance·Reliability 카테고리를 error로 승격)
- [x] `.gitattributes` — 줄바꿈 정규화를 저장소 제어로. `core.autocrlf` 의존 제거, `.editorconfig`와 정합
- [x] `ChServerM.Core` 프로젝트 생성 — 서드파티 의존 0 검증 테스트 포함 (2중 가드: `CHSM0001` MSBuild + `CoreDependencyTests`. 참/거짓 양성 모두 검증)
- [x] CI 스크립트 (build + test + AOT 컴파일 검증) — `eng/build.ps1` 4단계 전부 통과. **AOT 검증이 실제로 동작한다** (Echo 샘플 대상, Native AOT 1.9MB 바이너리 정상 실행). 개발자 셸이 아닌 환경에서 링크가 실패하지 않도록 `vswhere` 경로를 스크립트가 보강한다
- [ ] 원격 CI 첫 실행 (진행 중: 2026-08-03 푸시 완료. **결과 미확인** — 작업 환경에서 GitHub 에 접근할 수 없다. Linux 잡의 Native AOT 는 ubuntu 러너의 clang·zlib1g-dev 에 의존한다)
- [x] `Bench/` 골격 — `Bench/ChServerM.Bench` (BenchmarkDotNet 0.15.8). `BenchConfig`가 ServerGC·할당량 진단·첫 실패 시 중단을 고정한다. ENV-A 프로필 기록(Ryzen 9 9900X, 물리 12 / 논리 24)
- [x] 코드 커버리지 수집 (coverlet) + CI 리포트 — `eng/build.ps1 -Coverage`. **어셈블리별로 집계한다** (cobertura 파일명이 GUID 라 그대로 찍으면 관리에 쓸 수 없다). CI 가 cobertura 를 아티팩트로 올린다. 현재: Framing 95.0% / InMemory 83.4% / Concurrency 76.5% / Tcp 70.2% / Hosting 66.2% / Core 60.8%
  - [ ] 임계값 설정 — Core 추상화 확정 후. 지금은 수치를 보이게 만드는 단계다
  - [ ] ReportGenerator 도입 — 현재 집계는 어셈블리별 **최대값**이고 여러 테스트 프로젝트의 합집합이 아니다
- [x] ⚠ **public API 승인 파일 게이트** — `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0. `Server/Directory.Build.props`가 Server 어셈블리 6개에 적용한다(Tests/Bench/Samples 제외). 기준선 629줄. **켠 첫날 RS0026 으로 실제 API 결함을 잡았다** — `FrameWriter.WriteFrameAsync` 의 옵션 매개변수 기본값 세 개가 레거시 실패 패턴과 겹쳤고, 전부 필수로 바꿨다. 작업 절차는 CLAUDE.md 8.1
- [x] NuGet 취약점 감사 — `eng/build.ps1` audit 단계. **이 명령의 함정 둘을 모두 막았다**: (1) 취약점이 발견돼도 exit code 가 0 이라 naive 호출은 감사를 안 한 것과 같다, (2) 사람이 읽는 출력이 로케일에 따라 달라져 grep 이 CI 에서 깨진다 → `--format json` 파싱. **오프라인에서 빈 결과를 "안전함"으로 읽지 않도록** 원격 소스 존재까지 확인한다. 실제 취약 패키지(`System.Net.Http` 4.3.0)를 임시로 넣어 exit 1 을 확인했다
- [x] 의존성 업데이트 자동화 — `.github/dependabot.yml`. NuGet 주간 / GitHub Actions 월간. 테스트 도구와 분석기는 그룹으로 묶는다(버전이 어긋나면 "테스트가 발견되지 않는" 형태로 실패한다). **NuGet 메이저는 자동으로 받지 않는다** — ADR 이 필요한 결정이다
- [x] `LegacyServer/` + `LagacyClient/` 전수 정독 — 27,300줄 / 문서 14종. 결과: `docs/legacy/`(인덱스: [00-overview](legacy/00-overview.md))

**게이트**: CI가 build + test + 취약점 감사를 통과하고, public API 게이트가 켜져 있을 때.

> **2026-08-03 — 로컬에서 충족.** `eng/build.ps1` 6단계(restore·build·test·coverage·audit·aot)가
> Release + `-WarnAsError` 로 전부 통과하고 public API 게이트가 켜져 있다.
> **남은 것은 원격 확인 하나다** — 작업 환경에서 GitHub 에 접근할 수 없어 ubuntu 잡의
> 결과를 보지 못했다. Linux 에서 처음 도는 것이므로 Native AOT(clang·zlib1g-dev 의존)와
> 소켓 동작 차이가 실제로 통과하는지는 미확인이다.

## Phase 1 — Core 추상화

**가장 중요한 단계.** 여기서 그은 경계가 프레임워크의 확장성을 결정한다. 구현은 넣지 않는다.
Core에 들어간 인터페이스는 되돌리기 비용이 가장 크다 — 전부 ⚠로 취급한다.

### 기본 계약 (다른 모든 축이 이것에 의존한다)

- [x] ⚠ **에러 모델** — 범용 `Result<T>`를 쓰지 않는다. **연산별 상태 enum**(`FrameDecodeStatus`, `DispatchStatus`) + `TryXxx` + 목적 전용 결과 구조체(`FrameDecodeResult`). 공통 축은 `ErrorCode`(대역별 분류). 조립·설정 경로는 예외를 쓴다
- [x] ⚠ **생명주기·취소 계약** — `IConnection.ConnectionClosed` 를 단일 취소 원천으로(핸들러에 별도 토큰을 두지 않는다 — 원천이 둘이면 어느 쪽이 이겼는지 알 수 없다), `IAsyncDisposable`(graceful) ↔ `Abort`(abortive) 구분, `CloseReason`/`ConnectionCloseInfo`, 전송 3단 종료(Bind→Unbind→Stop)
- [x] ⚠ **ID 타입** — 6종 전부 `readonly struct` 강타입. `PartitionKey`(피보나치 해싱, 나눗셈 없는 인덱싱) 추가
  - ⚠ **`ObjectId`에 노드 성분을 반드시 포함한다.** 레거시 `GlobalM.MakeGameOid()`는 프로세스 전역 단조 카운터라 다중 노드에서 충돌하고 재시작 시 재사용된다 — **Phase 15의 선결 조건이며, 지금 `long` 증분으로 굳히면 되돌릴 수 없다.** Snowflake 계열 또는 노드별 블록 할당 ([06-session-user](legacy/06-session-user.md#globalm--compressandencryptmanm))
  - **`SessionHandle`은 세대(generation) 카운터를 포함**해 삭제된 세션 접근을 할당 없이 O(1)로 판별한다 (레거시 `UserM` 래퍼가 매 조회 힙 할당 + null 분기 버그) — **`ConnectionId`(slot+generation)에 이 패턴을 적용했다. 세션 쪽은 `ISessionStore`와 함께 남아 있다**
  - **존재하지 않는 세션의 기본값은 가장 제한적인 값**으로. 레거시는 `AllowedPkState` 기본값이 `A_SC_ANY_STATE`(전부 허용)였다 — 세션 계층 미착수. 같은 원칙을 프레이밍 쪽에 먼저 적용했다(`MessageId.None`=0 을 센티넬로 두고 핸들러 등록을 거부)
- [x] **시간 추상화** — `IClock`을 만들지 않고 BCL `TimeProvider`를 채택. `MonotonicTimestamp`(경과 측정 전용, 영속화 금지)로 벽시계와 타입 분리. `TimestampFrequency` 나눗셈이 public API로 새지 않는다. 음수 경과를 0으로 뭉개지 않는다 — 시계 역행은 감출 게 아니라 드러낼 신호다
- [x] **진단 계약** — `LogLevel`(레거시엔 레벨 개념 자체가 없었다), `EventId`(번호가 정본), `IServerLogger`(무할당, MEL 형태), `DiagnosticNames`/`MetricNames`/`TagNames`/`ActivityNames`

### 축 인터페이스

- [x] `IMessageSerializer<T>` + `IMessageSerializerProvider` — `IBufferWriter<byte>` 쓰기 / `ReadOnlySequence<byte>` 읽기. 실패는 `TryDeserialize`로, 예외 없음
- [x] ⚠ `IFrameDecoder` / `IFrameEncoder` — `ReadOnlySequence<byte>` 입력. **ADR-0002를 코드로 굳히는 지점.** 헤더는 고정 `struct`, 직렬화는 페이로드 전용. 이 경계가 프레이밍/직렬화 두 축의 독립 교체를 가능하게 한다
- [x] `IServerTransport` / `IClientTransport` / `IConnection` — 전송 중립 커넥션 추상화. 전송별 지식은 `IFeatureCollection`으로 뺀다(`IConnectionEndPointFeature`). 바이트 경로는 `PipeReader`/`PipeWriter`(**ADR-0006**)
- [x] `IMessageDispatcher` / `IMessageHandler<T>` — 디스패치 계약. `MessageContext`는 커넥션당 1개 재사용이고 `EndFrame()`이 페이로드 참조를 실제로 끊는다 — 레거시는 이 계약을 주석으로만 적고 `ToArray()`로 위반했다
- [x] `IServerMiddleware` + `MessageDelegate` — Chain of Responsibility. **`ValueTask<DispatchStatus>`를 반환한다** — 결과를 문맥의 가변 필드에 적는 방식이면 아무도 안 적고 지나가 거부된 메시지가 정상 처리로 집계된다
- [x] ⚠ **`IExecutionModel` — 유저별 순서 보장을 *표현할 수 있어야* 한다**. 계약이 이 전략을 강제하는 것이 아니라, 필요한 프로필이 선택할 수 있어야 한다는 뜻이다. `realtime-stateful`은 이 전략을 쓰고 `stateless-web`은 병렬 실행을 쓴다 — **하나의 계약이 양쪽을 수용해야 한다.** 근거: 레거시 `UserM.MemPkActionBlock`(TPL Dataflow, 유저 단위 직렬) vs `NetworkM.gMemPkActionBlock`(글로벌) ([01-network-transport](legacy/01-network-transport.md#sendpacketgroupm))
- [ ] `ISessionStore` / `ISession` — 상태 저장 추상화
- [ ] `IServerLogger` / `IMetricsSink` — 관측 추상화 (진행 중: `IServerLogger` 완료. `IMetricsSink` 미착수 — 이름 상수는 이미 있다)
- [ ] `IPayloadCodec` — 압축 계약
- [ ] `ITransportSecurity` — 전송 보안 계약
- [ ] `IAuthenticator` / `IAuthorizationPolicy` — 인증·인가 계약 (Phase 9에서 구현)
- [ ] `IRateLimiter` / `IAdmissionControl` — 과부하 제어 계약 (Phase 10에서 구현)
- [ ] `IClusterMembership` — 클러스터 계약 (Phase 15에서 구현)

### 마감

- [ ] 각 축의 `XxxOptions` 타입 + `IValidateOptions<T>` 검증 계약 (진행 중: `FramingOptions`·`TcpTransportOptions`·`InMemoryTransportOptions`·`PartitionedExecutionOptions`·`FramedConnectionOptions`가 `Validate()`로 시작 시점 검증. `IValidateOptions<T>`는 `Microsoft.Extensions.Options` 의존이라 Core가 아닌 Hosting 계층에 붙일지 미결)
- [x] Core 공개 표면 리뷰 — 44 타입 / 16 인터페이스. 축마다 하나씩이라 줄일 중복이 없음을 확인
- [ ] `docs/ARCHITECTURE.md`에 의존 방향·확장 지점 확정 기록

**게이트**: Core가 컴파일되고, 무의존 가드를 통과하고, 모든 축 인터페이스에 XML 문서가 있을 때.

## Phase 2 — 호스팅 & 조립 (Builder)

축을 실제로 "골라 끼우는" 표면. Phase 1 추상화가 진짜 조립 가능한지 검증하는 단계다.

- [ ] `ServerBuilder` 플루언트 API (진행 중: `.UseTransport()`·`.UseFraming()`·`.UseExecutionModel()`·`.ConfigureDispatcher()` 완료. 직렬화기는 현재 `Map<T>`에서 핸들러별로 등록한다 — 전역 `.UseSerializer()`로 올릴지 미결)
- [ ] DI 컨테이너 통합 (`Microsoft.Extensions.DependencyInjection`)
- [ ] **축 등록 편의 문법의 어셈블리 위치 결정** — `.UseTcp(port)` 같은 확장 메서드는 전송 어셈블리가 `Hosting`을 참조해야 성립하는데, 그것은 의존 방향(`Hosting → 어댑터 → Core`)을 뒤집는다. 지금은 `.UseTransport(new TcpServerTransport(...))`로 방향을 지키고 있다. 별도 `ChServerM.Hosting.Extensions` 계층을 둘지 결정 필요
- [x] 미들웨어 파이프라인 컴파일 — 델리게이트 체인. 라우팅은 배열 인덱싱(레거시는 프레임마다 선형 탐색 + 가상 호출 n번). **미들웨어가 라우팅보다 앞에 있다** — 반대면 모르는 ID를 보내는 것만으로 인증을 우회할 수 있다
- [x] 서버 생명주기 — 시작 / graceful shutdown / 커넥션 드레인 / 강제 종료 타임아웃. `ChServerMServer`가 종료 순서를 강제한다(전송 먼저, 실행 모델 나중 — 반대면 처리 중 커넥션의 연속이 갈 곳을 잃는다)
- [x] 옵션 검증 — 잘못된 축 조합을 **시작 시점에** 실패시킨다. `CompositionGuard`가 최대 프레임 ≤ 전송 버퍼 한계를 검사(**ADR-0007**). 이 조합이 어긋나면 큰 메시지 하나에서 예외도 로그도 없이 교착한다 — 실제로 발견된 결함이다
- [ ] 설정 소스 — `IConfiguration` 통합, 환경별 오버레이. 레거시 INI 방식은 폐기 — 1073줄 파서로 IP·포트 2개를 읽고 있었다 ([11-data-table](legacy/11-data-table.md#inifilem--inioptionm))
- [x] `ClientBuilder` 대칭 구성 — 서버와 같은 프레이밍·디스패치를 쓴다. 재접속 정책은 넣지 않는다(감추면 상위가 세션 재수립을 건너뛴다)
- [x] ⚠ **인메모리 루프백 전송** — `ChServerM.Transport.InMemory`. 소켓 없이 파이프라인을 끝까지 도는 전송. **전송 축의 두 번째 구현체 역할**을 싸게 수행해 추상화가 진짜 전송 중립인지 조기에 증명한다. 통합 테스트의 기본 전송이 되어 테스트 속도도 올라간다 (Kestrel의 인메모리 전송과 같은 발상)
- [x] 조립 테스트 — `CrossTransportTests` 14항목 × 2전송. 핸들러·프레이밍·디스패치 코드가 두 경우에 완전히 동일하다
- [x] 첫 실행 가능 프로젝트(`Samples/ChServerM.Samples.EchoServer`) — CI의 **AOT 컴파일 검증이 활성화됐다**. 같은 핸들러를 TCP·인메모리 양쪽에서 1000회 왕복시키고 exit code로 보고한다

**게이트**: 같은 핸들러 코드가 인메모리 전송과 (Phase 5 이후) TCP 전송 양쪽에서 동작하고, AOT publish가 성공할 때.
전송 축이 두 구현으로 증명되기 전까지 `IServerTransport`는 가설로 취급한다(ADR-0000).

---

# Part II — 데이터 경로 (Data Path)

핫패스. 여기서의 모든 결정은 벤치마크 수치로 방어해야 한다.

## Phase 3 — 메모리 & 버퍼

> **레거시에서 승계할 구현이 없다.** `MemoryPoolM`·`StackMemAllocM`·`UnsafeCopyBlock`은 전부 **참조 0 또는 전체 주석**이고, 실사용 풀은 `ObjectPoolM<T>`(32줄, 상한·중복반납 검사 없음) 하나뿐이다. 실제 풀링은 `ArrayPool<byte>.Shared` 직접 호출이며 그것이 반납 누수의 근원이다 ([12-domain-util-discarded](legacy/12-domain-util-discarded.md#-이전-판정-정정--버퍼-풀링은-승계할-구현이-없다)). **처음부터 설계한다.**

- [ ] `ChServerM.Buffers` — 슬랩 할당기, 커넥션당 버퍼 대여
- [ ] ⚠ `ArrayPool` / `MemoryPool` 래핑 정책 결정 (ADR) — 대여 단위, 반납 책임 소유자, 초과 크기 처리
- [ ] `IBufferWriter<byte>` 기반 쓰기 경로 — 중간 배열 없이 소켓까지
- [ ] **풀 누수 감지 진단** — DEBUG 빌드에서 미반납 대여를 추적. 레거시 `IoPipelineSrvM.cs`가 `try/finally` 밖에서 `Return`을 호출해 예외 경로에서 누수됐다. 같은 실수를 구조적으로 막는다
- [ ] 대여 소유권 규약 문서화 — "누가 반납하는가"를 타입으로 표현 (`ref struct` 스코프 또는 `IMemoryOwner<T>`)
- [ ] 벤치마크: 대여/반납 처리량, GC Gen0/1/2 수집 횟수, 커넥션당 메모리

**게이트**: 대여-반납 왕복이 힙 할당 0이고, 누수 감지가 의도적 누수를 잡을 때.

## Phase 4 — 프레이밍

ADR-0002로 프레이밍은 직렬화와 분리된 독립 축이 됐다. 별도 Phase로 다룬다.

- [x] ⚠ **와이어 헤더 레이아웃 확정** — 16B 고정, 리틀 엔디안. **`MemoryMarshal`을 쓰지 않고 `BinaryPrimitives`만** 쓴다(정렬·패딩·호스트 엔디안에 와이어 포맷이 끌려가지 않는다). 버전 필드 포함. 레거시 52B(실데이터 13B) → 16B
- [ ] length-prefix 디코더 — varint / fixed32 두 가지 (진행 중: fixed32 완료(`FixedHeaderFrameDecoder`). **varint 미착수** — 두 번째 구현이 나오기 전까지 `IFrameDecoder`는 가설로 취급한다)
- [x] 부분 프레임 처리 — 헤더가 세그먼트를 넘을 때만 16B `stackalloc`(힙 할당 0). 세그먼트 크기 1~64 및 헤더 16B의 모든 분할 지점을 테스트
- [x] **프레임 오류 처리 정책** — 길이 이상·버전 불일치·미정의 플래그·예약 필드 비영 시 커넥션 종료. 재동기화를 시도하지 않는다(다음 경계를 알 수 없다). 체크섬 필드는 두지 않는다 — 레거시 검증 함수는 본문이 `return true`였고 무결성은 Phase 9 AEAD가 담당한다. 레거시는 예외를 잡고 루프를 계속해 상태 머신이 어긋난 채 파싱을 이어갔다(프레이밍 desync). `TryXxx`로 처리하고 오류 프레임은 커넥션을 닫는다
- [x] 최대 프레임 크기 상한 — `MaxPayloadLength` + 절대 상한 64MiB. 길이 필드를 `uint`인 채로 비교한다(`int` 캐스팅 후 비교하면 2GB 이상이 음수가 된다). **버퍼를 잡기 전에** 판정
- [x] 프레임 조립 상태 머신 — **상태 머신이 필요 없는 설계로 해소했다.** 부분 프레임 상태는 `PipeReader` 버퍼가 이미 들고 있으므로 디코더는 무상태이고, 인스턴스 하나를 모든 커넥션이 공유한다. 레거시가 커넥션마다 5단 상태를 들고 있다가 예외 한 번에 desync 된 원인이 구조적으로 사라진다
- [ ] **조각 재조립(`Fragmented`/`EndOfMessage`)** — 임계값보다 큰 논리 메시지를 나눠 보내는 경로. **재조립 버퍼에 상한과 미완성 조각 만료가 반드시 필요하다** — 마지막 조각이 오지 않는 부분 메시지를 무한정 들고 있으면 그 자체가 메모리 고갈 공격 경로다 (ADR-0007 미해결 항목)
- [x] 퍼징 테스트 — 난수 12만 회 + 비트 플립 2만 회 + 잘린 프레임 전 오프셋 + 길이 필드 극단값. 불변식 4종(예외 없음 / 버퍼 밖 미참조 / **반드시 전진** / `NeedMoreData`는 버퍼 전체 검사). 시드 고정으로 재현 가능
- [x] 벤치마크: 프레임당 파싱 비용, 할당 0 확인 — 디코딩 **약 29 ns**, 할당 0. 초당 10만 프레임이면 코어 하나의 0.3%로, ADR-0002 의 "헤더 파싱 비용 0" 주장이 성립한다. 세그먼트 경계 경로는 14~19% 느리지만 절대값 4 ns 라 최적화할 이유가 없음을 확인

**게이트**: 퍼징이 크래시 없이 통과하고 프레임당 할당이 0일 때.

## Phase 5 — TCP 전송 (첫 실동 구현)

- [ ] ⚠ Kestrel Socket Transport 재사용 vs 순수 `Socket`+Pipelines — 양쪽 프로토타입 벤치마크 후 **ADR-0001 확정**
- [x] `ChServerM.Transport.Tcp` — accept 루프 + 수신/송신 펌프. `TcpClient`/`NetworkStream`을 쓰지 않는다. 수락 루프가 일시적 오류(개별 연결 끊김)와 치명적 오류(수락 소켓 사망)를 구분한다 — 구분하지 않으면 조용히 수용을 멈추거나 CPU를 태운다
- [x] 백프레셔 — pause/resume 임계값을 옵션으로 노출. **최대 프레임보다 커야 한다는 제약을 조립 시점에 검사한다**(ADR-0007). `WaitForDataBeforeAllocating`(0바이트 수신)으로 유휴 커넥션이 버퍼를 붙들지 않게 — 1만 유휴 접속 × 4KB = 40MB 절감. 레거시는 커넥션당 64KB 상수(= 640MB)
- [ ] 커넥션 생명주기 — idle timeout, half-open 감지(keepalive), graceful close, RST 처리 (진행 중: graceful close(2단 종료 — 드레인→FIN→상대 FIN 대기, 상한 있음)·RST 처리(`IsExpectedDisconnect`로 정상 종료 예외를 걸러 로그 소음 방지)·이식 가능한 keep-alive 옵션 완료. **idle timeout 미착수** — 타이밍 휠과 함께)
- [ ] **종료 레이스 처리** — 로그인 완료 전 연결이 끊기는 경우. 레거시는 1초 지연 타이머로 대응했다(실전에서 나온 장치). 동등한 보장을 구조적으로 제공
- [ ] 송신 배칭 — 작은 패킷 다수를 묶어 syscall 수를 줄인다. 레거시 `SendPacketGroupM` 참고
- [x] Nagle / `TCP_NODELAY` 정책 — `NoDelay` 기본 켬(Nagle 비활성). 근거를 옵션 문서에 기록
- [ ] 소켓 옵션 노출 — 버퍼 크기, linger, reuseaddr (진행 중: `NoDelay`·`EnableKeepAlive`만. **`IOControlCode.KeepAliveValues`를 쓰지 않는다** — Windows 전용이라 리눅스에서 던진다. 레거시의 크로스 플랫폼 차단 요인이었다)
- [ ] 통합 테스트: 연결/에코/대량 동시 접속/비정상 종료 (진행 중: 연결·에코·200KB 다중 세그먼트·200프레임 파이프라이닝·16커넥션 동시·비정상 종료 격리·포트 점유 완료. **1만 동시 접속 미검증**)
- [ ] 크로스 플랫폼 검증 — Linux/Windows 소켓 동작 차이 (미푸시라 원격 CI가 아직 돌지 않았다. 이식 불가 API는 코드에서 이미 배제)
- [ ] 벤치마크: 에코 RPS, p50/p99/p999 레이턴시, 커넥션당 메모리, 동시 커넥션 상한

**게이트**: 1만 동시 커넥션에서 안정 동작하고 p99 레이턴시 기준선이 기록됐을 때.

## Phase 6 — 직렬화 어댑터

축 교체가 실제로 동작함을 증명하는 단계. **최소 2개 구현이 필수.**

- [ ] `ChServerM.Serialization.MemoryPack`
- [ ] `ChServerM.Serialization.Protobuf`
- [ ] `ChServerM.Serialization.FlatBuffers` (FlatSharp)
- [ ] ⚠ 4자 벤치마크 → `docs/BENCHMARKS.md` + **ADR-0002 남은 부분(페이로드 기본값) 확정**. 레거시가 FlatBuffers 스키마·생성 코드를 운영 중이므로 승계 비용이 변수
- [ ] 스키마 진화 테스트 — 필드 추가/삭제 시 구버전 클라이언트 호환성
- [ ] 동일 샘플이 어댑터 교체만으로 동작하는지 검증
- [ ] 크로스 언어 상호운용 확인 — 클라이언트가 C#이 아닐 가능성에 대한 결론

**게이트**: 두 개 이상의 어댑터가 같은 샘플에서 동작하고 기본값 ADR이 확정됐을 때.

## Phase 7 — 디스패치 & 소스 제너레이터

- [ ] `ChServerM.SourceGen` — 메시지 ID → 핸들러 스위치 테이블 생성
- [ ] 컴파일 타임 검증 — 중복 메시지 ID, 누락 핸들러, 시그니처 불일치를 **빌드 실패로**
- [ ] 진단 규칙 ID 체계 (`CHSM1xxx`) + 각 진단에 대한 문서
- [ ] 제너레이터 스냅샷 테스트 — 생성 코드가 의도치 않게 바뀌면 실패
- [ ] 증분 생성(`IIncrementalGenerator`) — 대규모 프로젝트에서 IDE가 멈추지 않도록
- [ ] 리플렉션 기반 폴백 디스패처 (개발 편의용, 프로덕션 비권장. AOT에서 비활성)
- [ ] 벤치마크: 디스패치 오버헤드 (생성 코드 vs 리플렉션 vs 딕셔너리)

**게이트**: 생성 코드 경로가 AOT에서 동작하고 딕셔너리 방식보다 빠름이 측정됐을 때.

## Phase 8 — 동시성 실행 모델

> **`CLAUDE.md` 9절(병렬성 규약)이 이 Phase의 구현 기준이다.**
> 특히 9.1(공유 대신 파티셔닝), 9.2(`finally`로 상태 복원), 9.5(스레드 수 통제),
> 9.6(유계 큐 + 포화 관측)은 레거시에서 실제로 서버를 멈추거나 데이터를 유실시킨 항목이다.


- [x] `ChServerM.Concurrency` — 파티션당 전용 스레드 + 단일 FIFO 채널. **큐를 하나로 둔 이유**: 스케줄러 연속과 외부 게시가 같은 FIFO를 공유해야 둘 사이의 순서도 보장된다. **유계 규약(9.6)은 유입 지점에만 적용한다** — 스케줄된 `Task`를 거부하면 그것을 `await`하던 코드가 영원히 깨어나지 못한다
- [ ] ⚠ **유저별 순서 보장 구현** — `IExecutionModel` 계약의 실체 (진행 중: **커넥션 단위**로 완료. `PartitionedConnectionHandler`가 읽기 루프를 파티션에 고정해 프레임당 큐 비용이 0이다. 유저/세션 단위 고정은 세션 계층과 함께 — 지금은 재접속하면 다른 파티션으로 간다)
- [ ] 스레드-퍼-코어 모델 + CPU 어피니티 (진행 중: 파티션당 전용 스레드 = 기본 `ProcessorCount`개, 절대 상한 512. **CPU 어피니티 미적용**)
- [x] false sharing 회피 — 파티션 대기 카운터를 `[StructLayout(Size=128)]`로 패딩. 64B가 아니라 128B인 이유는 일부 x86이 인접 라인을 함께 프리페치하기 때문
- [ ] **작업 상자 풀을 파티션별로 분리 검토** — `TryPost` 게시당 할당이 파티션 1개에서 1 B 인데 8개에서 **70 B** 다. `WorkBoxPool<TWork>` 가 파티션 간 공유이고 상한이 1,024 인데 in-flight 가 그보다 크면 새로 할당하고 넘치면 버리는 churn 이 생긴다. 코드 주석에 "경합이 문제가 되면 파티션별 풀로 바꾼다 — 그때는 측정 결과를 근거로 남긴다"라고 적어둔 그 시점이다
- [ ] **작업 상자 풀을 파티션별로 분리 검토** — `TryPost` 게시당 할당이 파티션 1개에서 1 B 인데 8개에서 **70 B** 다. `WorkBoxPool<TWork>` 가 파티션 간 공유이고 상한이 1,024 인데 in-flight 가 그보다 크면 새로 할당하고 넘치면 버리는 churn 이 생긴다. 코드 주석에 "경합이 문제가 되면 파티션별 풀로 바꾼다 — 그때는 측정 결과를 근거로 남긴다"라고 적어둔 그 시점이다
- [ ] 스케줄러 공정성 — 한 유저가 워커를 독점하지 못하게
- [x] 데드락·경합 테스트 — 8 생산자 × 2000 작업이 정확히 1회 실행되는지 + Release 반복 실행. **실제로 두 건을 잡았다** — `Abort`가 송신 펌프를 깨우지 않아 생긴 교착, 최대 프레임 > 전송 버퍼 교착(ADR-0007)
- [x] ⚠ **예외 안전성 테스트** — 항목별 `try/catch` + `finally`로 큐 슬롯 복원. 예외 작업 50개 뒤에도 정상 작업이 처리되고, 용량의 10배를 예외 작업으로 밀어 넣어도 슬롯이 새지 않음을 확인
- [ ] 액터 모델 어댑터 검토 (Orleans / Proto.Actor) — Core에 침투 금지
- [x] ⚠ **벤치마크: 코어 수 대비 확장성 곡선** — 파티션 1·2·4·8·12·24 스윕. **물리 코어 구간 효율 95.0% 이상**(12파티션 11.40배). ADR-0005 의 검증 조건 충족.
  - [ ] 실제 코어 제한 재측정 — 위 곡선은 파티션 수 스윕이고 OS 는 모든 코어를 쓸 수 있다. `Process.ProcessorAffinity` 가 리눅스 미지원이라 `taskset`/`start /affinity` 로 감싸야 한다
  - [ ] NUMA 다중 소켓 머신에서 재확인 — ENV-A 는 단일 소켓이다
- [x] 유저별 순서 보장 오버헤드 측정 — **3.9%**. 파티션 모델(26.95ms)이 무순서 스레드풀 병렬의 상한(25.93ms)에 그만큼 차이로 근접한다. 전역 락은 직렬보다 느렸다(353.57 vs 351.20ms) — ADR-0005 의 탈락 근거가 수치로 확인됐다
- [ ] **경합 측정** — 원자 연산 경합, false sharing 유무를 프로파일로 확인 (9.3·9.4)

**게이트**: 코어 수 대비 처리량이 선형에 근접하고, 순서 보장이 부하 상태에서도 깨지지 않음이 검증됐을 때.

---

# Part III — 프로덕션 필수 (Production Essentials)

**이 Part를 건너뛰고 Part IV로 가지 않는다.** 상업용 서버에서 여기가 비면 나머지가 무의미하다.

## Phase 9 — 보안

- [ ] ⚠ **위협 모델 문서화** — `docs/THREAT-MODEL.md`. 신뢰 경계, 공격 표면, 각 위협에 대한 완화책. 이것 없이 개별 대책을 만들면 구멍이 남는다.
  **출발점**: [07-security](legacy/07-security.md#새-코드에-절대-옮기면-안-되는-것)의 결함 목록을 위협 → 완화책으로 매핑한다 (미인증 키 교환, XOR '암호화', AES-128 + 고정 IV, 인증 없는 CBC, PKCS#1 v1.5, 커넥션당 RSA 생성, 와이어 값 기반 할당, 최대 프레임 크기 부재)
- [ ] `ChServerM.Security.Tls` — `SslStream` 기반 전송 보안. 인증서 로딩·검증·회전
- [ ] ⚠ **핸드셰이크·키 교환 설계** — 레거시는 `FbsEncryptKey`(key/iv)로 교환하고 서버→클라 XOR, 클라→서버 AES256을 썼다. **XOR은 암호화가 아니다.** 양방향 AEAD(AES-GCM / ChaCha20-Poly1305)로 재설계한다
- [ ] `IPayloadCodec` 구현 — 압축(LZ4/Zstd). 레거시 정책(1024B 미만 무압축) 참고. **압축 후 암호화 순서 고정** (역순은 CRIME류 취약점)
- [ ] 리플레이 방지 — 패킷 시퀀스/nonce 검증. 레거시 `pid`(패킷 아이디) 개념 승계
- [ ] 무결성 검증 — AEAD 태그로 대체 (레거시의 단순 체크섬은 공격자에게 무의미)
- [ ] **상태별 패킷 화이트리스트** — 인증 전에 인증 후 패킷을 받지 않는다. 레거시 `AllowedPacketM` 승계
- [ ] `IAuthenticator` 구현 — 토큰 검증. 레거시 `BasicLibM/AuthM` 판정 필요
- [ ] 인가 미들웨어 — 메시지별 권한 검사
- [ ] 시크릿 관리 — 설정 파일에 키를 두지 않는다. 환경변수/시크릿 저장소
- [ ] 입력 검증 — 모든 페이로드 필드 범위 검사. 퍼징 확대
- [ ] `/security-review` 실행 + 결과 반영

**게이트**: 위협 모델의 모든 항목에 완화책이 매핑되고, 인증 전 패킷이 차단됨이 테스트로 확인될 때.

## Phase 10 — 복원력 & 과부하 제어

- [ ] `IRateLimiter` 구현 — IP별 / 세션별 / 메시지 타입별. `System.Threading.RateLimiting` 활용
- [ ] `IAdmissionControl` — 과부하 시 신규 연결 거부. **거부가 붕괴보다 낫다**
- [ ] 리소스 상한 — 최대 커넥션 수, 커넥션당 메모리 상한, 전체 메모리 워터마크
- [ ] 연결 폭주 방어 — accept 큐 관리, SYN 폭주 대응, 핸드셰이크 타임아웃
- [ ] 서킷 브레이커 / 재시도 미들웨어 — 외부 의존(DB/Redis) 장애 격리
- [ ] Bulkhead — 한 기능의 장애가 전체를 마비시키지 않게 격리
- [ ] 우아한 열화(graceful degradation) — 부하 시 비필수 기능 차단 순서 정의
- [ ] 크래시 처리 — 미처리 예외 정책, 덤프 수집, 재시작 전략
- [ ] 장애 주입 테스트 — 지연·패킷 손실·의존성 장애를 주입해 동작 확인
- [ ] 24시간 soak 테스트 — 메모리 누수·핸들 누수·성능 열화 확인. **단발 벤치마크로는 안 잡힌다**

**게이트**: 과부하에서 거부하며 살아남고, 24시간 soak에서 메모리가 평탄할 때.

## Phase 11 — 관측 & 진단

> **설계 목표: "실패가 관측되는가".**
> 레거시에서 체크섬 검증·LZ4 압축·MongoDB 재시도·`HashM` 만료·백프레셔·콜라이더
> 비활성화가 **전부 무동작이었는데 아무도 몰랐다.** 원인은 로그 레벨 부재, 설정 파일이
> 없으면 로깅이 통째로 사라지는 구조, `Debug.WriteLine`의 Release 소멸, 그리고
> **메트릭 전무**다 ([09-observability](legacy/09-observability.md#phase-11-설계에-반영할-것)).
> → **조용한 실패가 가능한 지점마다 카운터를 두고 0이 아니면 경보한다.**
> 드롭된 패킷, 미반납 풀 대여, 실패한 재시도, 만료되지 않은 잡, 포화된 큐.


- [ ] `ChServerM.Observability` — OpenTelemetry 트레이스·메트릭
- [ ] ZLogger 어댑터 (무할당 구조적 로깅)
- [ ] 핵심 메트릭 정의 — 커넥션 수, RPS, 레이턴시 히스토그램, 큐 깊이, 풀 사용률, 오류율
- [ ] 분산 트레이싱 — 메시지 흐름 상관관계(correlation ID) 전파
- [ ] 헬스체크 / 라이브 진단 엔드포인트 — liveness / readiness 구분
- [ ] 런타임 진단 — 커넥션 덤프, 스레드 상태, 풀 상태를 운영 중에 조회
- [ ] `EventSource` / `DiagnosticSource` — `dotnet-counters`/`dotnet-trace` 연동
- [ ] 로그 레벨 런타임 변경 — 재시작 없이 디버그 로그 활성화
- [ ] ⚠ **관측 오버헤드 측정** — 메트릭·트레이싱 데코레이터가 핫패스에 미치는 비용. 켠 상태와 끈 상태를 모두 벤치마크. 관측이 성능을 먹으면 프로덕션에서 꺼지고, 꺼진 관측은 없는 것과 같다

**게이트**: 관측을 켠 상태의 오버헤드가 측정·기록되고 허용 범위 안일 때.

## Phase 12 — 성능 검증 & 회귀 방어

지금까지의 벤치마크를 **회귀 방어 장치로** 승격시킨다. 측정만 하고 지키지 않으면 성능은 반드시 퇴화한다.

- [ ] 성능 목표 확정 — `docs/BENCHMARKS.md`의 가설 표를 실측 기준선으로 대체
- [ ] ⚠ **CI 벤치마크 회귀 게이트** — 기준선 대비 N% 이상 퇴화 시 빌드 실패. 노이즈 처리 전략(반복 실행, 중위값) 포함
- [ ] 할당 회귀 게이트 — 핫패스 메서드의 할당량 0을 테스트로 고정
- [ ] ⚠ **확장성 회귀 게이트** — 코어 수 대비 처리량 곡선을 기준선으로 고정하고, **선형성이 떨어지면 빌드 실패.** 병렬성 퇴화는 단일 스레드 성능 회귀보다 발견이 늦다 (`CLAUDE.md` 9.9)
- [ ] 종단 부하 테스트 (NBomber) — 현실적 시나리오, 램프업/스파이크/지속
- [ ] 프로파일링 워크플로 문서화 — CPU/할당 프로파일을 어떻게 뜨고 읽는지
- [ ] GC 튜닝 검증 — ServerGC / DATAS / region 설정별 비교
- [ ] Native AOT vs JIT 성능·기동시간 비교
- [ ] 경쟁 프레임워크 비교 측정 — 최소 하나 (SuperSocket / DotNetty / raw Kestrel)

**게이트**: 회귀 게이트가 의도적 성능 퇴화를 실제로 잡을 때.

---

# Part IV — 상태 & 확장

## Phase 13 — 세션 & 영속화

- [ ] `ChServerM.Persistence.InMemory` — 기본 구현
- [ ] `ChServerM.Persistence.Redis` (StackExchange.Redis)
- [ ] 로컬 KV 검토 (Tsavorite / Garnet)
- [ ] MongoDB 어댑터 검토 — 레거시 `DBManager/MongoDBManagerM.cs` 판정 필요
- [ ] ⚠ 세션 복구 / 재접속 — 끊긴 클라이언트가 상태를 잃지 않고 돌아오는 경로. `realtime-stateful` 프로필 필수
- [ ] 일관성 모델 명시 — 무엇이 강한 일관성이고 무엇이 최종 일관성인가
- [ ] 캐시 무효화 전략
- [ ] 커넥션 풀 관리 / 외부 저장소 장애 시 동작
- [ ] 벤치마크: 세션 조회·갱신 레이턴시

## Phase 14 — 데이터 테이블 & 설정 (선택 축)

정적 데이터 테이블을 로드해 서비스하는 서버는 흔하다 — 게임 밸런스 테이블, 요금표,
룰 엔진 설정, 피처 플래그. `ChServerM.DataTable.*`로 분리한다. 레거시가 상당한 자산을 갖고 있다.

- [ ] 정적 데이터 테이블 로딩 — 레거시 `Table/SrvTableM.cs`, `AbSrvTableM.cs`, `PublicLib/FileM/MetaDataM.cs`, `LoadableDataInStructM.cs` 판정 필요
- [ ] CSV/Excel 임포트 — **빌드 타임 변환 도구로 확정한다.** 런타임 어셈블리에 Excel 파서를 넣지 않는다. 레거시 `ExcelLibM`(2166) + `ExcelODBCM`(927) + `CsvParser`(182)는 **전부 참조 0**이며 `ExcelODBCM`은 Windows 전용 ODBC다 ([11-data-table](legacy/11-data-table.md#-미사용-코드-3359줄))
- [ ] 테이블 검증 — 참조 무결성, 범위 검사를 로딩 시점에
- [ ] ⚠ 핫 리로드 — 무중단 데이터 갱신. **승계할 구현이 없다** (`FileWatcherSystemM.cs`는 참조 0). 읽는 중 교체 시 일관성 보장이 어려운 지점이므로 처음부터 설계한다
- [ ] 클라이언트-서버 테이블 버전 검증 — 불일치 시 접속 거부

## Phase 15 — 클러스터 & 분산

- [ ] `IClusterMembership` — 정적 목록 구현
- [ ] 서비스 디스커버리 어댑터 (Consul / etcd / K8s)
- [ ] ⚠ 파티셔닝 / 라우팅 전략 — 상태 유지 노드에 어떤 키로 라우팅하는가
- [ ] 노드 간 통신 — 내부 전송 경로
- [ ] 리밸런싱 — 노드 추가/제거 시 상태 이동
- [ ] 분산 락 / 리더 선출 (필요한 경우)
- [ ] 스플릿 브레인 대응
- [ ] 무중단 배포 — 롤링 업데이트 중 커넥션 드레인
- [ ] 통합 테스트: 다중 노드 시나리오

## Phase 16 — 대체 전송

`stateless-web` 참조 프로필이 완성되는 지점. **여기서 "두 프로필이 같은 핸들러로 동작"이
프로덕션 수준으로 증명된다** (Phase 2의 인메모리 전송이 그 예비 증명이었다).

- [ ] `ChServerM.Transport.Http` — Kestrel 기반, 동일 파이프라인 재사용
- [ ] 무상태 모드 — 세션을 `ISessionStore`로 외부화
- [ ] **`stateless-web` 프로필 완성** — `realtime-stateful`과 동일한 핸들러 코드로 동작함을 통합 테스트로 고정
- [ ] `ChServerM.Transport.WebSocket`
- [ ] ⚠ `ChServerM.Transport.Udp` — 신뢰 UDP(순서·재전송·단편화). 실시간 게임에서 TCP head-of-line blocking 회피용. 자체 구현 vs LiteNetLib/ENet 어댑터 판단 필요
- [ ] QUIC / HTTP/3 (`System.Net.Quic`) 검토
- [ ] 전송 축 교체 테스트 — 같은 핸들러가 TCP/HTTP/WS에서 동작

---

# Part V — 실시간 프리미티브 (선택 축)

**전부 빼도 프레임워크가 성립해야 한다** (ADR-0004). Core는 이 Part의 존재를 알지 않는다.
`ChServerM.RealTime.*` 별도 패키지로 격리해, 쓰지 않는 사용자가 의존을 끌고 오지 않게 한다.

실시간 상시 연결 워크로드에서 반복적으로 필요한 것들을 프리미티브로 제공한다.
게임에만 쓰이는 것은 아니다 — 협업 편집, 실시간 대시보드, IoT 텔레메트리도 같은 프리미티브를 쓴다.
도메인 로직(레이팅 공식, 충돌 판정 등)은 프레임워크가 아니라 `Samples/`에 둔다.

## Phase 17 — 틱 & 시간 동기화

- [ ] `ChServerM.RealTime` — 고정 타임스텝 틱 루프. 드리프트 보정
- [ ] 틱 예산 초과 감지 — 한 틱이 예산을 넘으면 관측에 노출
- [ ] 서버 시간 동기화 — 레거시 `FbsServerTick`, `FbsLoginOk.serverFrequency` 개념 승계
- [ ] 지연 측정 / RTT 추정 — 레거시 `NetWorkDelayM.cs` 판정 필요
- [ ] 타이머 시스템 — 레거시 `Scheduler/TimeEventSchedulerM.cs`, `ExpireEventConCurSchedulerM.cs` 판정 필요
- [ ] 벤치마크: 틱 지터, 틱당 처리 용량

## Phase 18 — 룸/존 & 관심 영역

> ⚠ **충돌 판정은 단위 테스트를 먼저 쓴다.** 레거시 충돌 계층에는 미수정 버그 8건이
> 있고 — 회전 미적용, 위치 영구 고정, 축정렬 quad 충돌 항상 false, 접촉점 무의미 —
> **실제로 검증된 적이 없다고 보아야 한다** ([03-ecs-object-model](legacy/03-ecs-object-model.md#새-코드에-절대-옮기면-안-되는-것--미수정-버그)).
> 승계하는 것은 알고리즘 구조(SAT, 집합 차분, Stay 스로틀, 모튼 코드)이지 코드가 아니다.


- [ ] 룸/채널 추상화 — 생성·참가·퇴장·해산 생명주기
- [ ] 브로드캐스트 최적화 — 같은 페이로드를 N명에게 보낼 때 직렬화 1회
- [ ] ⚠ 관심 영역(AOI) — **승계할 구현이 없다.** `QuadTreeM.cs`는 빈 파일이고 `QuadGrid`/`LQuadTree` 타입은 코드베이스에 존재하지 않는다. 유일한 생존 자산은 `MortonCodeM`(Z-order curve)이며 이것을 출발점으로 삼는다 ([03-ecs-object-model](legacy/03-ecs-object-model.md#공간-분할quadgrid은-구현되어-있지-않다)). 공간이 없는 워크로드는 이 축을 쓰지 않는다
- [ ] 충돌·공간 질의 — 레거시 `BoxColliderM.cs`, `MathM.cs`, `HierachyM.cs` 판정 필요
- [ ] 스냅샷 / 델타 압축 — 변경분만 전송
- [ ] 벤치마크: 룸 인원 대비 브로드캐스트 비용

## Phase 19 — 매치메이킹 & 레이팅 (선택 축)

대기열에서 조건에 맞는 참가자를 묶는 문제는 게임 밖에서도 나타난다 —
대전 매칭, 배차, 상담 배정. 레이팅 공식 자체는 도메인이므로 `Samples/`에 둘 수도 있다.

- [ ] 레이팅 시스템 — 레거시 `GlickoM.cs`(301) / `WengLinM.cs`(626)는 **참조 0인 준비 코드**다. 알고리즘 구현은 참고하되 **프레임워크가 아니라 `Samples/`에 둔다** (ADR-0004: 도메인 로직은 프레임워크가 아니다)
- [ ] 매치메이킹 큐 — 대기 시간 vs 매칭 품질 트레이드오프
- [ ] 파티/그룹 매칭
- [ ] 매치 결과 반영 / 레이팅 갱신

---

# Part VI — 제품화

프레임워크는 **개발자 경험이 제품**이다. 여기가 비면 아무도 쓰지 않는다.

## Phase 20 — 개발자 경험

- [ ] `dotnet new` 템플릿 — `chserverm-server`, `chserverm-client`
- [ ] 시작 가이드 — 5분 안에 에코 서버가 도는 문서
- [ ] ⚠ **진단 분석기** — 사용자의 흔한 실수를 컴파일 타임에 잡는 Roslyn 분석기. 핫패스에서 `async void`, 풀 버퍼 미반납, 핸들러 미등록 등. 프레임워크 품질의 체감 차이가 여기서 난다
- [ ] 축 조합별 샘플 정리 (`Samples/`) — TCP+MemoryPack, HTTP+Protobuf, 게임 룸 예제
- [ ] 디버깅 지원 — `DebuggerDisplay`, `DebuggerTypeProxy`, 의미 있는 예외 메시지
- [ ] 에러 메시지 품질 검토 — 무엇이 잘못됐고 어떻게 고치는지 알려주는가
- [ ] API 문서 사이트 (XML doc → DocFX 등)
- [ ] 아키텍처 가이드 — 축을 어떻게 고르는가, 언제 무엇을 쓰는가
- [ ] 성능 튜닝 가이드 — 측정 근거와 함께
- [ ] 마이그레이션 가이드 — 레거시 서버에서 옮겨오는 경로

## Phase 21 — API 안정성 & 릴리스 엔지니어링

- [ ] ⚠ **SemVer 정책 문서화** — 무엇이 breaking change인가. 축 인터페이스 변경 규칙
- [ ] API 호환성 검사 CI — 이전 버전 대비 breaking change 자동 검출
- [ ] `PublicAPI.Shipped.txt` 확정 — 1.0 공개 표면 동결
- [ ] NuGet 패키징 — 축별 개별 패키지. 메타 패키지 제공
- [ ] SourceLink + 심볼 서버 — 사용자가 프레임워크 내부를 디버깅할 수 있게
- [ ] 결정적 빌드 검증 — 같은 커밋이 같은 바이너리를 내는가
- [ ] 패키지 서명 / 출처 증명(provenance)
- [ ] 릴리스 노트 자동화 — Conventional Commits 기반
- [ ] 지원 정책 — 지원 버전, 보안 패치 기간
- [ ] 라이선스 확정 + 서드파티 라이선스 감사

## Phase 22 — 1.0 출시

- [ ] Native AOT 샘플 전체 검증
- [ ] 컨테이너 이미지 + 배포 예제 (K8s 매니페스트)
- [ ] 전 Phase 게이트 재확인
- [ ] 최종 보안 검토
- [ ] 최종 성능 기준선 공표
- [ ] 문서 전체 검토 — 죽은 링크, 낡은 예제
- [ ] 1.0 태그 + 릴리스

---

# 횡단 관심사 (상시 유지)

Phase에 속하지 않지만 계속 지켜야 하는 것.

- **ADR 규율** — 라이브러리·아키텍처 선택 시 `docs/DECISIONS.md`에 대안·탈락 이유 기록
- **벤치마크 규율** — 성능 주장은 항상 수치. `perf(...)` 커밋은 before/after 필수
- **Core 무의존** — `CHSM0001` + `CoreDependencyTests`가 자동 강제
- **레거시 참조** — 승계 대상 구현 전에 `docs/legacy/`의 해당 문서를 읽는다. 각 문서의 "새 코드에 절대 옮기면 안 되는 것" 목록이 회귀 방지 체크리스트다
- **스탠드업** — 세션 시작 `/standup`, 종료 `/standup wrap`
- **코드 작성 전 승인** — 대상 파일·타입·시그니처·근거를 먼저 제시

---

# 백로그 (단계 미배정)

- 컴파일 타임 DI (Pure.DI / Jab) — AOT 경로 최적화용
- io_uring 기반 Linux 전송 검토
- NUMA 인식 스케줄링
- 관리 대시보드 (웹 UI)
- 스크립팅 재도입 검토 — 레거시 `RoslynCompilerM`/`ScriptM`은 하드 룰 위반으로 폐기했다. 필요하면 AOT 호환 대안(사전 컴파일 플러그인)을 별도 ADR로
- 다중 언어 클라이언트 SDK (C++/Unity/TypeScript)
- 리그레션 테스트용 트래픽 리플레이 도구
- 프로토콜 문서 자동 생성 (스키마 → 문서)
