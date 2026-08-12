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
- [x] 원격 CI 첫 실행 — 2026-08-03, ubuntu·windows 양쪽 통과. **두 번 실패한 뒤 통과했다**: (1) SDK 드리프트로 CA2025 가 CI 에서만 발생(로컬 10.0.102 / CI 10.0.302) → `global.json` 고정, (2) `MaxConnections` 테스트가 실패 지점을 특정해 Linux 에서만 깨짐 → 보내기·읽기를 함께 감쌌다. Native AOT 는 ubuntu 러너에서 별도 준비 없이 통과했다
- [x] `Bench/` 골격 — `Bench/ChServerM.Bench` (BenchmarkDotNet 0.15.8). `BenchConfig`가 ServerGC·할당량 진단·첫 실패 시 중단을 고정한다. ENV-A 프로필 기록(Ryzen 9 9900X, 물리 12 / 논리 24)
- [x] 코드 커버리지 수집 (coverlet) + CI 리포트 — `eng/build.ps1 -Coverage`. **어셈블리별로 집계한다** (cobertura 파일명이 GUID 라 그대로 찍으면 관리에 쓸 수 없다). CI 가 cobertura 를 아티팩트로 올린다. 현재: Framing 95.0% / InMemory 83.4% / Concurrency 76.5% / Tcp 70.2% / Hosting 66.2% / Core 60.8%
  - [ ] 임계값 설정 — Core 추상화 확정 후. 지금은 수치를 보이게 만드는 단계다
  - [ ] ReportGenerator 도입 — 현재 집계는 어셈블리별 **최대값**이고 여러 테스트 프로젝트의 합집합이 아니다
- [x] ⚠ **public API 승인 파일 게이트** — `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0. `Server/Directory.Build.props`가 Server 어셈블리 6개에 적용한다(Tests/Bench/Samples 제외). 기준선 629줄. **켠 첫날 RS0026 으로 실제 API 결함을 잡았다** — `FrameWriter.WriteFrameAsync` 의 옵션 매개변수 기본값 세 개가 레거시 실패 패턴과 겹쳤고, 전부 필수로 바꿨다. 작업 절차는 CLAUDE.md 8.1
- [x] NuGet 취약점 감사 — `eng/build.ps1` audit 단계. **이 명령의 함정 둘을 모두 막았다**: (1) 취약점이 발견돼도 exit code 가 0 이라 naive 호출은 감사를 안 한 것과 같다, (2) 사람이 읽는 출력이 로케일에 따라 달라져 grep 이 CI 에서 깨진다 → `--format json` 파싱. **오프라인에서 빈 결과를 "안전함"으로 읽지 않도록** 원격 소스 존재까지 확인한다. 실제 취약 패키지(`System.Net.Http` 4.3.0)를 임시로 넣어 exit 1 을 확인했다
- [x] 의존성 업데이트 자동화 — `.github/dependabot.yml`. NuGet 주간 / GitHub Actions 월간. 테스트 도구와 분석기는 그룹으로 묶는다(버전이 어긋나면 "테스트가 발견되지 않는" 형태로 실패한다). **NuGet 메이저는 자동으로 받지 않는다** — ADR 이 필요한 결정이다
- [ ] **SDK 버전 업그레이드 (의도적 결정)** — `global.json` 이 10.0.1xx 로 고정돼 있다. 올리면 새 분석기 규칙이 함께 들어오므로 그때 걸리는 것들을 함께 처리한다. 지금 고정한 이유는 드리프트로 CI 가 갑자기 깨지는 것을 막기 위해서다 — 로컬 10.0.102 / CI 10.0.302 에서 CA2025 가 CI 에서만 터졌다
- [x] `LegacyServer/` + `LagacyClient/` 전수 정독 — 27,300줄 / 문서 14종. 결과: `docs/legacy/`(인덱스: [00-overview](legacy/00-overview.md))

**게이트**: CI가 build + test + 취약점 감사를 통과하고, public API 게이트가 켜져 있을 때.

> **✅ 2026-08-03 — 충족.** `eng/build.ps1` 6단계(restore·build·test·coverage·audit·aot)가
> Release + `-WarnAsError` 로 전부 통과하고, public API 게이트가 켜져 있고,
> **원격 CI 가 ubuntu·windows 양쪽에서 통과했다.**
>
> 남은 3건은 게이트 조건이 아니다 — `Client/` 솔루션 폴더(클라이언트 어셈블리 필요),
> SDK 업그레이드(의도적으로 뒤로 미룬 결정), 커버리지 임계값·ReportGenerator.

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
- [x] `IPayloadCodec` — 압축 계약 (2026-08-06, ADR-0019 — 해제 상한 필수 인자·자기서술 블롭·실패는 값. 구현체 `ChServerM.Compression.LZ4`)
- [x] `ITransportSecurity` — 전송 보안 계약 (2026-08-05, ADR-0017: 커넥션 파이프 데코레이터 — 전송 중립이라 인메모리에서도 보안 경로가 테스트된다. 실패는 예외가 아니라 상태(핸드셰이크 폭주 = 공격 시나리오 T-16), 기본값 센티넬은 비확립. TLS 어댑터 + 호스팅 통합까지 실동 — `4d8810f`·`e35a9ff`·`42659bc`)
- [x] `IAuthenticator` / `IAuthorizationPolicy` — 인증·인가 계약 (Phase 9에서 구현) (2026-08-06 완료: `IAuthenticator` = `aff7942`, `IAuthorizationPolicy` = `792ea9c` — 실패는 값(T-16)·default 는 가장 제한적·페이로드 수명 계약 명시. 구현체는 Hosting 미들웨어 2종)
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

- [ ] `ChServerM.Buffers` — 슬랩 할당기, 커넥션당 버퍼 대여 (진행 중: 어셈블리 신설 완료(무의존). **슬랩 할당기는 보류** — 커넥션 버퍼는 `Pipe` 가 이미 풀링하고 남은 수요(직렬화 스크래치)는 공유 풀로 충분하다. 공유 풀 경합·단편화가 관측되면(Phase 12 프로파일링) 재개한다 — ADR-0016)
- [x] ⚠ `ArrayPool` / `MemoryPool` 래핑 정책 결정 (ADR) — 2026-08-05 **ADR-0016**: `ArrayPool.Shared` 래핑, 대여 단위는 `PooledBufferWriter`(커넥션당 1개 + `Clear()` 재사용), 반납 책임은 소유자 `Dispose()` 단일, 초과 크기는 2배 대여-복사-반납
- [ ] `IBufferWriter<byte>` 기반 쓰기 경로 — 중간 배열 없이 소켓까지 (진행 중: `PooledBufferWriter` 로 스크래치 힙 할당은 제거(0B). 남은 것: 스크래치→파이프 복사 1회 — 헤더가 페이로드 길이를 선행 요구하는 구조적 제약이라, 헤더 공간 예약 방식은 프레이밍 계약 변경이 필요해 별도 판단)
- [x] **풀 누수 감지 진단** — 2026-08-05: ROADMAP 원안(DEBUG 전용)보다 강한 형태로 — 파이널라이저-온-리크가 Release 포함 상시 `BufferPoolDiagnostics.LeakedBuffers` 로 계수하고 버퍼를 회수한다(정상 경로는 `SuppressFinalize` 라 비용 0). 의도적 누수 검출을 게이트 테스트로 고정. Phase 11 이 카운터를 경보로 승격 예정
- [x] 대여 소유권 규약 문서화 — "만든 자가 `Dispose` 로 반납"을 타입 문서·ADR-0016 에 고정. `ref struct` 스코프는 **async 핸들러에서 쓸 수 없어 탈락**(대안 표), 대신 누수를 관측 가능하게 만들어 위반이 조용히 사라지지 않게 했다
- [x] 벤치마크: 대여/반납 처리량 — 요청당 619ns/8,056B(`ArrayBufferWriter` 매번) → **50ns/0B**(`PooledBufferWriter` 재사용), 12.3×. BENCHMARKS.md 버퍼 절. 커넥션당 메모리는 Phase 5 실측(~8KB)에 이미 포함

**게이트**: 대여-반납 왕복이 힙 할당 0이고, 누수 감지가 의도적 누수를 잡을 때.

> **✅ 2026-08-05 — 충족.** `PooledBufferWriterTests` 가 두 조건을 고정한다 —
> 정착 상태 1,000회 왕복 할당 0(`GC.GetAllocatedBytesForCurrentThread` 실측),
> Dispose 누락 버퍼가 파이널라이저로 검출·계수됨. 남은 항목(슬랩 보류,
> 파이프 직결)은 게이트 조건이 아니다.

## Phase 4 — 프레이밍

ADR-0002로 프레이밍은 직렬화와 분리된 독립 축이 됐다. 별도 Phase로 다룬다.

- [x] ⚠ **와이어 헤더 레이아웃 확정** — 16B 고정, 리틀 엔디안. **`MemoryMarshal`을 쓰지 않고 `BinaryPrimitives`만** 쓴다(정렬·패딩·호스트 엔디안에 와이어 포맷이 끌려가지 않는다). 버전 필드 포함. 레거시 52B(실데이터 13B) → 16B
- [x] length-prefix 디코더 — varint / fixed32 두 가지 (2026-08-04 완료: fixed32(`FixedHeaderFrameDecoder`) + varint(`VarintFrameDecoder`/`VarintFrameEncoder`, `374299b`). **`IFrameDecoder` 가설 해소** — 정반대 성질(가변 2~8B 헤더, 버전·플래그·일련번호 없음)이 같은 계약에 들어왔다. LEB128 정규형만 수용(비정규는 Malformed — 표현이 여럿이면 Phase 9 AEAD 가 흔들린다), 프레임당 할당 0 실측, 교체 테스트로 같은 에코 핸들러가 고정/varint × 인메모리/TCP 4조합에서 동작(DoD-5))
- [x] 부분 프레임 처리 — 헤더가 세그먼트를 넘을 때만 16B `stackalloc`(힙 할당 0). 세그먼트 크기 1~64 및 헤더 16B의 모든 분할 지점을 테스트
- [x] **프레임 오류 처리 정책** — 길이 이상·버전 불일치·미정의 플래그·예약 필드 비영 시 커넥션 종료. 재동기화를 시도하지 않는다(다음 경계를 알 수 없다). 체크섬 필드는 두지 않는다 — 레거시 검증 함수는 본문이 `return true`였고 무결성은 Phase 9 AEAD가 담당한다. 레거시는 예외를 잡고 루프를 계속해 상태 머신이 어긋난 채 파싱을 이어갔다(프레이밍 desync). `TryXxx`로 처리하고 오류 프레임은 커넥션을 닫는다
- [x] 최대 프레임 크기 상한 — `MaxPayloadLength` + 절대 상한 64MiB. 길이 필드를 `uint`인 채로 비교한다(`int` 캐스팅 후 비교하면 2GB 이상이 음수가 된다). **버퍼를 잡기 전에** 판정
- [x] 프레임 조립 상태 머신 — **상태 머신이 필요 없는 설계로 해소했다.** 부분 프레임 상태는 `PipeReader` 버퍼가 이미 들고 있으므로 디코더는 무상태이고, 인스턴스 하나를 모든 커넥션이 공유한다. 레거시가 커넥션마다 5단 상태를 들고 있다가 예외 한 번에 desync 된 원인이 구조적으로 사라진다
- [x] ⚠ **Core 프레이밍 계약의 고정 헤더 결박 재검토** — (2026-08-04 해소, **ADR-0010** + `e162534`) 논리 엔벨로프 방향 채택: Core 에 `MessageEnvelope`(MessageId+Flags+Sequence) 신설, `FrameHeader` 는 `ChServerM.Framing` 으로 이동, `IFrameEncoder.HeaderSize`(상수 전제) → `MaxHeaderSize`(상한) + `WriteHeader(writer, envelope, payloadLength)`. 표현 불가 값은 인코더가 예외로 거부(조용한 유실 금지). 대안(Features 경유 최소 계약)은 프레임마다 feature 조회가 핫패스에 들어와 탈락 — 근거는 ADR-0010
- [x] **조각 재조립(`Fragmented`/`EndOfMessage`)** — 2026-08-05 (ADR-0015). 커넥션 소유 `FragmentAssembler`: 연속성 계약(진행 중엔 같은 ID 조각만) + 누적 상한(`MaxAssembledMessageLength`, 기본 1MiB, 0=거부) + 완성 즉시 풀 반납. **미완성 만료는 전용 타이머 없이** — 연속성 계약 아래 "멈춘 재조립 = 무입력 커넥션"이라 전송 idle timeout 이 끊는다(9.5). 송신은 `FrameWriter.WriteFragmentedFrameAsync`. 위반 8경로 통합 테스트 고정
- [x] 퍼징 테스트 — 난수 11만 회(단일 세그먼트 5시드×2만 + 분절 2시드×5천 — 종전 "12만"은 과기재, 2026-08-04 정정) + 비트 플립 2만 회 + 잘린 프레임 전 오프셋 + 길이 필드 극단값. 불변식 4종(예외 없음 / 버퍼 밖 미참조 / **반드시 전진** / `NeedMoreData`는 버퍼 전체 검사). 시드 고정으로 재현 가능
- [x] 벤치마크: 프레임당 파싱 비용, 할당 0 확인 — 디코딩 **약 29 ns**, 할당 0. 초당 10만 프레임이면 코어 하나의 0.3%로, ADR-0002 의 "헤더 파싱 비용 0" 주장이 성립한다. 세그먼트 경계 경로는 14~19% 느리지만 절대값 4 ns 라 최적화할 이유가 없음을 확인

**게이트**: 퍼징이 크래시 없이 통과하고 프레임당 할당이 0일 때.

## Phase 5 — TCP 전송 (첫 실동 구현)

- [x] ⚠ Kestrel Socket Transport 재사용 vs 순수 `Socket`+Pipelines — 양쪽 프로토타입 벤치마크 후 **ADR-0001 확정** (2026-08-05 확정: **순수 Socket 유지**. Kestrel 프로토타입(`KestrelSocketServerTransport`, 벤치 전용)과 3개 시나리오 대결 — 전 항목 ±3.2% 통계적 동률, p99 는 순수 소켓 우위. Kestrel 에 유리하게 기운 비교에서도 이득 없음 + FrameworkReference 유입 회피. 수치: BENCHMARKS.md 2026-08-05)
- [x] `ChServerM.Transport.Tcp` — accept 루프 + 수신/송신 펌프. `TcpClient`/`NetworkStream`을 쓰지 않는다. 수락 루프가 일시적 오류(개별 연결 끊김)와 치명적 오류(수락 소켓 사망)를 구분한다 — 구분하지 않으면 조용히 수용을 멈추거나 CPU를 태운다 (2026-08-04 DoD-3 충족: 에코 146-169k RPS / p50 104µs / p99 162µs~7.5ms — `BENCHMARKS.md` Phase 5 게이트 절)
- [x] 백프레셔 — pause/resume 임계값을 옵션으로 노출. **최대 프레임보다 커야 한다는 제약을 조립 시점에 검사한다**(ADR-0007). `WaitForDataBeforeAllocating`(0바이트 수신)으로 유휴 커넥션이 버퍼를 붙들지 않게 (2026-08-04 DoD-3 충족: 1만 접속 실측 — 서버 워킹셋 82MB, **커넥션당 약 8KB**. 레거시 방식이었다면 640MB)
- [x] 커넥션 생명주기 — idle timeout, half-open 감지(keepalive), graceful close, RST 처리 (2026-08-04 완결: `IdleTimeout` 옵션 + **전송당 스윕 타이머 하나**(9.5 — 커넥션당 타이머 금지). 판정 해상도는 스윕 주기(≥1s)만큼 거칠다 — 계층적 타이밍 휠은 Phase 17 타이머 시스템과 함께. 활동 시각은 수신·송신 펌프가 `Volatile` 로 기록)
- [x] **종료 레이스 처리** — 로그인 완료 전 연결이 끊기는 경우. 레거시는 1초 지연 타이머로 대응했다(실전에서 나온 장치). (2026-08-04: 구조적 보장을 테스트로 고정 — 디스패치 중 상대가 RST 로 소멸해도 완료 신호→읽기 루프 종료→`finally` 정리 사슬이 지연 타이머 없이 완결된다. `Phase5TransportTests.ClientVanishing_MidDispatch`)
- [x] 송신 배칭 — 작은 패킷 다수를 묶어 syscall 수를 줄인다 — 2026-08-05 실측으로 판정 완료: **자연 배칭(`Pipe` 4KB 블록 병합) 채택, 벡터드 send 탈락.** gather 경로를 구현하고 배치 극대화 파이프라이닝 시나리오(LoadRunner `--pipeline`)로 A/B 한 결과 처리량 이득 0, p99 소폭 악화, `Task` 할당으로 GC 힙 3배(BENCHMARKS.md 송신 배칭 절). `UseVectoredSend` 옵션(기본 꺼짐)과 정확성 테스트(`VectoredSendTests`)는 재현용으로 유지 — 실 NIC 다중 머신 측정(Phase 12)에서도 지면 제거한다
- [x] Nagle / `TCP_NODELAY` 정책 — `NoDelay` 기본 켬(Nagle 비활성). 근거를 옵션 문서에 기록
- [x] 소켓 옵션 노출 — 버퍼 크기, linger, reuseaddr (2026-08-04: `SocketReceiveBufferSize`/`SocketSendBufferSize`/`LingerSeconds`/`ReuseAddress` — 전부 표준 옵션이라 크로스 플랫폼. **`IOControlCode.KeepAliveValues`는 여전히 배제** — Windows 전용, 레거시의 이식 차단 요인)
- [x] **거부 이유를 클라이언트에 알린다** — (2026-08-04: `FrameworkMessageIds.ConnectionRejected`(40004) + `TcpTransportOptions.RejectionNotice` 원시 바이트. 전송은 프레이밍을 모르므로 조립하는 쪽이 인코더로 프레임을 만들어 넣고, 전송은 최선 노력 동기 send 후 닫는다 — 거부 경로의 비동기 대기는 그 자체가 공격 표면이다. 상한 도달의 동적 판정·정책은 Phase 10 `IAdmissionControl` 의 몫)
- [x] 통합 테스트: 연결/에코/대량 동시 접속/비정상 종료 — 연결·에코·200KB 다중 세그먼트·200프레임 파이프라이닝·16커넥션 동시·비정상 종료 격리·포트 점유(xUnit) + **1만 동시 접속은 부하 러너로 검증**(2026-08-04, 실패 0·정리 후 잔존 0)
- [x] 크로스 플랫폼 검증 — Linux/Windows 소켓 동작 차이 (2026-08-04 충족: 액터 전환 + Phase 5 전체 분량 12커밋 푸시 후 원격 CI ubuntu·windows 양쪽 통과 — run 30893493578, build·test·audit·AOT. 통합 테스트가 idle timeout·소켓 옵션·비정상 종료·1만 접속 경로를 포함한다. 이식 불가 API(`IOControlCode.KeepAliveValues` 등)는 코드에서 이미 배제)
- [x] 벤치마크: 에코 RPS, p50/p99/p999 레이턴시, 커넥션당 메모리, 동시 커넥션 상한 — `Bench/ChServerM.Bench.LoadRunner`(ADR-0009). 수치는 `BENCHMARKS.md` Phase 5 게이트 절

**게이트**: 1만 동시 커넥션에서 안정 동작하고 p99 레이턴시 기준선이 기록됐을 때.

> **✅ 2026-08-04 — 충족.** 10,000/10,000 접속(실패 0·에코 오류 0·정리 후 잔존 0),
> 서버 워킹셋 82MB(커넥션당 ~8KB), 에코 146-169k RPS, 지연 바닥 p50 104µs / p99 162µs.
> 단일 머신 루프백 측정이라는 한계는 `BENCHMARKS.md` 에 명시. 남은 항목(idle timeout,
> 송신 배칭, 종료 레이스, 소켓 옵션, 거부 통지, ADR-0001, 크로스 플랫폼 CI)은
> 게이트 조건이 아니다.

## Phase 6 — 직렬화 어댑터

축 교체가 실제로 동작함을 증명하는 단계. **최소 2개 구현이 필수.**

- [x] `ChServerM.Serialization.MemoryPack` — 2026-08-05 (ADR-0011). 엄격 소비·null 거부 계약. `IsRegistered` 거짓 음성(정적 생성자 미실행)을 테스트로 재현하고 `RunClassConstructor` 로 해소. 트리밍 억제 2건(IL2091·IL2059)은 근거 주석과 함께
- [x] `ChServerM.Serialization.Protobuf` — 2026-08-05 (ADR-0012). Google.Protobuf 3.35.1 (protobuf-net 은 IL emit 이라 탈락). `ParseFrom(ReadOnlySequence)`/`WriteTo(IBufferWriter)` 직결. 제공자는 조립 시점 명시 등록 — 제약 있는 제네릭을 리플렉션 없이 못 만든다(AOT)
- [x] `ChServerM.Serialization.FlatBuffers` (FlatSharp 7.9.0) — 2026-08-05 (ADR-0012). **Greedy 전용** — Lazy/Progressive 는 반환 객체가 페이로드 버퍼를 참조해 계약(호출 후 무효) 위반, 생성자가 조립 시점에 거부. ⚠ FlatSharp.Compiler 가 net9 도구라 `eng/build.ps1` 이 `DOTNET_ROLL_FORWARD=LatestMajor` 설정
- [x] ⚠ 4자 벤치마크 → `docs/BENCHMARKS.md` + **ADR-0002 남은 부분(페이로드 기본값) 확정** — 2026-08-05 (ENV-B). 왕복 최속 MemoryPack(small 104ns, 역직렬화 39% 우위) / 직렬화 단독·와이어 최소는 Protobuf(33ns·71B — "전 항목 최속" 초기 가설 부분 기각). **기본값 = MemoryPack (ADR-0013)**. MessagePack 은 벤치 비교군 전용, 어댑터 없음(ADR-0012 결정 3)
- [x] 스키마 진화 테스트 — 필드 추가 시 구버전 호환성 3포맷 고정: protobuf 양방향+모르는 필드 보존 / FlatBuffers 양방향(보존 없음) / **MemoryPack 기본 모드는 단방향**(신데이터→구리더 실패 — 롤링 배포 경계엔 `VersionTolerant` 필수. 초기 가정 "양방향 비호환"은 실측으로 정정)
- [x] 동일 샘플이 어댑터 교체만으로 동작하는지 검증 — `SerializerSwapTests`: 같은 핸들러 코드 × Utf8/MemoryPack/Protobuf/FlatSharp. `IMessageSerializer`/`IMessageSerializerProvider` 가설 해소(DoD-5)
- [x] 크로스 언어 상호운용 확인 — 결론을 ADR-0013 에 기록: "클라이언트는 C#" 을 Core 전제로 넣지 않고, 비-C# 요구 시 답은 Protobuf 어댑터 교체(이미 실동). 실제 비-C# 클라이언트와의 와이어 왕복 실증은 그 요구가 확정될 때 수행한다

**게이트**: 두 개 이상의 어댑터가 같은 샘플에서 동작하고 기본값 ADR이 확정됐을 때.

> **✅ 2026-08-05 — 충족.** 실동 어댑터 3종(MemoryPack·Protobuf·FlatSharp)이
> `SerializerSwapTests` 에서 같은 핸들러 코드로 동작하고, 기본값이 ADR-0013 으로
> 확정됐다(4자 벤치마크 근거). Phase 6 전 항목 완료 — 커밋 `cd02d6d`.

## Phase 7 — 디스패치 & 소스 제너레이터

- [x] `ChServerM.SourceGen` — 메시지 ID → 핸들러 디스패치 테이블 생성 — 2026-08-05 완결(ADR-0014 + 2차): `[MessageHandler]` 발견 + `MapGeneratedHandlers` 등록 생성(시작 시점에 배열 테이블로 굳는다). **switch 문 직생성은 측정으로 탈락** — 배열 인덱싱 0.69ns vs switch 0.88ns(BENCHMARKS.md), 제너레이터가 디스패처 본체를 만들 근거가 없다. EchoServer 샘플이 생성 경로를 태우고 Native AOT publish 통과
- [ ] 컴파일 타임 검증 — 중복 메시지 ID, 누락 핸들러, 시그니처 불일치를 **빌드 실패로** (진행 중: 중복 ID(CHSM1001)·계약 미구현(1002)·센티넬(1003)·모호(1004)·인스턴스화 불가(1006) 완료. **누락 핸들러 검출은 남았다** — 메시지 선언 목록이 있어야 성립하므로 메시지 레지스트리 설계와 함께)
- [x] 진단 규칙 ID 체계 (`CHSM1xxx`) + 각 진단에 대한 문서 — 2026-08-05: `docs/DIAGNOSTICS.md` 신설(CHSM0xxx 가드 포함), CHSM1001~1007. 릴리스 추적(RS2008)은 `AnalyzerReleases.*.md` — PublicAPI 승인 파일과 같은 diff 게이트
- [x] 제너레이터 스냅샷 테스트 — 생성 코드 전문을 기대 문자열로 고정 + 생성 코드 포함 컴파일레이션이 오류 0 으로 컴파일되는지 검증. 드라이버 테스트 9종(진단별 1개 이상)
- [x] 증분 생성(`IIncrementalGenerator`) — `ForAttributeWithMetadataName` + 값 동등성 record 모델(`Location` 은 값 표현으로 변환해 캐시 유지). 처음부터 증분으로 작성 — 비증분에서 이관할 일이 없다
- [ ] 리플렉션 기반 폴백 디스패처 (개발 편의용, 프로덕션 비권장. AOT에서 비활성)
- [x] 벤치마크: 디스패치 오버헤드 (생성 코드 vs 리플렉션 vs 딕셔너리) — 2026-08-05 (ENV-B, 5종 비교): 배열 0.69ns / switch 0.88ns / Frozen 1.12ns / Dictionary 1.93ns / 리플렉션 20.5ns+32B. 프로덕션 전체 경로 7.63ns·할당 0. 리플렉션 폴백의 "프로덕션 비권장" 근거 수치 확보

**게이트**: 생성 코드 경로가 AOT에서 동작하고 딕셔너리 방식보다 빠름이 측정됐을 때.

> **✅ 2026-08-05 — 충족.** EchoServer 샘플이 `[MessageHandler]` 생성 등록 +
> MemoryPack 어댑터 경로로 Native AOT publish·자체 검증을 통과하고,
> 배열 라우팅 0.69ns vs Dictionary 1.93ns(2.8×)가 측정됐다. 남은 항목(누락 핸들러
> 검출, 리플렉션 폴백)은 게이트 조건이 아니다.

## Phase 8 — 동시성 실행 모델

> **`CLAUDE.md` 9절(병렬성 규약)이 이 Phase의 구현 기준이다.**
> 특히 9.1(공유 대신 파티셔닝), 9.2(`finally`로 상태 복원), 9.5(스레드 수 통제),
> 9.6(유계 큐 + 포화 관측)은 레거시에서 실제로 서버를 멈추거나 데이터를 유실시킨 항목이다.


- [x] `ChServerM.Concurrency` — 파티션당 전용 스레드 + 단일 FIFO 채널. **큐를 하나로 둔 이유**: 스케줄러 연속과 외부 게시가 같은 FIFO를 공유해야 둘 사이의 순서도 보장된다. **유계 규약(9.6)은 유입 지점에만 적용한다** — 스케줄된 `Task`를 거부하면 그것을 `await`하던 코드가 영원히 깨어나지 못한다
- [ ] ⚠ **유저별 순서 보장 구현** — `IExecutionModel` 계약의 실체 (진행 중: **커넥션 단위**로 완료. `PartitionedConnectionHandler`가 읽기 루프를 파티션에 고정해 프레임당 큐 비용이 0이다. 유저/세션 단위 고정은 세션 계층과 함께 — 지금은 재접속하면 다른 파티션으로 간다)
- [ ] 스레드-퍼-코어 모델 + CPU 어피니티 (진행 중: 파티션당 전용 스레드 = 기본 `ProcessorCount`개, 절대 상한 512. **CPU 어피니티 미적용**)
- [x] false sharing 회피 — 파티션 대기 카운터를 `[StructLayout(Size=128)]`로 패딩. 64B가 아니라 128B인 이유는 일부 x86이 인접 라인을 함께 프리페치하기 때문
- [ ] **작업 상자 풀을 파티션별로 분리 검토** — `TryPost` 게시당 할당이 파티션 1개에서 1 B 인데 8개에서 **70 B** 다. `WorkBoxPool<TWork>` 가 파티션 간 공유이고 상한이 1,024 인데 in-flight 가 그보다 크면 새로 할당하고 넘치면 버리는 churn 이 생긴다. 코드 주석에 "경합이 문제가 되면 파티션별 풀로 바꾼다 — 그때는 측정 결과를 근거로 남긴다"라고 적어둔 그 시점이다 (2026-08-04: 이 항목이 두 줄로 중복돼 있던 것을 정리 — 총 항목 수가 218→217 로 정정된다)
- [x] **ADR-0008 프레임당 할당 교정 (측정 기반)** — 완료(`bc8ec95`): 프레임당 184 B → **~0.01 B**, Gen0 33k회 → 0. 교정 3건(게이트 `IThreadPoolWorkItem` 게시 / 재사용 이벤트 대기 / `WaitToReadAsync` 토큰 미전달)과 트레이드오프(핑퐁 P≤2 벽시계 ~10%↑)는 `BENCHMARKS.md` 2026-08-04 perf 절
- [x] **소비 스레드 상시 바쁨 구성의 배타 왕복 측정** — 완료(`bc8ec95`): 프레임당 +0.26µs, P=4 까지 3.16×(79%) 확장. 핑퐁 역확장이 깨우기 비용 지배임을 확인. P≥8 은 초과 구독 영역이라 코어 제한 재측정과 함께 다시 본다
- [ ] 스케줄러 공정성 — 한 유저가 워커를 독점하지 못하게
- [x] 데드락·경합 테스트 — 8 생산자 × 2000 작업이 정확히 1회 실행되는지 + Release 반복 실행. **실제로 두 건을 잡았다** — `Abort`가 송신 펌프를 깨우지 않아 생긴 교착, 최대 프레임 > 전송 버퍼 교착(ADR-0007)
- [x] ⚠ **예외 안전성 테스트** — 항목별 `try/catch` + `finally`로 큐 슬롯 복원. 예외 작업 50개 뒤에도 정상 작업이 처리되고, 용량의 10배를 예외 작업으로 밀어 넣어도 슬롯이 새지 않음을 확인
- [ ] 액터 모델 어댑터 검토 (Orleans / Proto.Actor) — Core에 침투 금지
- [x] ⚠ **벤치마크: 코어 수 대비 확장성 곡선** — 파티션 1·2·4·8·12·24 스윕. **물리 코어 구간 효율 95.0% 이상**(12파티션 11.40배). ADR-0005 의 검증 조건 충족.
  - [x] 실제 코어 제한 재측정 — 2026-08-07 완료(ENV-B): 프로세스 어피니티로 물리 코어 1·2·4·8·16 을 제한해 재측정. **16 코어 14.67× (효율 91.7%)** 이고 같은 머신의 순수 ALU 천장(14.81×)의 **99.0%** — 남은 손실은 실행 모델이 아니라 하드웨어·OS 의 몫이다. **선행 발견: SMT 형제가 인접 쌍이라 기존 안내(`start /affinity F` = 4코어)가 틀렸다** — 실제로는 2코어이며 물리 N 코어는 한 칸씩 건너뛴 마스크(`0x55` 등)다. `Program.cs` 안내 정정. 어피니티 적용은 **1코어 행이 파티션 24까지 완전 평탄**한 것으로 검증. 초과 구독은 비율이 고르지 않을 때 더 나쁘다(8코어에서 12파티션 61.7ms > 24파티션 46.8ms) → 파티션 수는 코어 수의 배수로. BENCHMARKS.md 2026-08-07 절
  - [ ] NUMA 다중 소켓 머신에서 재확인 — ENV-A 는 단일 소켓이다
- [x] 유저별 순서 보장 오버헤드 측정 — **3.9%**. 파티션 모델(26.95ms)이 무순서 스레드풀 병렬의 상한(25.93ms)에 그만큼 차이로 근접한다. 전역 락은 직렬보다 느렸다(353.57 vs 351.20ms) — ADR-0005 의 탈락 근거가 수치로 확인됐다
- [ ] **경합 측정** — 원자 연산 경합, false sharing 유무를 프로파일로 확인 (9.3·9.4)

**게이트**: 코어 수 대비 처리량이 선형에 근접하고, 순서 보장이 부하 상태에서도 깨지지 않음이 검증됐을 때.

---

# Part III — 프로덕션 필수 (Production Essentials)

**이 Part를 건너뛰고 Part IV로 가지 않는다.** 상업용 서버에서 여기가 비면 나머지가 무의미하다.

## Phase 9 — 보안

- [x] ⚠ **프로토콜 버전 협상** — 현재 유일한 정책이 "버전 불일치 → 커넥션 종료"인데, 무중단 롤링 배포(Phase 15) 중에는 서버·클라 버전 불일치가 **정상 상태**라 구조적으로 충돌한다(2026-08-04 감사). 핸드셰이크에서 상호 지원 버전을 교섭하는 경로가 필요하다 — Phase 9 핸드셰이크 설계에 포함시킨다 (2026-08-06 완료 `2fcb199`+`6d70037`: `VersionHandshakeCodec`(Core, **영구 동결** 고정 헤더 v1 + 고정 바이너리 — 교체 가능한 축에 부트스트랩을 얹지 않는다(R-2), `FrameHeaderCodec` 과의 일치는 교차 검증 테스트) + `UseVersionNegotiation()` 양쪽 빌더(보안이 바깥 = 협상은 TLS 채널 안, R-4 구조 충족). 거부 = 서버 지원 구간 실은 40004(R-3), 무응답 = 타임아웃 절단(T-16), 결과 = `IProtocolVersionFeature` + 버전별 로그(R-5, 카운터는 Phase 11). 종단 8종·2전송)
- [x] ⚠ **위협 모델 문서화** — `docs/THREAT-MODEL.md`. 신뢰 경계, 공격 표면, 각 위협에 대한 완화책. 이것 없이 개별 대책을 만들면 구멍이 남는다.
  **출발점**: [07-security](legacy/07-security.md#새-코드에-절대-옮기면-안-되는-것)의 결함 목록을 위협 → 완화책으로 매핑한다 (미인증 키 교환, XOR '암호화', AES-128 + 고정 IV, 인증 없는 CBC, PKCS#1 v1.5, 커넥션당 RSA 생성, 와이어 값 기반 할당, 최대 프레임 크기 부재)
  (2026-08-05 완료 `2842d2d`: 신뢰 경계 5 · 공격 표면 9 · 위협 22(STRIDE) **전 항목 완화책 매핑** + 레거시 결함 14종 역매핑 표. 게이트의 매핑 조건 충족 — 남은 게이트 조건은 "인증 전 패킷 차단 테스트"뿐)
- [x] `ChServerM.Security.Tls` — `SslStream` 기반 전송 보안. 인증서 로딩·검증·회전 (어댑터 `e35a9ff` + 빌더 통합 `42659bc` + TLS on/off 실측(RPS −2.5%·p50 +50µs). 2026-08-06 운영 경로 완결 `886b2ab`: `IServerCertificateSource`(핸드셰이크별 해석 — 회전이 재시작 없이 반영) + `FileCertificateSource`(PFX/PEM 쌍, Windows ephemeral 함정 내장 흡수, mtime 폴링 — FileSystemWatcher 는 k8s Secret 마운트에서 이벤트 누락으로 탈락 + 명시 `Reload()`). 재적재 실패 = 기존 유지+경고(가용성), 구세대 1세대 보관(use-after-dispose 방지), 만료 임박 경고. 실핸드셰이크 포함 테스트 8종)
- [x] ⚠ **핸드셰이크·키 교환 설계** — 레거시는 `FbsEncryptKey`(key/iv)로 교환하고 서버→클라 XOR, 클라→서버 AES256을 썼다. **XOR은 암호화가 아니다.** 양방향 AEAD(AES-GCM / ChaCha20-Poly1305)로 재설계한다 (2026-08-05 **ADR-0017 로 확정: 자체 재설계 대신 TLS 1.3(`SslStream`) 위임** — 키 교환·양방향 AEAD·nonce·다운그레이드 방지를 검증된 구현이 담당한다. 자체 ECDHE+AEAD·Noise 의 탈락 근거는 ADR 대안 표)
- [x] `IPayloadCodec` 구현 — 압축(LZ4/Zstd). 레거시 정책(1024B 미만 무압축) 참고. **압축 후 암호화 순서 고정** (역순은 CRIME류 취약점) (2026-08-06 완료 `c04644b`+`3507b0e`+`b9bd2d0`+`2030489`, ADR-0019: 계약은 해제 상한 **필수 인자**(T-18 생략 불가) + 자기서술 블롭(버퍼 확보 전 선언 검증, T-12 역). 첫 어댑터 LZ4(K4os) — Brotli 대비 비압축성 최악 경로 11~35× 실측 우위. 순서 고정은 구조 보장(압축=페이로드 수준, TLS=스트림 수준) + 비밀 문맥 `DoNotCompress`(T-11). 송신은 플래그 자동 부착(표시-변환 불일치 불가), 수신은 재조립→해제, 미조립+압축 프레임 = 종료. 1GiB 폭탄 = 할당 0 거부 종단 고정. "압축이 실제로 실행됨" 테스트 — 레거시 무동작의 역)
- [x] 리플레이 방지 — 패킷 시퀀스/nonce 검증. 레거시 `pid`(패킷 아이디) 개념 승계 (2026-08-05 **ADR-0017 결정 4 로 재배치 — `pid` 승계 안 함**: 커넥션 내 와이어 리플레이는 TLS 레코드 계층이 차단하므로 앱 시퀀스를 중복 구현하지 않는다. 크로스 커넥션 토큰 재사용은 `IAuthenticator`(1회용·만료 토큰) 항목의 몫. 보안 축 "없음" 조립은 무보호임을 계약 문서에 명시)
- [x] 무결성 검증 — AEAD 태그로 대체 (레거시의 단순 체크섬은 공격자에게 무의미) (2026-08-05: TLS 1.3 레코드 AEAD 가 담당(ADR-0017) — 구현·종단 테스트·실측까지 완료. 헤더에 가짜 무결성 장치가 없음은 Phase 4 에서 이미 확정(체크섬 필드 제거))
- [x] **상태별 패킷 화이트리스트** — 인증 전에 인증 후 패킷을 받지 않는다. 레거시 `AllowedPacketM` 승계 (2026-08-05 완료 `1b3fc20`: `IConnectionStateFeature`(Core, 상태 비트마스크 — 의미는 앱 정의, ADR-0004) + `MessageStateFilterMiddleware`(FrozenDictionary + 비트 AND, **기본 거부는 옵션이 아님** — 레거시 `AllowedPkState` 기본 전부 허용 결함의 역). 거부 = 커넥션 종료(4001) + 경고 로그. 게이트 테스트: 인증 전 특권 메시지 = 응답 없이 종료(InMemory/TCP 2전송) 포함 +10)
- [x] `IAuthenticator` 구현 — 토큰 검증. 레거시 `BasicLibM/AuthM` 판정 필요 (2026-08-06 완료 `aff7942`+`9a7297d`+`f47967b`: 미들웨어 방식 — 실패 = `next` 미호출 + **옵션 무관** 6000 종료(전용 `RejectedByAuthentication`, T-20 구조 봉쇄), 성공 = `GrantedStates` 상태 대체 전이(T-19 필터와 한 몸, 별도 "인증됨" 플래그 없음). 조립 순서(필터→인증) 위반은 `Build()` 예외. `ITokenReplayGuard` + 유계·TTL 인메모리 구현(T-05, 검증→클레임 순서 계약). `AuthM` 판정 이행: `PasswordHasher` 위임 어댑터 `ChServerM.Security.AspNetIdentity`(ADR-0018, 레거시 해시 형식 호환·결함 4종 역해소). 종단 16종·2전송 + 해셔 7종)
- [x] 인가 미들웨어 — 메시지별 권한 검사 (2026-08-06 완료 `792ea9c`+`83a8d76`: 2단 구조 — 메시지 수준은 T-19 필터+`GrantedStates` 기본 거부가 담당, 자원 수준(페이로드 의존 소유자 검사·동적 정책)은 `AuthorizationMiddleware` 보호 목록+`IAuthorizationPolicy`. 거부 = 6001+`CloseOnPolicyRejection` 옵션(인증과 의도적 비대칭). 조립 순서 필터→인증→인가 `Build()` 검증. 종단 11종·2전송)
- [x] 시크릿 관리 — 설정 파일에 키를 두지 않는다. 환경변수/시크릿 저장소 (2026-08-06 완료 `3225133`+`6e22fcd`: `ISecretSource`(Core — 미래 KeyVault류 어댑터의 의존 방향) + `EnvironmentSecretSource`(12-factor)/`DirectorySecretSource`(k8s 마운트, 캐시 없음 = 회전 즉시, 경로 탈출 방어). **빈 값 = 부재**(빈 암호로 조용히 진행 금지). 가짜 메모리 보안 타입은 만들지 않음(SecureString류 = 가짜 체크섬의 재판 — T-10 현실 완화는 문서 계약). 레거시 하드코딩 자격증명(`ServerGlobals.cs:103`) 판정: 커밋 시점에 유출 간주, 재사용됐다면 폐기·교체 권고 — 레거시 트리는 참조 전용이라 코드 불변)
- [x] 입력 검증 — 모든 페이로드 필드 범위 검사. 퍼징 확대 (2026-08-06 완료 `f1cd165`: `IMessageValidator<T>`(Core) — 역직렬화 성공 ≠ 유효한 값(T-22). `Map` 4인자 오버로드가 역직렬화 직후·핸들러 전 검증을 강제(핸들러 안 검증은 하나쯤 빠뜨린다). 실패 = `DeserializationFailed` 재사용(범위 밖 = 스키마 어긋남과 같은 부류). 퍼징 확대 2종: 핸드셰이크 코덱·LZ4 해제 — 무작위 5000회 + 유효 프레임 전 비트 변조에 던지지 않음·출력 상한 준수·실패 시 미커밋. 유효 범위 자체는 워크로드 소관 — 프레임워크는 검증이 끼는 자리와 실패 규약만 강제, ADR-0004)
- [x] `/security-review` 실행 + 결과 반영 (2026-08-06: 브랜치 전 보안 축을 3단계 리뷰 — 식별 → 병렬 위양성 필터 → confidence 8 미만 제거. **보고 임계치를 넘는 신규 취약점 0건**. 검증된 경로 10종(토큰 리플레이 단일 승자·인증 우회 불가·기본 거부·조립 순서 강제·핸드셰이크 바이트 정확 소비·해제 상한 선검증·인증서 2세대 보관·TLS 기본 전체 검증·PBKDF2 위임). 임계치 미만 관찰 2건(`DirectorySecretSource` 드라이브 상대 경로·TLS 없는 협상 다운그레이드)은 필터에서 각 confidence 2/10 위양성 확정 — 이름은 조립 시점 상수라 비신뢰 입력 미도달, 프레이밍 버전은 보안 차등 없음)

**게이트**: 위협 모델의 모든 항목에 완화책이 매핑되고, 인증 전 패킷이 차단됨이 테스트로 확인될 때.
(2026-08-05 **두 조건 모두 충족** — 매핑은 `2842d2d`, 인증 전 차단 테스트는 `1b3fc20`. **2026-08-06 Phase 9 전 항목 완료 — 13/13**)

## Phase 10 — 복원력 & 과부하 제어

- [x] `IRateLimiter` 구현 — IP별 / 세션별 / 메시지 타입별. `System.Threading.RateLimiting` 활용 (2026-08-06 완료 `a1cf3ec`+`c5876a7`: `IRateLimiter`(Core, `TryAcquire(MessageContext)`) + `RateLimitMiddleware` + 첫 구현 `PerConnectionRateLimiter`(커넥션별 토큰 버킷 — 상태를 `Connection.Features` 에 둬 순차 디스패치로 락-프리·축출 불필요). 거부 = `RejectedByRateLimit`(9)→6003, 무-종료(일시적 제한). 관측은 `DispatchFailures` 자동. 종단 5종. **System.Threading.RateLimiting 은 per-connection 에 과임** — 전역·IP별·메시지타입별 후속 구현이 그 라이브러리로 간다)
- [x] `IAdmissionControl` — 과부하 시 신규 연결 거부. **거부가 붕괴보다 낫다** (2026-08-06 완료 `f8bbf5b`+`1faca86`, ADR-0021: `IAdmissionControl`(Core, `TryAdmit(EndPoint?)`) + 전송 옵션 주입 + 첫 구현 `ConnectionRateAdmissionControl`(신규 연결 토큰 버킷 — 정적 상한 안의 연결 폭주 방어, T-16) + `CompositeAdmissionControl`(AND·단락). 거부는 전송이 `ConnectionsRejected` 메트릭으로 방출(사유 태그). 종단 11종. 2026-08-07 `4cb9373`(ADR-0026): **주소별 구현 추가** — `PerAddressConnectionRateAdmissionControl`(고정 슬롯 배열이라 **축출 없이 구조적 유계** — Dictionary 였다면 소스 주소를 바꾸는 것만으로 맵을 키워 OOM 을 유발할 수 있다. 표적 충돌은 `HashCode` 의 프로세스별 랜덤 시드가 차단). **IPv6 는 /64 프리픽스로 집계**(/128 단위는 공격자에게 2^64 우회로), IPv4 매핑 IPv6 는 IPv4 로 환원. 컴포지트로 전역과 AND. 테스트 13종. 워터마크는 후속)
- [x] 리소스 상한 — 최대 커넥션 수, 커넥션당 메모리 상한, 전체 메모리 워터마크 (최대 커넥션 수(정적 `MaxConnections`)·커넥션당 메모리(Phase 5 실측 ~8KB) 완료. **전체 메모리 워터마크 2026-08-08 완료**: 신호는 `MemoryLoadLevelSource`(ADR-0029, 비율 기반이라 컨테이너 제한 자동 추종), 배선은 `LoadLevelAdmissionControl` — 부하가 임계 이상이면 신규 연결을 거부해 **이미 붙은 커넥션을 보호**한다. **⚠ 기본 임계가 `Critical` 인 것이 핵심**: 1단계 `Elevated` 에서는 열화가 비필수 메시지만 버리고 문은 열어 두고, 2단계 `Critical` 에서야 신규 수용을 끊는다 — 조금 밀린다고 문을 닫으면 그 재시도가 accept 부하를 더한다. 열화와 **같은 부하 신호를 공유**해 운영자가 상태를 하나로 설명할 수 있다. 컴포지트로 속도 제한과 AND. 테스트 9종)
- [ ] 연결 폭주 방어 — accept 큐 관리, SYN 폭주 대응, 핸드셰이크 타임아웃 (진행 중: SYN·재접속 폭주는 `ConnectionRateAdmissionControl`(2026-08-06)이 신규 연결 속도 제한으로 방어. 핸드셰이크 타임아웃은 버전 협상·TLS 에 있음. accept 큐(backlog) 튜닝은 후속)
- [ ] 서킷 브레이커 / 재시도 미들웨어 — 외부 의존(DB/Redis) 장애 격리 (**보류 — 대상 부재**: 2026-08-07 조사(ADR-0027)에서 이 프레임워크에 **아웃바운드 호출 지점이 0** 임을 확인했다 — `Persistence.*` 어셈블리도 `ISessionStore`·`IClusterMembership` 계약도 없다. 지금 만들면 구현 0·호출 지점 0 인 추상화가 되고 실물이 올 때 계약이 틀린다("두 번째 구현 전까지 추상화는 가설", CLAUDE.md 3). **Phase 13 세션 저장소 / Phase 15 클러스터에서 첫 아웃바운드 호출과 함께 만든다**)
- [ ] Bulkhead — 한 기능의 장애가 전체를 마비시키지 않게 격리 (진행 중: 파티션 간 격리는 실행 모델이 이미 제공(다른 키 = 다른 파티션, ADR-0005). 2026-08-07 `576f393`(ADR-0027): **파티션 내 정지 감지** 추가 — 완료하지 않는 핸들러가 전용 스레드를 무기한 붙들면 그 파티션의 모든 커넥션이 함께 멈추는데 **스레드는 살아 있어 생존 신호로는 안 잡히는** 사각지대를 메웠다(진행 표식 + `CountStalledPartitions`, 프레임당 long 쓰기 2회·할당 0, 헬스 **Degraded** — 일시 지연을 재시작으로 승격시키지 않는다). **남은 것: 강제(핸들러 타임아웃)** — 협조적 취소라 CPU 무한루프를 못 막고 프레임당 타이머 비용이 붙어 별도 판단)
- [x] 우아한 열화(graceful degradation) — 부하 시 비필수 기능 차단 순서 정의 (2026-08-07 완료 `b88267b`, ADR-0029: 세 조각이 다 없었다 — ① 부하 신호 ② 우선순위 표현 ③ 구분되는 거부. `LoadLevel`+`ILoadLevelSource`(Core, 축 분리) + `LoadSheddingMiddleware`/`LoadSheddingOptions`(**차단 순서는 앱이 선언** — 프레임워크는 무엇이 비필수인지 모른다. **미등록 = 필수**라 설정 누락이 부하 시에만 재현되는 기능 상실이 되지 않는다) + `RejectedByLoadShedding`(10)/`ErrorCode.LoadShed`(5003) **무-종료**(`RejectedByPolicy` 재사용은 `CloseOnPolicyRejection` 에 걸려 부하 시 커넥션을 무더기로 끊고 재접속이 부하를 키운다 — 열화가 붕괴를 앞당긴다). 첫 소스 `MemoryLoadLevelSource`(비율 기반이라 **컨테이너 메모리 제한 자동 추종**, 1초 캐시). 평상시 `Normal` 이면 규칙 조회 없이 통과. 테스트 10종)
- [x] 크래시 처리 — 미처리 예외 정책, 덤프 수집, 재시작 전략 (2026-08-07 완료 `79e640f`, ADR-0028. 셋의 답이 각각 다르다: **① 미처리 예외 = 코드** — 조사에서 실재 결함 발견(수락 루프가 소켓 예외만 잡는데 `StartConnection` 은 사용자 공급 `IAdmissionControl` 을 부른다 → 던지면 루프가 죽고 태스크는 `Unbind` 까지 관측되지 않아 **readiness 는 계속 "수용 중"**, 예외는 종료 시점에야 튀어나옴 = 조용한 죽음). catch-all + `TcpServerTransport` 가 `IHealthCheck` 구현 → 호스팅이 readiness 에 자동 등록(실행 모델과 같은 옵트인 규율). 자동 재시작은 안 한다(지속적 원인이면 무한 예외 루프). **② 전역 훅 = opt-in 헬퍼** `ProcessFaultHandlers.Install`(프로세스는 호스트의 것이라 기본 설치 안 함, `IDisposable` 로 해제. `SetObserved()` 는 안 부른다 — 진짜 버그를 감춘다). **③ 덤프·재시작 = 운영 설정**(덤프는 `DOTNET_DbgEnableMiniDump` 계열 — 관리 코드는 플랫폼별 P/Invoke + AOT 충돌. 재시작은 오케스트레이터 몫 — 자가 재시작은 백오프·이벤트 기록을 우회해 장애를 감춘다). 테스트 5종)
- [x] 장애 주입 테스트 — 지연·패킷 손실·의존성 장애를 주입해 동작 확인 (2026-08-07 완료 `56c16d3`: **생산 코드 변경 0** — 적대 조건은 기존 API 로 만들어지므로 주입 설비를 추상화하지 않았다(설비가 검증 대상보다 커진다). **무작위 카오스도 안 쓴다** — 재현 안 되는 실패는 테스트를 끄게 만든다. 메운 갭: 역직렬화 실패(정의만 있고 생산 경로 미검증이었다)·쓰레기 바이트 e2e(퍼즈는 코덱 수준만)·잘린 프레임 후 절단. **★ 복합 시나리오**: 수용 제어+속도 제한+열화를 동시에 걸고 200프레임 폭주 → 거부하되 **커넥션 유지**(무-종료 불변)·부하 하강 시 회복 = 게이트 주장의 실제 증거. 3회 반복 무경합)
- [x] 24시간 soak 테스트 — 메모리 누수·핸들 누수·성능 열화 확인. **단발 벤치마크로는 안 잡힌다** (2026-08-06 완료 `df1db82`: `SoakTests` 하네스 — 8워커 커넥션 처치(connect→16프레임→disconnect) 지속 반복. 게이트 기본 2초(대량 누수 결정적 감지, 게이트 상시 실행), `CHSM_SOAK_SECONDS=86400` 로 24h 정식 판. 실측: 2초에 27.9만 처치 사이클, 활성 커넥션 드레인 후 0(결정적 누수 신호), 최종 정착 메모리 3.0MB≈기준선 2.7MB. **정식 24h 판은 CI 스케줄·수동 운영의 몫** — 단발 세션에서 24h 는 못 돈다)

**게이트**: 과부하에서 거부하며 살아남고, 24시간 soak에서 메모리가 평탄할 때.
(2026-08-06 **실질 충족** — 전반부(거부하며 생존): 수용 제어 `IAdmissionControl`(ADR-0021)·속도 제한 `IRateLimiter`. 후반부(메모리 평탄): soak 하네스 짧은 판 실측 평탄(`df1db82`). **정식 24h 판만 수동/CI 실행으로 남음** — 하네스·짧은 판 증거는 완비. 2026-08-07 `56c16d3`: **게이트 전반부의 실제 증거 확보** — 그동안 수용 제어·속도 제한·열화가 따로만 검증됐는데, 셋을 동시에 걸고 적대적 폭주를 넣어 "거부하되 커넥션 유지·부하 하강 시 회복" 을 고정했다. 남은 Phase 10 항목: 서킷 브레이커(**대상 부재로 보류**, Phase 13/15) · Bulkhead 강제(감지는 완료) · 워터마크→수용 제어 배선 · 정식 24h soak CI 스케줄)

## Phase 11 — 관측 & 진단

> **설계 목표: "실패가 관측되는가".**
> 레거시에서 체크섬 검증·LZ4 압축·MongoDB 재시도·`HashM` 만료·백프레셔·콜라이더
> 비활성화가 **전부 무동작이었는데 아무도 몰랐다.** 원인은 로그 레벨 부재, 설정 파일이
> 없으면 로깅이 통째로 사라지는 구조, `Debug.WriteLine`의 Release 소멸, 그리고
> **메트릭 전무**다 ([09-observability](legacy/09-observability.md#phase-11-설계에-반영할-것)).
> → **조용한 실패가 가능한 지점마다 카운터를 두고 0이 아니면 경보한다.**
> 드롭된 패킷, 미반납 풀 대여, 실패한 재시도, 만료되지 않은 잡, 포화된 큐.


- [x] `ChServerM.Observability` — OpenTelemetry 트레이스·메트릭 (2026-08-06 1차 증분 `ca3b331`+`79babde`+`4186d84`, ADR-0020: `IMetricsSink`(Core) + `MeterMetricsSink`(BCL `System.Diagnostics.Metrics` — dotnet-counters 즉시, OTel 은 Meter 구독으로 얹는다). 트레이스는 후속 증분)
- [x] ~~ZLogger 어댑터~~ → **MEL 어댑터** (무할당 구조적 로깅) (2026-08-07 완료 `9ad167b`, **ADR-0030 — 대상을 바꿨다**. ZLogger 는 수단이고 괄호 안이 목표다. 조사에서 셋이 드러났다: ① `IServerLogger.Log<TState>` 가 `ILogger.Log<TState>` 와 인자 구성이 동일해 MEL 어댑터가 **~30줄 패스스루**이고 그 하나로 **ZLogger·Serilog·콘솔·Seq 생태계 전체**가 열린다 ② **무할당은 이미 충족** — 로그 지점 29곳 전수 확인 결과 전부 오류·희소 경로이고 정상 프레임 경로엔 로그가 **하나도 없다**(`IsEnabled` 게이트로 평상시 비용 0, 구조체 상태라 박싱 0) ③ ZLogger 의 방출 시점 무할당 경로는 **상태 타입이 ZLogger 인터페이스를 구현해야 해 Core 에 벤더가 스며든다**(하드 룰 위반) — 그 경로 없이는 MEL 경유와 동일. `ChServerM.Logging.Extensions`(`MicrosoftServerLogger`·`loggerFactory.CreateServerLogger()`), 상태 무박싱을 테스트로 고정, 범주는 `ChServerM` 로 메트릭·추적과 통일. 테스트 11종)
- [x] 핵심 메트릭 정의 — 커넥션 수, RPS, 레이턴시 히스토그램, 큐 깊이, 풀 사용률, 오류율 (이름 계약 `MetricNames`/`TagNames` 는 2026-08-04 확정. 2026-08-06 `IMetricsSink` 배선: 커넥션 수립·활성·디스패치 지연 히스토그램·처리량·실패가 데코레이터로 실물. 2026-08-07 `5c9059b`: 파티션 백프레셔 관측 배선 — `PartitionWorkRejected`(포화 거부)·`PartitionQueueDepth`(게이지)가 `ExecutionPartition` 에서 방출(TryPost 프로덕션 호출자는 후속). 2026-08-07 `5a89ed6`(ADR-0025): **바이트·풀 완료** — `BytesReceived`/`BytesSent` 는 전송이 소켓 경계에서 push(회선 기준. 회선 없는 인메모리는 내지 않음 — 계약 명시), 풀 3종(`pool.buffers.rented`/`returned`/`leaked`)은 `IMetricsSink.ObserveCounter` pull 로 핫패스 비용 0. **남은 것: `FramesSent`** — `FrameWriter` 가 static 확장이라 싱크 주입 지점이 없다(별도 API 판단))
- [x] 분산 트레이싱 — 메시지 흐름 상관관계(correlation ID) 전파 (2026-08-07 완료: `05a5ca6` 디스패치 span + fast-path(ADR-0022, 리스너 없음 8ns/0B) → `1417941` 커넥션 span + **크로스 스레드 부모 전파**. `TracingConnectionHandler` 가 커넥션 span 의 `ActivityContext` 를 `ConnectionTraceFeature`(커넥션 기능)에 실어, 파티션 스레드에서 도는 디스패치 span 이 명시적 부모로 읽는다(`Activity.Current` 는 그 스레드로 안 흐름). 실행 모델 e2e 로 자식 링크 고정. 전 생애 부모 span — 볼륨은 head 샘플링으로 조절)
- [x] 헬스체크 / 라이브 진단 엔드포인트 — liveness / readiness 구분 (2026-08-07 완료: `a1c581d` 헬스 생산·집계 + 프로그래밍 API(ADR-0023) → `3880020` HTTP 엔드포인트(ADR-0024). Core `IHealthCheck`/`HealthStatus`/`HealthReport`/`HealthProbe` + `HealthCheckService`(최악 우선 집계·항목별 try/catch) + 내장 readiness(생명주기 `ServerLifecycleState`, `UnbindAsync`→Draining=not-ready)·liveness(`PartitionedExecutionModel` 이 `IHealthCheck` 구현). `ChServerM.Diagnostics.Http.HealthHttpEndpoint`(HttpListener, `/healthz`·`/readyz`, 200/503, Core 만 참조·프로브 델리게이트)로 k8s 프로브가 직접 사용)
- [x] 런타임 진단 — 커넥션 덤프, 스레드 상태, 풀 상태를 운영 중에 조회 (2026-08-08 완료: Core `IDiagnosticsSource`/`IDiagnosticsWriter`(옵트인, ADR-0023 규율) + `DiagnosticsService`(구역별 try/catch — 장애 중에 한 구역이 던져 전체를 못 보면 진단이 장애를 키운다) + 전송·실행 모델 자동 등록 + `BufferPoolDiagnosticsSource`(Observability, Buffers 무의존 유지). **메트릭과 겹치지 않는다** — 메트릭은 카디널리티 규약상 커넥션 ID·주소를 담을 수 없고, 진단은 그 여집합(요청 시점 고카디널리티 스냅샷)이다. **⚠ 커넥션은 집계+상한 표본(20개, 가장 오래 조용한 순)** — 전체 덤프는 1만 접속에서 MB 급 응답이고 무인증 admin 엔드포인트에 클라이언트 주소를 통째로 노출한다. HTTP 노출은 `DiagnosticsPath` **옵트인**(기본 미노출). 테스트 8종)
- [x] `EventSource` / `DiagnosticSource` — `dotnet-counters`/`dotnet-trace` 연동 (2026-08-06: `MeterMetricsSink` 가 `System.Diagnostics.Metrics.Meter` 를 쓰므로 `dotnet-counters` 가 별도 작업 없이 메트릭을 읽는다. 2026-08-07 `05a5ca6`: `TracingMiddleware` 가 `ActivitySource`("ChServerM")로 span 을 내므로 `dotnet-trace` 도 별도 작업 없이 트레이스를 읽는다)
- [x] 로그 레벨 런타임 변경 — 재시작 없이 디버그 로그 활성화 (2026-08-08 완료 — **프레임워크 코드 0**. MEL 어댑터(ADR-0030)를 얹은 순간 이미 성립한다: 어댑터가 `IsEnabled` 를 **호출마다** MEL 로 위임하고(캐시하지 않는다) MEL 은 필터 옵션이 바뀌면 규칙을 재구성하므로, 호스트가 `LoggerFilterOptions` 를 바꾸면 프레임워크 로그가 즉시 따라온다. **가정하지 않고 실제 `LoggerFactory` 로 검증**: 필터 변경 반영·어댑터가 캐시하지 않음(질의 3회 = 위임 3회)·범주(`ChServerM`)로 프레임워크 로그만 선택적으로 열기. 테스트 3종)
- [x] ⚠ **관측 오버헤드 측정** — 메트릭·트레이싱 데코레이터가 핫패스에 미치는 비용. 켠 상태와 끈 상태를 모두 벤치마크. 관측이 성능을 먹으면 프로덕션에서 꺼지고, 꺼진 관측은 없는 것과 같다 (2026-08-06 완료 `bf99ebf`: 메트릭 4변형 실측 — 켠 비용 ~72ns/프레임·**전 변형 할당 0**, 끈 관측(NullMetricsSink) 6ns=기준선. 비용 대부분은 Meter 가 아니라 미들웨어 async 래퍼. 2026-08-07 `05a5ca6`: 트레이싱 3변형 실측 — fast-path(리스너 없음) 8ns/0B 로 그 async 래퍼 비용을 회피, span 생성은 170ns/560B(관측될 때만). BENCHMARKS.md 관측·추적 절)

**게이트**: 관측을 켠 상태의 오버헤드가 측정·기록되고 허용 범위 안일 때.
(2026-08-06 **충족** — 켠 오버헤드 ~72ns/프레임·할당 0 실측 기록, 끈 상태 6ns. 메트릭 축 첫 증분으로 게이트 통과. 트레이싱·헬스체크·ZLogger 등 나머지 항목은 게이트 조건과 무관하게 후속 증분)

## Phase 12 — 성능 검증 & 회귀 방어

지금까지의 벤치마크를 **회귀 방어 장치로** 승격시킨다. 측정만 하고 지키지 않으면 성능은 반드시 퇴화한다.

- [x] 성능 목표 확정 — `docs/BENCHMARKS.md`의 가설 표를 실측 기준선으로 대체 (2026-08-08 완료: 처리량 169,180 RPS·1만 접속·커넥션당 ~8KB·프레임당 할당 0·라우팅 0.69ns·**코어 확장 14.67×(91.7%, 머신 천장의 99.0%)**·관측 오버헤드. 각 항목에 환경 ID 와 근거 기록을 붙였다. **회귀 방어 상태 표**도 함께 — 할당은 상시 게이트, 처리량·확장성은 미구현임을 명시)
- [x] ⚠ **CI 벤치마크 회귀 게이트** — ~~기준선 대비 N% 이상 퇴화 시 빌드 실패~~ → **비율 게이트**로 재설계 (2026-08-08, ADR-0032, `eng/bench-gate.ps1`). **원안대로 만들면 동작하지 않는다**: 공용 러너는 절대 시간이 20~30% 흔들려 임계가 좁으면 플래키하고 넓으면 아무것도 못 잡는다. 대신 **같은 실행 안 두 팔의 비율**을 고정한다 — 노이즈가 분자·분모에 함께 실려 상쇄된다. 설계 주장 6종(코드 생성 라우팅 우위·리플렉션 대비 우위·추적 fast-path·풀링 우위·부분 수신 조기 반환·MemoryPack 우위), 각 항목이 실패 시 **무너진 주장**을 출력한다. **★ 고의 회귀 검증에서 처음엔 게이트가 놓쳤다** — 임계를 노이즈 여유로 잡았는데 실제 회귀(2.6배)가 그 여유(3배)보다 작았다. 임계를 **회귀 실측에서 역산**하도록 고쳐 재검증. **못 잡는 것 명시**: 두 팔이 함께 느려지는 회귀
- [x] 할당 회귀 게이트 — 핫패스 메서드의 할당량 0을 테스트로 고정 (2026-08-08 완료 `DispatchAllocationGateTests`: 그동안 코덱 수준에만 있던 할당 고정을 **조립된 디스패치 파이프라인**으로 확장 — 미들웨어가 쌓이는 그 경로가 정작 무방비였다. 추적 fast-path·열화 fast-path·열화 거부 경로·속도 제한(커넥션당 버킷 재사용)·**셋을 합성한 실제 조립 형태** 전부 프레임당 0. **★ 게이트가 고의 회귀를 실제로 잡는지까지 테스트로 고정**(프레임당 할당하는 미들웨어를 넣으면 실패해야 한다) — Phase 12 게이트 조건 그 자체. **할당을 첫 게이트로 고른 이유**: 시간 기반은 개발 머신에서 노이즈가 크지만 할당은 결정적이라 첫날부터 신뢰할 수 있다)
- [x] ⚠ **확장성 회귀 게이트** — `eng/scaling-gate.ps1` (2026-08-08, ADR-0032). **빌드 실패가 아니라 수동 게이트로 두는 것이 결정 사항이다**: GitHub 공용 러너는 2~4 vCPU 라 곡선이 2점뿐이고 물리 코어와 SMT 형제를 구분할 수도 없다 — 거기서 도는 게이트는 의미 없는 통과를 반복해 게이트를 장식으로 만든다. 측정 머신에서 릴리스 전·동시성 코드 변경 후 사람이 돌린다. 하한은 **ADR-0005 가 스스로 단 무효 조건(효율 70%)과 일치** — 실패는 '느려졌다'가 아니라 '키 기반 샤딩 전제가 무효다'를 뜻한다 (진행 중: 전체 5지점 곡선 재실행은 미수행 — 기준선은 2026-08-07 측정이 근거)
- [x] 종단 부하 테스트 (~~NBomber~~ → **LoadRunner `spike` 모드**) — 현실적 시나리오, 램프업/스파이크/지속 (2026-08-08). **ADR-0009 를 재검토했고 결론은 유지**: 커스텀 프레이밍이라 NBomber 를 넣어도 클라이언트 코드는 그대로 써야 하고, 실제 공백은 도구가 아니라 **시나리오 모양**이었다(램프업·지속은 이미 있었고 **스파이크가 없었다**). 기준 커넥션 256 을 유지한 채 신규 접속 3,000 을 램프업 없이 얹고 워밍업/기준/스파이크/회복 4구간으로 측정. **수용 제어가 기존 손님 p99 를 41% 지킨다**(27.20→16.00ms, 거부 1,975). **★ 두 결함을 발견해 고쳤다**: ① **TCP 연결 성공은 수용이 아니다** — 거부는 accept 이후라 클라이언트 connect 는 성공한다. 왕복 기반 판정으로 고치기 전까지 방어를 켜고도 '거부 0' 이었다 ② **워밍업 없으면 기준 구간이 JIT 승격 중에 측정**되어 스파이크가 30% *빠르게* 나온다 (미측정: 주소별 제한 — 루프백은 한 주소라 다중 호스트 생성기 필요)
- [x] LoadRunner 램프업 무한 루프 수정 — 대상 서버가 없거나 도중에 죽으면 연결 실패만 세며 영원히 돈다(2026-08-05 발견, 고아 프로세스 유발). 연속 실패 임계 초과 시 중단으로 바꾼다 (2026-08-08 완료: `while (all.Count < connections)` 는 전부 실패하면 진전이 없어 무한히 돌았고, **루프 뒤의 "연결이 하나도 성립하지 않았다" 검사가 도달조차 못 했다.** 판정 기준은 **실패 수가 아니라 진전 여부** — 부하 시험에서 일부 실패는 정상 결과(그 수가 곧 측정값)이므로 실패를 세어 끊으면 멀쩡한 시험을 중단시킨다. 한 라운드에서 **단 하나도 못 붙는 상태가 3회 연속**이면 대상이 없다는 뜻이다. **실측 검증**: 死 포트 대상 → 9초 만에 사유 출력 후 정상 실패 종료(수정 전이면 무한). **대조군**: 실제 서버 대상 64/64·실패 0·171,298 RPS 로 정상 동작 무영향)
- [x] 프로파일링 워크플로 문서화 — `docs/PROFILING.md` (2026-08-09). **모든 절차와 수치를 실제로 실행해 얻었다** — 추측으로 쓴 단계가 없다. dotnet-trace/counters/gcdump 설치·수집·해석. **실전 함정 명시**: `cpu-sampling` 은 Linux 전용(Windows 는 `dotnet-sampled-thread-time`) / 스택 리프가 의사 프레임(`CPU_TIME`·`UNMANAGED_CODE_TIME`)이라 그대로 세면 두 줄만 나온다 / 기동 직후 수집은 JIT 을 프로파일하는 것 / 셸 백그라운드 PID ≠ 대상 PID. **★ 워크드 예제가 이 문서의 핵심**: 프로파일 1위였던 `ExecutionPartition.WaitForWork()` 25.32% 를 A/B 해보니 **없애면 RPS −4.7%·p99 +8%** 로 나빠져 기각했다 — 프로파일은 가설을 만들 뿐 결론을 만들지 않는다. **'고치지 않기로 한 결정' 도 기록한다**(안 남기면 다음 사람이 같은 시간을 쓴다)
- [x] GC 튜닝 검증 — ServerGC / DATAS 비교 (ADR-0031, 2026-08-08). **⚠ 이 항목이 설정 결함을 드러냈다**: `ServerGarbageCollector` 오타로 3개월간 Workstation GC 로 돌았다. 처리량은 GC 모드에 ±2% 이내로 둔감(= 무할당 설계의 증거), DATAS 는 p99 +46~85% 악화, DATAS 끈 ServerGC 는 워킹셋 10배 (진행 중: 전용 머신 재측정 — 루프백은 ServerGC 에 구조적으로 불리하다)
- [x] Native AOT vs JIT 성능·기동시간 비교 (2026-08-08 완료(ENV-B): 같은 샘플의 두 발행판, 각 7회 중위값. **기동 포함 실행 62.2ms vs 172.7ms(2.8× 빠름)** · **최대 워킹셋 11.4MB vs 31.8MB(2.8× 작음)** · 배포 3.22MB 단일 파일 vs 0.64MB×12파일+런타임 설치 필요. **CLAUDE.md 2절이 리플렉션을 금지하고 소스 제너레이터를 강제한 근거("AOT 호환성과 콜드스타트")가 처음으로 수치가 됐다** — 110ms 차이는 런타임 초기화+JIT 워밍업이고, 프로세스가 자주 뜨는 배포에서 그대로 응답 지연이 된다. **한계**: 정상 상태 처리량은 미측정(짧은 워크로드라 TieredPGO 가 돌 시간이 없다 — 장시간은 JIT 이 따라잡을 수 있다. AOT 발행 장수명 부하 서버가 필요한데 LoadRunner 는 Kestrel 프로토타입 포함이라 AOT 호환 미확인) · win-x64 단일 플랫폼(리눅스 컨테이너 재확인 필요))
- [x] 경쟁 프레임워크 비교 측정 — **raw Kestrel 바닥선** (2026-08-09, `LoadRunner raw` 모드 + `RawKestrelEchoServer`). 같은 소켓 엔진 위에서 프레임워크를 전부 걷어낸 최소 에코와 대결해 **조립 가능성·순서 보장·관측성의 가격표**를 잰다. **비교를 의도적으로 바닥선에 유리하게 기울여** 나온 값이 세금의 *상한*이 되게 했다(바닥선은 바이트를 그대로 되쓰고, 검증이 길이 하나뿐이고, 운영 기능이 없다). **결과: 세금은 단일 숫자가 아니다** — 저부하 지연 바닥에서 RPS −13.5%·p99 +38%(프레임당 고정 비용이 드러난다. p50 차 11µs 는 CPU 가 아니라 **파티션 인계 지연**), 512 활성 −1.1%, 10k+256 −3.3%. **★ 고동시성에서는 ChServerM 의 p99 가 오히려 34% 더 좋다**(3.78 vs 5.75ms, 2회 재현) — raw 는 커넥션마다 스레드풀에 올려 스케줄링 편차가 꼬리에 실리고, ChServerM 은 키별 전용 스레드+유계 큐라 꼬리가 안정된다. **ADR-0005 파티션 모델이 처리량이 아니라 꼬리 지연에서 값을 낸다는 첫 증거** (미측정: SuperSocket 등 동종 프레임워크 대결)

**게이트**: 회귀 게이트가 의도적 성능 퇴화를 실제로 잡을 때.

---

# Part IV — 상태 & 확장

## Phase 13 — 세션 & 영속화

- [x] **Core 계약 `ISessionStore`** — ⚠ **원래 목록에 없던 선행 항목이다.** 축 추가 순서는 Core 인터페이스 → 참조 구현 1개 → 벤치마크 → 두 번째 구현(CLAUDE.md 3절)인데 Phase 13 은 어댑터부터 시작하고 있었다 (2026-08-09, ADR-0033). **바이트 계약 + 버전(CAS) + 만료(TTL)**. 값을 제네릭 타입으로 두면 인메모리=참조 / 원격=사본 으로 **의미가 갈려 같은 핸들러 코드가 저장소마다 다르게 동작**한다(ADR-0004 위반) → 바이트로 양쪽 모두 값 의미. CAS·TTL 은 `PublicAPI.Shipped` 로 굳은 뒤엔 못 넣으므로 v1 에 둔다. ABA 방지를 계약으로 명시(버전은 같은 키에 재사용 금지)
- [x] `ChServerM.Persistence.InMemory` — 기본 구현 (2026-08-09, 참조 구현). 값 의미(복사)·CAS·만료를 모두 지킨다. **지연 만료 + 주기적 청소** — 지연 판정만으로는 *다시 조회되지 않는 세션*(= 끊긴 클라이언트)이 영원히 남아 OOM 벡터가 된다. 타이머는 저장소당 하나(9.5). 저장 배열은 풀 대여가 아니라 정확한 크기(장기 보유는 풀 고갈 + 반납 지점 4곳 분산 = 누락 확정). 테스트 24종이 **축의 합격 기준** — Redis 어댑터가 같은 단언을 통과해야 한다
- [x] `ChServerM.Persistence.Redis` (StackExchange.Redis) — 2026-08-10, ADR-0034. **두 번째 구현이 나와야 추상화가 가설을 벗어난다**(CLAUDE.md 3절). 버전을 **값 안에**(`[8B 버전][상태]`) 넣어 상태·버전이 원자적으로 함께 바뀌게 하고, CAS 는 **Lua 스크립트**로 한다(`WATCH/MULTI/EXEC` 는 커넥션에 묶이는데 멀티플렉서는 다중화한다). 버전 발급도 서버가 `INCR` — 클라이언트가 만들면 다중 노드에서 충돌한다. 만료는 `SET PX`/`PEXPIRE` 로 **서버가 회수**(인메모리는 직접 청소했다 — 같은 계약을 각자의 네이티브 수단으로 만족시키는 것이 축의 요점)
- [x] ⭐ **적합성 스위트** — `Tests/ChServerM.Persistence.Conformance` (2026-08-10). 계약 단언 21종을 추상 클래스에 두고 각 어댑터가 **상속만 한다**. 어댑터마다 자기 테스트를 쓰면 각자 자기 구현대로 단언하게 되어 **축 교체 시 동작 차이를 아무도 못 잡는다** — ADR-0004 의 요구를 실행 가능한 형태로 만든 것. 시간만 추상화하고(인메모리=가짜 시계 / Redis=실제 대기) **단언은 완전히 동일**하다. **Redis 어댑터가 첫 실행에 21종 전부 통과** = 계약이 구현에 기대지 않고 그어졌다는 증거. Docker 없으면 **건너뛰되 사유를 남긴다**(조용히 통과하면 "검증됐다" 는 착각을 준다)
- [x] 로컬 KV 검토 (Tsavorite / Garnet) — **문헌 조사가 아니라 실행으로 답했다** (2026-08-10, ADR-0038). **Garnet 은 `RedisSessionStore` 가 코드 한 줄 없이 그대로 동작한다** — 적합성 21종 전부 통과(`GarnetSessionStoreConformanceTests` 는 대상만 바꾸고 단언은 상속). 저장소 교체에 필요한 것이 **연결 문자열뿐**이라는 것이 축이 제대로 잘렸다는 가장 값싼 증거다. **⚠ 발견: Garnet 은 Lua 를 기본 비활성으로 띄운다** — `--lua` 없이 뜨면 **쓰기는 전부 실패하는데 읽기(`GET`)는 통과**해 *부분적으로만 동작하는* 가장 헷갈리는 상태가 된다. 이 플래그가 곧 운영 요구사항이며, **문헌 조사만 했다면 놓쳤을 것**이다. **Tsavorite 는 보류** — 인프로세스 영속 KV 로 성격이 또 다르지만 **요구하는 배포 시나리오가 없다**(ADR-0027 의 "대상 없는 추상화는 만들지 않는다" 규율). 만들 조건은 ADR 에 명시. 검증은 **상시 테스트로 남겼다**(일회성이면 호환성이 깨져도 모른다)
- [x] **`ChServerM.Persistence.Postgres` (Npgsql)** — 관계형 어댑터 (2026-08-10, ADR-0037). ~~MongoDB 어댑터 검토~~ → **PostgreSQL 로 결정**(사용자 지시). 레거시가 MongoDB 를 쓴 것은 사실이지만 승계 대상이 아니다. **축의 세 번째 구현**이며 성격이 가장 다르므로(참조 / 원격 KV / 관계형) 교체 가능성의 가장 강한 증거다 — **적합성 21종을 첫 실행에 전부 통과**. CAS 는 조건부 `UPDATE`(영향 행 0 = 충돌) — Redis 가 Lua 로 묶어야 했던 것을 관계형은 공짜로 준다. 버전은 전역 `SEQUENCE`(행별이면 삭제 시 사라져 ABA). **⚠ 네이티브 TTL 이 없어 지연 판정 + 주기적 청소**(인메모리와 같은 모양) · 청소에 **배치 상한**(만료 행 수백만에서 무제한 DELETE 는 긴 잠금으로 서비스를 막는다) · 시간 기준은 **DB 서버 `now()`**(앱 시계면 노드마다 다른 답) · **스키마를 자동 생성하지 않는다**(운영자가 변경 시점을 통제해야 한다) · 식별자는 **화이트리스트로 거부**(SQL 에 직접 삽입되므로 이스케이프보다 거부가 안전). 테스트 33종

> ~~MongoDB 어댑터~~ — **채택하지 않는다**(ADR-0037, 사용자 지시). 레거시가 MongoDB 를
> 쓴 것은 사실이지만 레거시 트리는 참조 전용이라 새 어댑터의 선택을 구속하지 않는다.
> **하지 않기로 한 일은 체크박스로 두지 않는다** — 영원히 미완료로 남아 진행률을 왜곡한다.

- [x] ⚠ 세션 복구 / 재접속 — `SessionResumeService` (2026-08-10, ADR-0036). **재개 자격은 서버 발급 토큰**이다 — `SessionId` 는 로그·진단에 등장하고 열거 가능하므로 자격으로 쓰면 **ID 를 아는 사람이 곧 주인**이 된다. 보안 규약 4종을 테스트로 고정: 저장소엔 **해시만** / **쓸 때마다 회전**(탈취 토큰은 1회용이고 늦게 온 쪽이 실패하므로 **탈취가 드러난다**) / **실패 사유 미구분**(구분하면 실재 SessionId 열거 가능) / 상수 시간 비교. **⭐ 좀비 차단은 CAS 에 얹어 새 개념이 0**: 재개가 토큰 회전이라는 쓰기를 유발해 버전이 올라가고 옛 커넥션 버전이 자동 무효화된다 — ADR-0033 이 CAS 를 v1 에 넣은 이유가 이 경로다(다중 노드에서도 성립. 능동 종료는 옛 커넥션이 다른 노드면 닿지 않는다). 저장 값은 `[1B 형식][32B 해시][앱 상태]` 봉투 — 키를 둘로 나누면 회전과 상태 갱신이 원자적으로 함께 일어나지 않는다. **⚠ 토큰 타입을 Core 에 뒀다가 `CoreDependencyTests` 가 잡았다**(암호 연산 수행 = 추상화 아님) → Hosting 으로 이동. 테스트 15종 (2026-08-10 후속: **와이어 프로토콜 완료** — 예약 ID 40007~40009 + Core 동결 코덱 `SessionHandshakeCodec` + Core `ISessionFeature`(커넥션↔세션 바인딩) + Hosting `SessionResumeDispatch`. **재개는 프레임워크가, 수립은 앱이 한다** — 재개는 '제시된 토큰이 해시와 맞는가' 라는 기계적 판정뿐이라 정책이 없고, 수립은 '이 사람에게 세션을 줘도 되는가' 라는 정책이라 앱의 몫이다. 이 경계를 흐리면 인증 정책이 프레임워크로 새어 ADR-0004 가 깨진다. **성공·실패 응답의 바이트가 완전히 동일**함을 테스트로 고정(길이 차이도 부수 채널이다). 형식 오류도 응답한다 — 끊기만 하면 클라이언트가 '거부' 와 '네트워크 장애' 를 구분 못 해 재시도 정책을 세울 수 없다. 테스트 16종. 2026-08-10 후속 2: **`ServerBuilder.UseSessions` 배선 완료** — 앱이 예약 ID 40007 을 알 필요가 없다. **서비스를 받고 만들지는 않는다**: 저장소·만료·서킷 브레이커 조합은 앱이 정해야 축 교체 가능성이 유지된다. 실제 서버·실제 소켓 종단 테스트 3종. **정정**: `IAuthenticator` 는 **이미 배선돼 있었다**(`Dispatch/AuthenticationMiddleware` + `MessageDispatcherBuilder` 의 미들웨어 순서 강제) — 앞선 기록이 `Hosting/*.cs` 최상위만 확인해 잘못 적었다)
- [x] 일관성 모델 명시 — `docs/CONSISTENCY.md` (2026-08-10). **여기 적히지 않은 것은 보장되지 않는다**를 원칙으로, 보장/미보장을 한 표에 모았다: 세션 **한 키**에 대해 선형화 가능(단일 마스터 기준) / **여러 키에 걸친 원자성은 보장 없음** / 만료 판정은 즉시·회수는 지연 / 좀비 차단은 **다중 노드에서도 성립**(단일 키 CAS 위에 세웠으므로) / 서킷 브레이커·인메모리 저장소·속도 제한은 **노드 로컬**. 앱이 지켜야 할 읽고-고치고-쓰기 규율(**충돌 후 반드시 다시 읽는다** — 옛 값 위의 재시도는 남의 변경을 덮는다)도 함께.
- [x] ⚠ **Redis Cluster 미지원 해소** — **전역 카운터를 버리고 쓰기마다 클라이언트 발급 64비트 난수 버전으로** (2026-08-11, ADR-0058). 쓰기 Lua 가 키를 둘(세션 + 전역 버전 카운터) 만져 클러스터에서 `CROSSSLOT` 으로 전멸하던 것을, 모든 스크립트가 **정확히 키 하나**만 만지게 바꿔 구조적으로 해소했다. 부수로 **모든 쓰기가 경쟁하던 전역 `INCR` 핫 키도 소멸**(9.1). 계약 1(쓰기마다 다른 값)은 기대-버전 배제로 **결정적**, 계약 2(재사용 금지)는 확률적(2⁻⁶⁴, CONSISTENCY 4절에 명시 — 절대 보장이 필요하면 카운터 기반 저장소를 고른다). ⭐ **클러스터 모드(슬롯 검사 활성) 컨테이너 픽스처를 신설**해 적합성 21종이 그 위에서 통과 — 초판 스크립트라면 전멸하는 상시 회귀 게이트다. 탈락: 해시 태그+세션별 영구 카운터(만들어진 적 있는 세션마다 영구 키가 남는 무계 상태). `VersionCounterKey` 옵션 제거(Unshipped 라 비용 0), 기존 데이터와 호환(값 배치 동일·비교는 등가뿐)
- [x] 캐시 무효화 전략 — **캐시 계층을 만들지 않는 것이 답이다** (2026-08-10, ADR-0039). **먼저 측정했다**: 원격 세션 읽기가 Redis 452µs / PostgreSQL 565µs 로 인메모리(10ns) 대비 **4~5 자릿수**이고, 이 프레임워크의 **에코 왕복 전체(p50 104µs)보다 4~5배** 비싸다 — 메시지마다 원격을 읽는 구성은 성립하지 않으므로 **캐시는 선택이 아니라 전제**다. **그런데 데코레이터를 만들지 않는다**: 앱의 작업본은 바이트가 아니라 역직렬화된 객체라 프레임워크가 바이트를 캐시해도 **역직렬화가 남고**, 타입을 알게 만들면 ADR-0033 이 거부한 "의미가 갈리는" 문제가 돌아온다. 커넥션이 이미 세션의 소유자이므로 **작업본의 수명 = 커넥션의 수명**이고 만료·축출 정책이 필요 없다. **⭐ 무효화하지 않고 충돌로 감지한다** — 세션을 바꿀 수 있는 주체는 소유 커넥션뿐이라 다른 주체의 변경은 곧 재개이고, 그 사실은 **다음 쓰기의 `Conflict`** 로 정확히·놓칠 수 없게 전달된다(CAS 를 v1 에 넣은 값의 **세 번째 회수**). **⚠ 한계를 계약으로 명시**: 읽기만 하는 경로는 무한히 낡을 수 있으므로 최신이어야 하면 그때만 원격을 읽는다 — "얼마나 낡아도 되는가" 는 도메인 질문이라 프레임워크가 대신 정하지 않는다
- [x] 외부 저장소 장애 시 동작 — **서킷 브레이커**(ADR-0035, 2026-08-10). **ADR-0027 의 보류를 해제했다** — Redis 세션 저장소가 첫 실물 대상이 되면서 조건이 충족됐고, **보류가 옳았음이 드러났다**(실물이 없었다면 아래 분류를 근거 없이 정했을 것이고 CAS 충돌이라는 개념 자체가 세션 계약 전에는 없었다). Core `ICircuitBreaker`/`CircuitState`/`CircuitOpenException` + Hosting `CircuitBreaker`(무락) + `CircuitBreakingSessionStore`(데코레이터). **⚠⚠ 설계의 본체는 상태 기계가 아니라 "무엇을 실패로 세지 않는가"**: CAS 충돌·NotFound 는 저장소가 정상적으로 '아니오' 라고 답한 것이므로 실패가 아니다(세면 **경합이 곧 차단** — 부하를 견디라고 만든 장치가 부하 때문에 서비스를 끊는다). 호출자 버그(`ArgumentException`)·취소도 아니다(잘못된 코드 한 줄이 저장소를 차단시킨다). **실제 StackExchange.Redis 예외가 인프라 장애로 분류되는지 테스트로 고정**(스텁만으로는 증명 불가 — 분류가 틀리면 장애 때 알게 된다). 열림 시 예외를 던진다(`NotFound` 로 접으면 호출자가 새 세션을 만들어 **사용자 상태 유실**). 테스트 19종
- [x] 커넥션 풀 관리 — **추상화하지 않는다. 풀 자체가 Bulkhead 다** (2026-08-10, ADR-0040). **두 벤더의 모델이 정반대**라 공통 추상화는 둘 중 하나에 대해 거짓말이 된다(Npgsql=진짜 풀 / StackExchange.Redis=멀티플렉서, 풀 크기 개념 없음·앱당 하나 공유). **실측으로 확인**: 풀을 1개로 조여 고갈시키면 **매달리지 않고 `Timeout` 안에 실패**하고, 서킷 브레이커는 그것을 **인프라 장애로 세어 회로를 연다**. 대조군으로 풀을 동시성만큼 주면 같은 부하가 통과 — **저장소의 문제가 아니라 사이징의 문제**다. ⭐ **Phase 10 의 Bulkhead 항목이 저장소 축에 대해서는 이미 충족돼 있다** — 풀이 동시 실행 상한이므로 앞에 세마포어를 두면 같은 일을 두 번 하고 진단만 어려워진다(Redis 는 모델이 달라 `SyncTimeout`+브레이커가 그 역할). ⚠ 풀 고갈이 회로를 여는 것을 **그대로 뒀다** — 대응 방향은 옳고, 정교한 분류는 브레이커를 벤더에 의존하게 만든다(필요하면 어댑터가 술어를 넘긴다, ADR-0035). 대신 **"DB 가 느린가 / 풀이 작은가" 가 같은 증상을 낸다는 사실**을 문서와 테스트로 못 박았다. 사이징 지침 포함. 테스트 3종
- [x] 벤치마크: 세션 조회·갱신 레이턴시 (2026-08-10, `SessionStoreBenchmarks`). 기준선은 맨 `ConcurrentDictionary` — 계약을 걷어낸 형태라 차이가 곧 **값 의미·CAS·만료의 가격**이다. 읽기 24~32ns **무할당**, 미적중 1.8ns, 쓰기 성공 62~136ns(사본+항목). **★ 벤치마크가 구현 결함 2건을 잡았다**: ① 거부 경로가 버전 검사 **전에** 상태를 복사해 버리고 있었다(1KB에서 1,048B) — 경합이 심할수록 충돌이 느니 **정확히 부하가 높을 때** GC 압력이 커진다. 고치니 **−62%, 할당 0, 크기 독립**(43.2 vs 43.7ns) ② `TryRenew` 가 항목을 새로 만들어 40B 할당 — 만료를 미루자고 객체를 만드는 건 이 메서드의 존재 이유와 모순이다. 제자리 원자적 갱신으로 −21%·할당 0. **둘 다 테스트로 고정** (미측정: 경합 아래 확장성, 원격 저장소 대비)

## Phase 14 — 데이터 테이블 & 설정 (선택 축)

정적 데이터 테이블을 로드해 서비스하는 서버는 흔하다 — 게임 밸런스 테이블, 요금표,
룰 엔진 설정, 피처 플래그. `ChServerM.DataTable.*`로 분리한다. 레거시가 상당한 자산을 갖고 있다.

- [x] 정적 데이터 테이블 로딩 — `ChServerM.DataTable` (2026-08-10, ADR-0041). 레거시 판정은 `docs/legacy/11-data-table` 이 이미 끝내 뒀고 그대로 따랐다. **선택 축이라 Core 를 참조하지 않는다** — 참조를 만들지 않는 것 자체가 "전부 빼도 프레임워크가 성립한다" 의 검증이다. 런타임 형식은 **CSV**(사람이 읽고 `git diff` 가 되어야 밸런스 표를 리뷰할 수 있다. 레거시는 Excel/ODBC 파서 3,093줄을 런타임에 넣고 **한 번도 호출하지 않았다**). ⭐ **검증은 로딩 시점에, 오류는 줄 번호와 함께 한 번에 전부** — 레거시는 검증이 없어 잘못된 값이 첫 조회에서 예외가 되거나 조용히 기본값이 됐다. 첫 오류에서 멈추면 "고치고 다시 띄우기" 를 오류 수만큼 반복한다. 키 중복도 실패(조용히 넘기면 **나중 행이 이기고 아무도 모른다**). 값은 로딩 때 한 번 파싱해 열 종류별 배열에 담고(박싱 0) 조회는 **서수 기반**(문자열 키를 핫패스에 두지 않는다). 파싱은 **컬처 불변** — 프레임워크는 라이브러리라 쓰는 앱이 invariant 가 아닐 수 있다. 이름은 `System.Data` 충돌을 피해 `StaticTable*`. 테스트 21종
- [x] ⭐ 강타입 접근자 소스 제너레이터 — `[StaticTableRow]` (2026-08-10, ADR-0043). **서수는 문자열 키를 고치다 만든 함정이다** — 열을 가운데에 하나 끼워 넣으면 뒤따르는 모든 `GetInt32(row, 3)` 이 조용히 다른 열을 읽는다. 컴파일도 되고 예외도 안 나고 **밸런스 값만 틀린다**(문자열 키는 최소한 터지기라도 했다). **⭐ 진단으로는 못 잡으므로 스키마와 접근자를 같은 선언에서 함께 생성한다** — 열 순서와 서수가 같은 입력에서 나오므로 어긋날 경로가 없다. 뷰는 **스키마 참조 동일성**을 확인한다(구조만 같은 스키마를 받으면 "같은 선언에서 나왔다" 는 근거가 사라진다). 참조 대상은 `typeof(RowType)` — 오타가 컴파일 오류가 되고 표 이름을 두 군데 적지 않는다. 범위 "설정 안 함" 은 **명명 인자의 부재**로 판단(센티넬을 두면 "설정한 0" 과 섞인다). 진단 CHSM2001~2010(2009 결번). 테스트 39종 추가(드라이버 20 + **실제 빌드가 생성한 코드를 돌리는** 종단 19), 읽기 경로 할당 0
- [x] CSV 검증을 컴파일 타임으로 — 헤더 대조 + 스키마 레지스트리 (2026-08-10, ADR-0046). **⚠ 계획했던 검증 CLI 는 설계 중에 무너졌다** — CLI 가 스키마를 알려면 앱 어셈블리를 로드해야 하는데(스키마가 생성된 C# 안에 있으므로), 그 복잡도로 얻는 것은 이미 전부 있다: 라이브옵스는 `TryReload` 가 막고(ADR-0042), CI 는 **테스트 한 줄**이 막는다. **레거시가 쓰지 않을 Excel 파서 3,093줄을 쓴 것을 지적해 놓고 같은 실수를 할 뻔했다.** ⭐ **중복 없는 값은 컴파일 타임 헤더 대조뿐**이다(에디터에서·줄 단위로·빌드 실패로 — 테스트도 CLI 도 못 한다). 설정을 요구하지 않는다: `AdditionalFiles` 의 `.csv` 중 **파일 이름이 표 이름과 같은 것**만 본다. **⚠ 값 검증은 옮기지 않는다** — 파서·검증기를 제너레이터 쪽에 복제하면 두 정본이 갈라져 **빌드 통과 + 기동 실패**가 가능해진다. 헤더 규칙 30줄만 중복하고 일치는 테스트가 지킨다. 곁들여 `GeneratedStaticTableSchemas.All`(스키마 손 목록 제거 — 표 추가하고 목록에 넣는 것을 잊는 사고는 서수를 손으로 적던 것과 같은 종류다). 진단 CHSM2011~2013. 테스트 11종, **실제 빌드에서 양방향 확인**(정상 CSV 는 깨끗, 열 이름 하나 고치면 `Item.csv(4,1): error CHSM2011`)
- [ ] Excel → CSV 변환 도구 — **수요가 확인될 때 만든다.** 레거시 `ExcelLibM`(2166) + `ExcelODBCM`(927) + `CsvParser`(182)는 **전부 참조 0**이었고, 그것은 "위치가 틀렸다"가 아니라 **"수요가 없었다"**는 증거일 수 있다. 지금 잘 만들어 옮기면 위치만 바꿔 같은 실수를 반복한다. 실제 요청이 오면 CLI 에 `import` 를 더한다 — 구조(스키마·검증·스냅샷)는 이미 서 있다 ([11-data-table](legacy/11-data-table.md#-미사용-코드-3359줄))
- [x] 테이블 검증 — 참조 무결성·범위 검사를 로딩 시점에 (2026-08-10, ADR-0041 후속). **⭐ 검증과 인덱스 변환은 같은 패스다** — 참조가 유효한지 확인하려면 어차피 대상 행을 찾아야 하고, 찾은 김에 **행 번호를 저장**해 두면 조회 때마다 키로 다시 찾지 않아도 된다(레거시 `ConvertColToIndexRefMetaM` 의 🟢 승계). 검증만 하고 버리는 것이 오히려 낭비다. **참조 무결성은 표 하나만 봐서는 판정할 수 없으므로** 로딩 단위를 파일이 아니라 **묶음**(`StaticTableSetBuilder`)으로 올렸다. 여러 표의 오류를 **함께** 보고한다(A 를 고친 뒤에야 B 의 문제를 알게 되면 안 된다). 범위는 정수·실수를 **따로** 둔다 — 하나의 `double` 로 통일하면 `Int64` 의 2⁵³ 초과 값이 **경계에서 조용히 틀린 판정**을 낸다. **모순된 스키마는 조립 시점에 거부**(문자열 열에 정수 범위 등) — 조용히 무시되는 설정이 가장 위험하다. 테스트 15종 추가(총 36)
- [x] ⚠ 핫 리로드 — 무중단 데이터 갱신 (2026-08-10, ADR-0042). **승계할 구현이 없어**(`FileWatcherSystemM.cs` 참조 0) 처음부터 설계했다. **⭐ 문제의 전부는 "교체를 원자적으로 만드는 것"** 이었고, 테이블이 이미 불변이라 **참조 하나를 바꾸는 것**으로 끝났다 — 읽기 쪽 동기화가 0이다. **⚠⚠ 기동과 재적재의 실패 처리는 정반대다**: 기동 검증 실패 = 기동 실패, 재적재 검증 실패 = **옛 데이터 유지**. 돌고 있는 서버를 표 오타로 죽이면 안 된다. 그래서 검증 실패는 예외가 아니라 결과(`StaticTableReloadResult`)이고, 반대로 파일 없음 같은 **환경 오류는 그대로 전파**한다. **파일 감시기는 넣지 않는다** — 언제 다시 읽을지는 정책이다. 테스트 9종 추가(총 45)
- [x] 클라이언트-서버 테이블 버전 검증 — 불일치 시 접속 거부 (2026-08-10, ADR-0044). **⚠ Core 는 이 값이 무엇의 지문인지 모른다** — 데이터 테이블은 선택 축이므로 Core 의 타입은 `ContentFingerprint` 이지 `TableVersion` 이 아니다. **이 일반화는 취향이 아니라 하드 룰이 강제한 것**이고, 그 결과 `StaticTableFingerprint` → `ContentFingerprint` 변환 한 줄이 **앱의 몫**으로 남는다(그 어색함이 두 축이 분리돼 있다는 증거다). **⭐ 동결 핸드셰이크에 필드를 더하지 않는다** — `ClientHello` 는 영구 동결이라(R-2) 새 메시지 ID(40010·40011)를 예약했고, `ClientHello` 와 같은 플러시에 실어 **왕복은 늘지 않는다**. 불일치는 새 응답 형식 없이 기존 `ConnectionRejected` 의 **사유 코드만** 늘려 보낸다(형식을 늘리면 그것을 모르는 클라가 사유를 잃는다). 예외 타입을 나눈 이유는 **조치가 다르기 때문**이다(실행 파일 갱신 vs 데이터 갱신). **⚠ 파일이 아니라 파싱 결과를 해싱한다** — 주석·개행에 반응하면 `git autocrlf` 하나로 전 클라가 거부된다. 행 순서는 포함(행 번호가 참조의 목적지), 표 등록 순서는 미포함. 지문은 `XxHash128`(`string.GetHashCode` 는 프로세스마다 시드가 달라 쓸 수 없다). **이것은 인증이 아니다** — 사고 방지이지 공격 방지가 아니다. 테스트 48종
- [x] 서버 표를 클라에 그대로 전송 — `StaticTableSnapshot` (2026-08-10, ADR-0045). 지문 대조는 "어긋났다"를 알려 줄 뿐 고쳐 주지 않는다. 전송하면 **불일치가 원천 차단**되고 클라이언트는 **데이터 파일을 갖지 않아도 된다**. **⭐ 스키마는 와이어에 싣되 읽을 때는 로컬 것을 쓴다** — 실린 것은 대조용이다(값이 열 우선이라 스키마가 한 칸만 어긋나도 **전부 엉뚱한 열로 조용히 해석**된다). 로컬 인스턴스를 쓰는 결정적 이유는 **생성된 접근자와의 호환**: 뷰가 스키마 참조 동일성으로 서수 일치를 보장하므로(ADR-0043) 와이어 스키마로 세우면 받은 표가 거부된다. 대가로 **클라가 스키마를 컴파일 타임에 가져야** 한다 — 그 이점을 포기하지 않으면 강타입 접근이 사라진다. **합격 기준은 지문 보존**(필드 비교보다 강하고 열 종류가 늘어도 유효하다). 참조 해결 결과는 싣지 않고 **다시 푼다**(값에서 유도되는 것이라 따로 실으면 어긋날 수 있다). 잘린 입력은 예외가 아니라 `false` — 문자열 길이를 믿고 배열을 먼저 잡지 않는다(손상된 스냅샷의 큰 길이가 곧 OOM). 테스트 25종. **남은 것: 전송 배선은 앱의 정책**(어느 메시지로 언제 보낼지 — 파일 감시기를 넣지 않은 것과 같은 선)

## Phase 15 — 클러스터 & 분산

- [x] `IClusterMembership` — 정적 목록 구현 (2026-08-10, ADR-0047). **⚠ 이 축은 장애를 판정하지 않는다** — "살아 있는가"는 제공자가 이미 답한다(K8s readiness, Consul 헬스체크). 프레임워크가 자체 감지를 얹으면 **두 판정이 어긋나고 어느 쪽을 믿을지 아무도 모른다**. `Nodes` 에 있다는 것이 곧 "지금 보낼 수 있다"이고, 항상 `Alive` 인 필드는 거짓말이다. **⭐ 뷰는 식별자 사전 순으로 고정** — 발견 순서에 기대면 노드마다 다른 순서를 보고, 순서에 기대는 라우팅(해시 링 구성)이 **모든 노드가 자기만 옳다고 믿는** 장애를 만든다(스냅샷을 이름 순으로 굽는 것과 같은 판단). **⚠⚠ 한 작업은 `Current` 를 한 번만 읽는다** — 다시 읽으면 같은 요청의 두 조각이 다른 노드로 간다(핫 리로드와 같은 규약·같은 이유). 알림은 **밀지 않고 기다린다**(`WaitForChangeAsync`) — 이벤트는 구독 해제 누수 + 느린 구독자용 큐를 부른다. **세대 인자가 "확인 직후·대기 직전" 경합을 닫고**, 바뀌지 않는 제공자는 취소될 때까지 완료하지 않는다(헛되이 깨우면 비용이 노드 수만큼 곱해진다). 노드 주소는 **내부 통신용**(클라 접속 주소와 섞으면 "연결은 되는데 엉뚱한 경로"). 식별자는 주소와 분리(주소가 바뀌면 모든 키가 재배치된다). 테스트 18종. **⚠ 두 번째 구현 전까지 이 추상화는 가설이다**
- [x] ⭐ 서비스 디스커버리 어댑터 — **Consul** (2026-08-11, ADR-0055). **이 구현이 나오면서 `IClusterMembership` 이 가설에서 벗어난다**(CLAUDE.md 3절). ⭐ **블로킹 쿼리가 우리 계약과 1:1 이었다** — `?index=N&wait=T` 가 `WaitForChangeAsync(knownGeneration)` 의 "세대를 들고 기다린다"·"밀지 않고 당긴다"(ADR-0047)와 같고, 헬스체크가 내장이라 "살아 있는가는 제공자가 답한다" 가 그대로 성립한다(`?passing=true`). **벤더 패키지를 쓰지 않는다** — `HttpClient` + 소스 생성 JSON(리플렉션은 AOT 를 깬다, 하드 룰). 서드파티 의존 **0**. ⚠⚠ **Consul 인덱스를 세대로 쓰지 않는다** — `ulong`↔`int` 로 잘리고, **인덱스는 되돌아가며**(서버 재시작), 우리와 무관한 이유로도 오른다. 세대는 **구성원이 실제로 달라졌을 때만** 오른다. ⭐ 고의 회귀로 확인: 내용 비교를 없애자 무관한 변경 3회에 세대가 **1→4**. ⚠⚠ **첫 조회 실패 시 인스턴스를 만들지 않는다** — 구성을 모르면 이 노드는 **전 키스페이스를 자기 것이라 믿는다**. 이후 실패는 던지지 않고 마지막 구성을 유지한다(재시도에 지연 — 없으면 Consul 이 죽었을 때 CPU 를 태운다). ⚠ **자기 자신을 뷰에 끼워 넣지 않는다**(끼우면 남들은 아니라는데 나만 내 것이라 믿는 키가 생긴다). ⚠ **노드 번호는 명시적 메타** — 서비스 ID 파싱 금지(형식이 배포마다 다르고 조용히 틀린 번호를 만든다). 없으면 구성원에서 제외. **등록은 하지 않는다**(배포의 몫. 어댑터가 등록하면 테스트가 자기가 쓴 걸 자기가 읽는 순환이 된다). 실제 Consul 컨테이너로 테스트 9종, 반복 6회 통과
- [x] ⚠ 파티셔닝 / 라우팅 전략 — 랑데뷰(HRW) 해싱 (2026-08-10, ADR-0048). **`PartitionKey.ToIndex(노드 수)` 는 쓸 수 없다** — 측정 결과 노드 8 → 9 에서 **키의 50%가 이동**한다(나머지 연산이 아니라 곱셈-시프트 축소라 89%는 아니었다. 테스트가 짐작을 정정했다). 랑데뷰는 11%이고, 무엇보다 **살아남은 노드끼리는 키를 주고받지 않는다**. **⭐ 링(일관 해싱)이 아닌 이유는 가상 노드 수라는 조용한 손잡이** — 적게 잡으면 한 노드가 몇 배 부하를 받는데 **오류가 안 난다**. 상위 k 후보도 랑데뷰는 순위 그대로가 답이다(링은 "같은 물리 노드 건너뛰기"가 고전적 버그 자리). **대가는 측정했다**: 노드당 1.1ns 선형, 16노드 17ns(메시지 예산의 0.3%) · 64노드 81ns(1.3%) · 256노드 278ns(4.6%) → **교차점은 64~128노드**(BENCHMARKS 2026-08-10). ⚠ **라우터는 뷰 하나에 묶인다** — "한 작업은 뷰를 한 번만 읽는다"를 **타입으로** 표현한 것이다. 동점은 번호가 작은 노드가 이긴다("사실상 안 일어난다"는 분산 시스템에서 근거가 아니다). 테스트 20종
- [x] ⭐ 노드 번호 유일성을 런타임에도 지킨다 — **배정하지 않는다. 겹침을 기동 실패로 드러낸다** (2026-08-11, ADR-0056). `ClusterView` 는 **한 목록 안의** 중복만 잡으므로 서로 다른 목록을 든 두 노드는 **아무도 겹침을 보지 못했고**, 그러면 `ObjectId` 가 조용히 충돌해 "가끔 엉뚱한 객체가 나온다" 로 나타난다. → `ConsulNodeIdLease`(KV 잠금 + 세션, `Behavior=delete`). **⚠ 배정이 아니라 확인이다** — 번호를 어디서 얻는지는 배포가 정하고(StatefulSet 서수 등), 프레임워크가 배정하려면 "몇 번까지" 를 알아야 하며 **재시작마다 번호가 바뀌면 로그에서 노드를 추적할 수 없다**. 충돌 예외에 **누가 들고 있는지**를 싣는다(진단 없으면 운영자가 할 수 있는 일이 없다). ⚠⚠ **상호 배제가 아니다** — 세션 만료 판정은 Consul 이 하고 우리는 갱신 실패 뒤에야 알므로 "만료됐는데 아직 도는" 구간이 남는다(`LockDelay` 가 좁힐 뿐). 그래서 **프로세스를 대신 죽이지 않고** `Lost` 를 노출한다. ⚠ 네트워크 흔들림 한 번으로 번호를 포기하지 않는다(성급한 `Lost` 는 **멀쩡한 노드를 스스로 내려가게** 한다). ⭐ **고의 회귀가 같은 실수를 또 찾아냈다** — Consul 은 잠금 실패도 **HTTP 200 + 본문 `false`** 로 돌려준다. ADR-0051 의 `FlushResult` 와 **정확히 같은 모양**이고, 이번에는 "이 API 는 실패를 반환값으로 알리는가" 를 미리 물어봐서 처음부터 막혔다. 테스트 9종, 반복 4회 통과
- [x] 노드 간 통신 — 라우팅 결정 (2026-08-10, ADR-0049) (진행 중: 피어 링크 배선). **⭐ 새 전송을 만들지 않는다** — 노드가 노드에 접속하는 것은 그냥 클라이언트 접속이고, 전송·프레이밍·디스패치·TLS 축이 전부 이미 있다. 세어 보니 **없던 것은 "그 키의 소유자가 나인가" 라는 결정 하나**였다. `ClusterRoute`(Local/Remote/Unavailable)로 **로컬 단락을 타입에 드러낸다** — 이 판정은 호출자마다 반복되고 반복되는 판정은 언젠가 한 곳에서 빠지는데, 그 한 곳이 **자기에게 네트워크 왕복을 하고 자기에게 연결하는 커넥션을 만들어** 접속 한도와 통계를 오염시킨다. ⚠ **뷰와 라우터는 짝으로만 유효하다** — 뷰는 새것인데 라우터가 옛것이면 **사라진 노드로 보낸다**. `ClusterRouteResolver` 가 그 재생성을 한 곳에서 하고, 잠금 없이(불변 교체) CAS 한 번만 시도한 뒤 **자기 치유**에 맡긴다. 테스트 12종
- [x] 피어 링크 배선 — `ChServerM.Cluster.Hosting` (2026-08-10, ADR-0050). **계층은 별도 어셈블리로 갈랐다** — 어댑터에 넣으면 의존 방향이 뒤집히고(`Hosting → 어댑터 → Core`), Hosting 에 넣으면 조립 계층이 클러스터를 알게 되어 "빼도 성립한다"가 흐려진다. 별도 어셈블리는 둘 다 피하고 **클러스터를 빼면 이것만 빠진다**. ⚠ **재연결은 하고 재전송은 하지 않는다** — 피어 링크에는 다시 세울 세션 상태가 없으므로 `ClientBuilder` 의 "재접속을 감추지 않는다"가 적용되지 않지만, 끊길 때 큐의 프레임은 사라진다(재전송은 중복을 만들고 중복 처리는 응용의 몫). **`Sent` 는 "상대가 받았다"가 아니다.** 피어당 **유계 채널 + 소비자 하나**로 직렬화·백프레셔·거절을 한 구조로 얻는다. **⚠⚠ 레거시와 같은 `Wait`+`TryWrite` 조합을 쓰되 반환값을 본다** — 그 `false` 가 곧 `QueueFull` 이고 호출자에게 나간다(레거시는 버려서 조용히 유실했다). 자기에게 보내면 `Loopback`(조용히 성공시키면 자기에게 연결하는 커넥션이 생긴다). **⭐ 테스트가 실제 버그를 잡았다** — 정리 코드가 구성원 확인보다 뒤에 있어 클러스터가 자기 혼자로 줄면 링크가 영영 안 닫혔다. 테스트 13종(**실제 두 노드가 프레임을 주고받는다**). ~~⚠ 미검증: `QueueFull` 경로 · 버퍼 반납 · 재연결 · 부하 아래 처리량~~ → **2026-08-10 전부 닫힘, 셋이 결함이었다(ADR-0051)**
- [x] ⚠ 피어 링크 미검증 넷 해소 (2026-08-10, ADR-0051). **미검증이라고 정직하게 적어 둔 것이 결함을 감추고 있었다** — 넷 중 셋이 결함이다. ⭐ **`PipeWriter.FlushAsync` 는 상대가 닫혀도 던지지 않는다**: `IsCompleted` 만 세우고 성공한 듯 반환한다. 그 결과를 버려서 링크가 살아 있는 척하며 **이후 모든 프레임을 조용히 삼켰고 재연결이 영원히 트리거되지 않았다**(고의 회귀: 피어 재기동 후 20번 보내도 0장 도착). ADR-0050 이 `TryWrite` 의 `false` 를 반드시 본다고 적어 놓고 **바로 다음 줄의 반환값은 버리고 있었다.** 그 밖에: `Drop()` 이 커넥션을 해제하지 않아 재연결마다 소켓 누수 · `ClientSession.Completion`(읽기 루프)을 버려 예외 미관측 + 가장 이른 사망 신호 상실 · `GetOrAdd` 팩토리 중복 호출로 고아 링크. ⚠ **죽음의 신호는 둘이고 둘 다 필요하다** — 쓰기 전 검사는 프레임을 살리고(재전송이 아니다), `FlushResult` 는 half-close 처럼 **다른 신호가 없는** 경우를 잡는다. 재연결 테스트 하나로는 `FlushResult` 를 지워도 초록이라 **half-close 테스트를 따로 붙여야 홀로 증명된다**. ⚠ **관측과 기다림을 갈랐다** — 커넥션 해제는 기다리고(우리 자원) 읽기 루프는 관측만 한다(기다리자 무한 정지를 실제로 재현했다, 감사 H3 과 같은 판단). 테스트 18종(+5), 고의 회귀 3종 확인. 벤치: 피어 1개 **~2.0~2.5 M 프레임/s**
- [x] ⚠ **"프레임당 힙 할당 0" 이 큐 깊이에 조건부임을 확인하고 원인을 특정했다** (2026-08-10, ADR-0051). 같은 코드가 in-flight 1,000 에서 0 B, 10,000 에서 27~308 B 다. 원인은 `ArrayPool<byte>.Shared` 의 **버킷당 보관 개수 한계** — ⭐ **풀만 바꾸자(코드 변경 0) 0 B 로 회복됐고 처리량이 33% 빨라졌다**(514.0 → 342.9 ns/프레임). 대가가 할당만이 아니라는 것이 이 측정의 핵심이다. ⚠ **판별 기준은 "큐" 가 아니라 미처리 대여물이 무엇에 비례하는가** 다 — 동시성에 비례하면 안전, **설정 용량**(큐 깊이·최대 커넥션 수)에 비례하면 같은 함정. 전수 결과: `ClusterPeerSet`(확인·수정) · `PooledBufferWriter`(⚠ **최대 커넥션 수**에 비례. 문서가 "커넥션당 하나 재사용" 을 권장하므로 **권장 사용법이 곧 함정**이다) · `FragmentAssembler`(조건부) · 나머지는 호출 스코프 반납이라 안전
- [x] ⚠ **전용 풀의 보유 메모리 상한 측정** — 기본값 결정의 선결 조건 (2026-08-10, ADR-0051 · BENCHMARKS). 하네스 `Bench/ChServerM.Bench/Buffers/PoolRetentionReport.cs` (`-- retention`). **BDN 밖에서 잰다** — BDN 은 연산당 할당량을 재고 여기서 필요한 것은 **정상 상태 보유량**이다. ① **닫힌 식**(실측 오차 ≤0.42%): `상한 ≈ 2 × maxArrayLength × maxArraysPerBucket`. 초과 대여는 담기지 않으므로 어림이 아니라 상한이다. **기본값(1 MiB × 1,024)에서 이미 2 GiB, 깊은 큐(10,000)면 19.5 GiB**. ② ⭐⭐ **전용 풀은 트리밍하지 않는다** — 256 MiB 피크 후 **10분 유휴에 1 바이트도 반납 안 함**(256.01 MiB 고정). 같은 조건 `Shared` 는 분당 ~32 MiB 씩 **9분에 99.6% 반납**. 즉 상한은 옵션이 이미 약속한 최대 수요이지만 전용 풀은 그것을 **일시적 피크에서 영구 점유로 바꾼다**. ⚠ **90초 표본만 봤을 때는 `Shared` 도 9.4% 라 정반대 결론으로 보였다** — 트리밍이 분 단위라 짧은 표본이 거짓말을 한다
- [x] ⚠ **기본값 결정 — 풀 인자를 필수로 했다** (2026-08-10, ADR-0051 결정 6). 4-인자 생성자를 제거해 `ArrayPool<byte>` 를 필수 인자로 만들었다. **측정이 가리킨 결론은 "어느 기본값이 나은가" 가 아니라 "프레임워크가 옳은 값을 계산할 수 없다" 였다** — 옳은 기본값이 없으면 기본값을 두지 않는다(`FrameWriter` 옵션 매개변수를 전부 필수로 바꾼 것과 같은 자리, CLAUDE.md 8.1). 기각: 1안(계산 불가) · 5안("큰 페이로드는 드물다" 가 **미측정 가정**) · 6안(메모리 위험을 **조용한 성능 열화로 바꿀 뿐**) · 3·4안(원래 이유). 생성자 XML 문서에 **세 규약**(깊이 × 피어 수 · 트리밍 없음 · 상한 식)과 무엇을 넘길지를 적었다. ⚠ **상한이 크면 줄일 것은 풀이 아니라 `SendQueueDepth`·`MaxPayloadLength`** 다 — 상한은 그 둘이 이미 약속한 최대 수요다. `PublicAPI.Unshipped` 에서 줄 삭제(릴리스 전이라 비용 0). 1114개 통과, 경고 0. 근거였던 것: ⭐ **"보관 개수 = 큐 깊이" 규칙 자체가 틀렸다**: 보낼 큐는 **링크마다**(`ClusterPeerSet.cs:410`) 생기는데 풀은 **집합에 하나**(`:146`)라 최악 미처리 대여는 **`깊이 × 피어 수`** 다. 16노드면 15배 작게 잡혀 **고치려던 함정이 그대로 돌아온다**. 기존 A/B 가 이것에 안 걸린 이유는 피어가 하나였기 때문이지 규칙이 맞아서가 아니다. 진짜 걸림돌은 "상한 계산" 이 아니라 **"필요한 보관 개수를 생성 시점에 알 수 없다"**(피어 수는 옵션이 아니고 뷰가 런타임에 바꾼다). ⚠ **다중 피어 포화는 구조에서 읽었고 실측하지 않았다** — 이 결정의 근거 중 유일하게 측정으로 뒷받침되지 않은 항이다
- [x] ⭐ `PooledBufferWriter` 1만 커넥션 재측정 — **ADR-0016 의 주장이 성립한다** (2026-08-11, BENCHMARKS). 1만 개가 대여를 붙든 상태에서도 정상 상태(`Clear`+쓰기) 할당은 **0 B**(53.69→68.59 ns). "얕은 조건의 값" 경고를 거둔다. ⭐⭐ **그리고 이것이 ADR-0051 의 판별 기준을 정정했다** — "미처리 대여물이 설정 용량에 비례하면 함정" 은 **너무 넓었다**. `ClusterPeerSet` 은 메시지마다 빌리고 반납해 **반납이 몰리지만**, 이 타입은 생성 시 한 번 빌려 수명 내내 들고 `Clear` 가 버퍼를 유지하므로 **정상 상태에 대여 왕래 자체가 없다**. **정정: 함정을 가르는 것은 "동시에 붙들린 수" 가 아니라 "반납이 설정 용량 규모로 몰리는가" 다** — 붙들고만 있는 대여물은 버킷을 놓고 경쟁하지 않는다. ⚠ 대신 진짜 비용은 **상주 메모리**(1만 × 8 KiB ≈ 80 MiB)이며 헤드라인 "할당 0" 이 그 크기를 가린다. ~~⚠ 대량 접속 해제는 반납이 몰리는 바로 그 모양이라 미측정~~ → **같은 날 실측했다**(BENCHMARKS "대량 접속 해제 구간"): 반납 자체는 0 B·2 ms 지만 풀이 1만 반납 중 ~9%만 붙들어, **재접속 폭풍에서 커넥션당 ~7.4 KiB(합계 70.7 MiB) 신규 할당**이 난다. 순차 왕래는 배열 할당 0 — 이것이 "몰림"의 값이다. 결함이 아니라 예산으로 판정(스파이크 생존 기측정), 전용 풀 전환은 실워크로드에서 문제로 관측될 때. **`FragmentAssembler` 조건부 경로도 실측 종결**(BENCHMARKS "조각 재조립의 조건부 비용"): 조각 없는 커넥션 0 B(ADR-0015 성립), 조각 4×1 KiB·16×256 B 재조립도 **정상 상태 무할당**, 시간 영향 상한 +1.3%
- [x] ⚠ 리밸런싱 — **옮길 상태가 없다. 없던 것은 소유권이 바뀌었다는 신호였다** (2026-08-10, ADR-0052). 구현 전에 무엇을 옮겨야 하는지부터 셌더니 **하나도 없었다** — 저장 축은 공유(Redis·Garnet·PostgreSQL)이거나 이미 다중 노드 불가로 문서화된 것(인메모리)이고, `ExecutionPartition` 이 담는 것은 상태가 아니라 실행 순서이며, 서킷 브레이커·속도 제한은 **노드 로컬인 것이 의도된 설계**(ADR-0035)라 옮기면 오히려 틀린다. 소유권 겹침도 **이미 막혀 있다** — 단일 키 CAS 가 다른 노드의 좀비 쓰기까지 `Conflict` 로 거부한다(CONSISTENCY 5절). 없던 것은 **뷰 전환 신호**였고, 그것이 없으면 노드-로컬 캐시·룸·타이머를 든 앱이 **남의 키를 계속 처리한다**. → `ClusterRouteResolver.WatchAsync`. **⭐ 새 타입을 만들지 않았다** — 별도 타입이면 뷰·라우터 짝을 다시 맞춰야 하고 그 어긋남이 이 축의 알려진 함정이다(ADR-0049). **⭐ 뷰가 아니라 라우터를 준다**(어긋날 창을 없앤다) · **첫 항목은 지금 뷰**(기동 배치와 재검토를 같은 코드로) · **밀린 세대는 합친다**(뷰는 이벤트가 아니라 상태 → 무제한 큐 금지를 구조적으로 만족) · ⚠ **깨우는 신호에 실린 뷰를 믿지 않는다**(큐로 나르는 제공자에선 낡는다). ⚠⚠ **"잃은 키 목록" 을 주지 않는다 — 줄 수 없다**(랑데뷰는 역방향이 없고 프레임워크는 앱의 보유분을 모른다). 상태 이동은 앱의 몫. 테스트 8종. ⭐ **고의 회귀가 속 빈 테스트를 드러냈다** — 합치기 테스트가 장치를 없애도 초록이었고(가짜가 신호에 `Current` 를 실어 보냈다), 낡은 신호를 내는 가짜를 따로 만들고서야 홀로 증명됐다
- [x] ⭐ ADR-0047 열린 질문 종결 — **"일시적 이탈 vs 영구 제거" 구분은 필요 없다** (2026-08-10, ADR-0052). 그 구분이 필요한 이유는 옮기는 것이 있을 때 깜빡이는 노드가 **이동을 두 번 유발**하기 때문인데, 옮기지 않으면 스래싱할 것이 없다. 재검토는 멱등하고 값싸다. ⚠ **상태 이동을 도입하는 순간 다시 열린다**
- [x] ⚠ 리더 선출 — **선출하지 않는다. 계산한다** (2026-08-10, ADR-0054). 역할 고정 키의 랑데뷰 소유자가 곧 리더다 — **메시지 0개·합의 0회**, 같은 뷰면 모든 노드가 같은 답, 리더가 빠지면 승계 절차 없이 다음 순위. `ClusterRouteResolver.IsLeaderFor`. **⚠⚠ 이것은 상호 배제가 아니다** — ① 뷰가 갈리면 각 무리가 리더를 뽑고 ② **뷰 갱신은 즉시가 아니라** 옛 리더가 밀려난 줄 모르는 구간이 남는다(**정족수를 켜도 남는다**). 그래서 리더의 일은 **중복 실행돼도 안전해야** 한다. ⭐ 그 사실을 `SplitBrain_bothSidesElectLeaders_whenQuorumIsNotUsed` 로 **계약으로 못 박았다** — 나중에 "리더니까 하나겠지" 로 읽히는 것을 막는다. ⚠ **`ClusterView.Generation` 을 펜싱 토큰으로 쓰지 않는다**(갈라진 두 무리가 같은 세대를 들 수 있다). 리스+펜싱은 만들지 않았다 — 클러스터 축이 영속화 축에 의존하게 되고 etcd·Consul 이 이미 준다
- [x] ⚠ 스플릿 브레인 대응 — **정족수 게이트. 감지가 아니다** (2026-08-10, ADR-0054). `ClusterQuorum.MajorityOf(기대 노드 수)` 가 하는 일은 **내 뷰의 크기를 세어 문턱과 비교하는 것**뿐이다("저쪽이 살아 있는가" 는 묻지 않는다 — 이 축은 장애를 판정하지 않는다, ADR-0047). 효과는 **과반을 못 보는 무리가 스스로 물러나는 것**. ⚠ **문턱은 설정이지 발견이 아니다** — 제공자의 뷰를 문턱 근거로 쓰면 **분할 뒤에도 "내 뷰 전부가 살아 있으니 과반" 이라는 순환**에 빠진다. ⚠ **짝수는 손해다** — 6대가 3:3 이면 **양쪽 다 물러나** 아무 일도 안 일어난다(테스트로 고정). ⭐ **정족수는 필수 인자다** — `None` 은 값이 아니라 명시적 선택이며, 기본값을 골랐다면 둘 중 하나가 조용히 틀린 경우다(ADR-0051 결정 6 과 같은 자리). 테스트 17종
- [x] ⚠ 무중단 배포 — **순서는 이미 있었다. 없던 것은 간격이다** (2026-08-10, ADR-0053). 3단 종료(`Unbind`→`Stop`)와 전송 드레인·readiness 연동은 Phase 5·11 부터 있었는데, `UnbindAsync` 가 **readiness 내리기와 수용 중지를 같은 호출에서 연달아** 했다. ⚠⚠ **그 사이에 도착한 접속은 다른 노드로 넘어가지 않고 RST 로 실패한다** — 프로브 주기+전파는 초 단위라 배포마다 그만큼 잘린다. 이것이 "무중단 배포인데 오류가 난다" 의 정체였다. → `ChServerMServer.DrainAsync`: readiness↓ → **전파 대기** → 수용 중지 → 드레인 → 정지. **새로 만든 것은 가운데 대기 하나**다. ⭐ **드레인 상한과 절차 취소를 다른 토큰으로 가른다**(겹치면 상한 만료가 "배포 취소" 로 읽힌다) · ⭐ **강제 종료는 예외가 아니라 보고**(`DrainReport.CompletedWithinTimeout`)로 나온다 — 매 배포마다 거짓이면 상한이 짧거나 앱이 커넥션을 안 놓는 것이고, 값이 없으면 **둘 다 조용히 지나간다**. ⚠ 문서로만 갚은 둘: **드레인 상한 < 오케스트레이터 종료 유예**(넘으면 SIGKILL 로 잘려 **드레인이 없는 것보다 나빠진다**) · **긴 수명 커넥션은 스스로 안 끝난다**(상한을 항상 치는 것이 정상. "옮겨 가라" 통지는 프로토콜 결정이라 앱의 몫이고 `DrainAsync` **전에** 보내야 한다). 고의 회귀로 확인(전파 대기를 언바인드 뒤로 옮기니 정확히 그 테스트 하나가 깨졌다). 테스트 7종
- [x] ⚠ 통합 테스트: 다중 노드 시나리오 — **미검증으로 남겨 둔 것들을 닫는 자리** (2026-08-10). `MultiNodeClusterTests` 4종. ⭐⭐ **`깊이 × 피어 수` 를 실측했다**(ADR-0051 의 마지막 미검증 근거): 피어 4·깊이 4 에서 최고 미처리 대여 **17개**(= 4×4 + 소비자가 꺼내 든 1). **결정성은 `GatedClientTransport`(연결 수립을 붙잡는다)로 얻었다** — 소비자가 첫 프레임에서 멈추므로 큐가 정확히 찬다. ⭐ **분할이 동작으로 보인다**(ADR-0054): 노드마다 자기 멤버십을 들려 ① 게이트 없이 양쪽 다 리더 ② 과반 요구 시 소수파가 통째로 물러남 ③ **서로에게 `NotAMember`**. 그 밖에: 세 노드가 라우팅 결정대로 주고받고 로컬은 단락 · **한 노드를 드레인해도 나머지 트래픽이 끊기지 않는다**. ⭐ **반복 실행 12회가 흔들림을 잡았다**(CLAUDE.md 9.9) — `Task.Delay` 와 `Stopwatch` 가 다른 시계라 시간 단언이 2/12 로 깨졌고, 주제가 아닌 단언은 없애고 남긴 것에는 여유를 뒀다. ⚠ **여전히 미검증**: 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 전송 위의 다중 노드(뷰는 제공자가 주므로 프레임워크 쪽은 달라지지 않지만, 그것을 근거로 삼는 것과 재는 것은 다르다)

## Phase 16 — 대체 전송

`stateless-web` 참조 프로필이 완성되는 지점. **여기서 "두 프로필이 같은 핸들러로 동작"이
프로덕션 수준으로 증명된다** (Phase 2의 인메모리 전송이 그 예비 증명이었다).

- [x] `ChServerM.Transport.Http` — Kestrel 기반, 동일 파이프라인 재사용 (2026-08-11, ADR-0057). 핵심 대응: **HTTP/2 스트림 하나 = 커넥션 하나** — 요청 본문이 `Input`, 응답 본문이 `Output` 이라 프레이밍·디스패치·핸들러가 **한 줄도 안 바뀌고** HTTP 위에서 돈다(gRPC 양방향과 같은 모양). **`WebApplication`/DI 없이 `KestrelServer` + `IHttpApplication<T>` 직접 구현**, 의존은 공유 프레임워크라 NuGet 패키지 0. 평문 h2c 전용(평문 포트에선 1.1↔2 협상이 불가능하고, 어중간히 1.1 을 받으면 왕복 워크로드만 조용히 교착한다). Unbind = 신규 스트림 503(LB 드레인 패턴) · 흐름 제어 윈도가 `ITransportBufferLimits` 로 노출되어 ADR-0007 교착 검사가 그대로 적용. ⭐ 구현 중 함정 둘을 실측으로 잡았다: ① **같은 연결의 두 번째 스트림부터 HEADERS 가 본문 첫 쓰기까지 클라이언트 버퍼에 갇혀** 연결 수립이 교착(첫 스트림은 연결 수립 플러시에 편승해 통과 — 커넥션 1개 테스트는 전부 초록인 형태) → 펌프 시작 직후 빈 플러시(gRPC 와 같은 해법) ② Kestrel 요청 본문 리더는 스트림 리셋 후 `CancelPendingRead`/`CompleteAsync` 에서도 던지며, 새어 나가면 **프로세스가 죽는다**(테스트 호스트 크래시 실측) → 종료 경로 전부 방어. `CrossTransportTests` 14항목 × **3전송** + 반복 5회 통과
- [x] 무상태 모드 — 세션을 `ISessionStore`로 외부화 (2026-08-11). **새 장치가 필요 없었다** — 실행 모델 미지정(=병렬) + 핸들러가 `ISessionStore` CAS 계약만 쓰는 조립이 곧 무상태 모드다(ADR-0004 가 설계한 대로). 증명은 아래 프로필 테스트가 한다
- [x] **`stateless-web` 프로필 완성** — `realtime-stateful`과 동일한 핸들러 코드로 동작함을 통합 테스트로 고정 (2026-08-11, `StatelessWebProfileTests`). 같은 `CounterHandler` **타입 하나**가 ① TCP + 파티션 실행 모델 + 노드 로컬 저장소 ② HTTP + 병렬 + **서버 노드 2개가 공유하는 외부 저장소** 양쪽에서 돈다. **카운터가 노드를 건너 이어진다** — 상태가 커넥션이 아니라 저장소에 있다는 것(수평 확장의 전제)이 동작으로 증명된다. 동시 세션 8개 × 20회 간섭 없음
- [x] `ChServerM.Transport.WebSocket` (2026-08-11, ADR-0059). **메시지 경계를 버리고 바이트 스트림으로** — WS 메시지=프레임 대응은 전송이 프레임 경계를 알게 되어 축 독립(ADR-0002)이 깨지고, 상대의 분할을 신뢰하는 구조가 된다. 내부 유계 파이프 2개 + 펌프 2개, 수신 파이프 임계값이 곧 백프레셔(`ITransportBufferLimits` 노출). 호스팅은 ADR-0057 재사용(KestrelServer 직접), 핸드셰이크는 `IHttpUpgradeFeature` 로 직접(RFC 6455 서버 쪽은 헤더 검증 + GUID SHA-1 한 줄 — `WebSocketMiddleware` 는 호스팅 스택을 요구해 탈락), 클라이언트는 BCL `ClientWebSocket`. 종료 대응: 반닫힘=Close 프레임, **수신 펌프는 어떤 종료든 EOF 로 통일**(전송별 "EOF vs 예외" 갈림 방지 — HTTP 실측 교훈). ⭐ `CrossTransportTests` 14항목 × **4전송** + 반복 5회, **첫 실행에 전부 통과** — HTTP 의 함정 둘을 처음부터 반영한 결과다
- [ ] ⚠ `ChServerM.Transport.Udp` — **수요가 확인될 때 만든다** (2026-08-11 보류, ADR-0060, 사용자 결정). 신뢰 UDP 의 존재 이유(HOL 회피·신뢰 스트림·단편화)는 QUIC 이 BCL 로 흡수한다. 남는 유일한 실수요는 **비신뢰 데이터그램**(RFC 9221 — `System.Net.Quic` 미노출)이며, 그것을 요구하는 워크로드가 생기면 자체 구현 vs LiteNetLib/ENet 을 그때 겨룬다(ADR-0038 규율 — 대상 없는 축은 만들지 않는다)
- [x] QUIC / HTTP/3 (`System.Net.Quic`) 검토 (2026-08-11, ADR-0060). **실행으로 검토했다**: 이 환경(Win11+.NET10)에서 `IsSupported` true·자가서명 수립(serverAuth EKU + PFX 왕복 함정 둘 확인)·**커넥션 1개 위 양방향 스트림 8개 × 100회 왕복 에코 성공**. 구현 시 매핑 확정: **QUIC 양방향 스트림 하나 = 커넥션 하나**(ADR-0057 과 같은 대응 + 스트림 단위 HOL 격리), TLS 는 프로토콜 내장이라 `ITransportSecurity` 조립 안 함
- [x] `ChServerM.Transport.Quic` 구현 (2026-08-11, ADR-0060 후속 — 검토와 같은 날). 매핑은 결정 1(양방향 스트림=커넥션), 구조는 WS 전송의 파이프 펌프 재사용, 클라이언트는 종단별 QUIC 연결 공유 + 죽은 연결 자기 치유 1회. **서버 인증서는 필수 인자**(기본값 없음 — ADR-0051 결정 6 규율), BCL 만 써서 서드파티 의존 0·Kestrel 참조도 0. `CrossTransportTests` **14항목 × 5전송** + 반복 5회 통과. QUIC 은 `SkippableTheory` — msquic 없는 환경(CI ubuntu 포함)은 실패가 아니라 건너뜀이고 그 사실이 출력에 남는다. ⚠ 배운 것: QUIC 스트림 열기는 로컬 연산이라 드레인 거부가 첫 입출력에서 드러난다 — Unbind 계약 테스트를 "쓸 수 없다"로 일반화
- [x] 전송 축 교체 테스트 — 같은 핸들러가 TCP/HTTP/WS에서 동작 (2026-08-11). `CrossTransportTests` 14항목 × 4전송(InMemory·TCP·HTTP·WebSocket)이 같은 핸들러·프레이밍·디스패치 코드로 통과. 항목당 `[InlineData]` 한 줄이 전송 추가의 전부였다 — 그것이 축이 서 있다는 증거다
- [x] HTTP 전송 성능 측정 — 프레임워크 세금(TCP 대비 HTTP 경유 비용)·h2c 다중화의 손익 (2026-08-11, BENCHMARKS "HTTP 전송 세금"). `LoadRunner --transport socket|http` 신설 — 서버 조립·프레이밍·핸들러·클라이언트 루프가 완전히 동일하고 전송 축만 갈린다. **지연 바닥(16 활성): p50 +8~9 µs · RPS −6.1%**(HTTP/2 프레임당 고정 비용). ⭐⭐ **고동시성(512 활성): 5.9× 역전**(883k vs 149k RPS · p50 419 µs vs 2.9 ms · p99 −81%) — 512 소켓이 스트림 512개/TCP 연결 ~6개로 다중화되어 **syscall 이 두 자릿수 배로 준다**. 10k+256 도 같은 경향(5.0×). 오류 0 · 생성기 비포화. ⚠ 루프백·128B 조건 — 실 NIC·대형 페이로드에서는 격차가 줄 것(한계 명시)

> **✅ 2026-08-11 — Phase 16 사실상 완료.** 전송 5종(인메모리·TCP·HTTP·WebSocket·QUIC)이
> `CrossTransportTests` 14항목을 같은 핸들러 코드로 통과하고, `stateless-web` 프로필이
> 2노드 세션 외부화로 증명됐다(ADR-0004 합격 기준). 남은 1건은 조건부 보류다 —
> UDP(수요 확인 시, ADR-0060). 후속 관찰: WS·QUIC 성능 측정은 수요 시(전송 세금의
> 상한은 h2c 측정이 그려 놨다), wss/HTTPS 는 Kestrel 옵션 노출로.

---

# Part V — 실시간 프리미티브 (선택 축)

**전부 빼도 프레임워크가 성립해야 한다** (ADR-0004). Core는 이 Part의 존재를 알지 않는다.
`ChServerM.RealTime.*` 별도 패키지로 격리해, 쓰지 않는 사용자가 의존을 끌고 오지 않게 한다.

실시간 상시 연결 워크로드에서 반복적으로 필요한 것들을 프리미티브로 제공한다.
게임에만 쓰이는 것은 아니다 — 협업 편집, 실시간 대시보드, IoT 텔레메트리도 같은 프리미티브를 쓴다.
도메인 로직(레이팅 공식, 충돌 판정 등)은 프레임워크가 아니라 `Samples/`에 둔다.

## Phase 17 — 틱 & 시간 동기화

- [x] `ChServerM.RealTime` — 고정 타임스텝 틱 루프. 드리프트 보정 (2026-08-11, ADR-0061). **절대 스케줄**(마감 = 원점 + n×간격 — 상대 스케줄의 오차 누적이 없다) + **유계 캐치업**(`MaxCatchUpTicks`, 초과분은 실행하지 않고 **건너뛰며 관측된다** — 무제한 캐치업은 죽음의 나선, 9.6과 동형). 핸들러는 동기 계약(예산 경계를 지키기 위해 의도적), 틱 단위 예외 격리(9.2), 전용 스레드 1개(9.5). Core 에 계약을 만들지 않았다 — 틱 루프는 구현체이고, 선택 축의 계약이 Core 에 들어가면 ADR-0004 위반이다(ADR-0061 결정 1)
- [x] 틱 예산 초과 감지 — 한 틱이 예산을 넘으면 관측에 노출 (2026-08-11). `TickLoopStatistics`(총·초과·건너뜀·실패·최장·최악 지터) + `RealTimeMetricNames`(chserverm.tick.*) + 간격 제한 로그(`IntervalGate` — 과부하 시 틱마다 경고를 찍으면 로그가 과부하를 가속한다). 강제 중단은 하지 않는다 — 반쯤 갱신된 시뮬레이션 상태가 더 나쁘고, 대응은 운영자의 몫이다(ADR-0061 결정 3)
- [x] 서버 시간 동기화 — 레거시 `FbsServerTick`, `FbsLoginOk.serverFrequency` 개념 승계 (2026-08-11, ADR-0063). ⭐ **µs 고정 단위(`MicrosecondClock`)로 주파수 환산 자체가 소멸했다** — 레거시의 문제 인식(머신마다 `Stopwatch.Frequency` 가 다르다)은 정확했지만 환산이 결함 표면(0 나누기·미수신 상태·double 정밀도)이었다. 몫·나머지 정수 분해라 1년 가동 회귀 테스트가 오차 0 을 고정한다. 클라 측 외삽은 `RemoteClock`(레거시 `ServerTickCurrent` 외삽 + 단조 보장 승계 — 출력은 역행 대신 멈췄다가 따라잡는다)
- [x] 지연 측정 / RTT 추정 — 레거시 `NetWorkDelayM.cs` 판정: 🟡 개작 확정 (2026-08-11, ADR-0063). IQR 이상치 제거 발상은 승계(`RttEstimator`, 스파이크 500ms 하나가 평균을 오염시키지 않음을 테스트로 고정), 동시성 결함(공유 정렬 버퍼 무락·`_locker` 선언만)은 락 추가가 아니라 **소유권 규약**(세션 소유 실행 컨텍스트 전용, 9.1)으로 해소. 왕복은 NTP 식 4-타임스탬프(`TimeSyncExchange`)로 상대 처리 지연을 제거 — RTT/2 비대칭 한계는 API 문서에 명시
- [x] 타이머 시스템 — 레거시 `Scheduler/TimeEventSchedulerM.cs`(🟢 설계 승계 — 레거시 최고 자산), `ExpireEventConCurSchedulerM.cs`(🔴 폐기) 판정 확정 (2026-08-11, ADR-0062). `TimerWheel` — 계층 타이밍 휠, 기하 레벨 구성 옵션화(기본 100ms×512×3=155일). 레거시 결함 전수 수정: 원점 초기화 · **만료/취소 콜백 분리**(`ITimerJob`) · 문자열 ID → 노드+세대 핸들(상태·세대 패킹 CAS 로 ABA 구조적 차단) · 정밀 시간 변환 · 풀/타이머 상한(초과는 **거부**) · Volatile 일관(9.3) · 콜백 예외 격리(9.2). **휠은 수동(스레드 없음)** — 단일 드라이버(`Advance`)가 밀고, 대개 틱 루프가 그 드라이버다(조립 테스트로 고정). 커넥션당 타이머 금지(9.5)의 종착점
- [x] 벤치마크: 틱 지터, 틱당 처리 용량 (2026-08-11, BENCHMARKS.md 두 절, ENV-B). **지터**: 순수 슬립 p99 ≈ OS 해상도 15ms · ⚠ 스핀 구간이 슬립 해상도보다 작으면 무효(1ms 스핀 p99 13.5ms — 초기 문서 주장이 실측으로 반증되어 정정) · **16ms 스핀이면 p99 0µs**(비용: 코어 1개의 ≤32%). **휠 용량**: 예약+발화 17.9ns/개 · 예약+취소 22.7ns/개 · **전부 0 B**(10k 발화가 100ms 틱 예산의 0.18%)

> **✅ 2026-08-11 — Phase 17 완료.** `ChServerM.RealTime`(선택 축, Core 만 참조·Core 는 모름),
> 테스트 61개(전 스위트 1,295개), 전체 재빌드 경고 0. Part V 의 "전부 빼도 성립"이 어셈블리
> 경계로 증명된다 — ARCHITECTURE.md 실시간 프리미티브 절.

## Phase 18 — 룸/존 & 관심 영역

> ⚠ **충돌 판정은 단위 테스트를 먼저 쓴다.** 레거시 충돌 계층에는 미수정 버그 8건이
> 있고 — 회전 미적용, 위치 영구 고정, 축정렬 quad 충돌 항상 false, 접촉점 무의미 —
> **실제로 검증된 적이 없다고 보아야 한다** ([03-ecs-object-model](legacy/03-ecs-object-model.md#새-코드에-절대-옮기면-안-되는-것--미수정-버그)).
> 승계하는 것은 알고리즘 구조(SAT, 집합 차분, Stay 스로틀, 모튼 코드)이지 코드가 아니다.


- [x] 룸/채널 추상화 — 생성·참가·퇴장·해산 생명주기 (2026-08-11, ADR-0064). `ChServerM.RealTime.Rooms` 신설(선택 축, Core 만 참조). `Room`(COW 멤버 배열 — 브로드캐스트는 Volatile 읽기 한 번, 참가·퇴장은 저빈도 락)·`RoomDirectory`(룸 수·정원 상한 + **거부**, 9.6). 레거시 `MapObjM` 맵 브로드캐스트 계약의 승계를 오브젝트 모델에서 독립 프리미티브로 분리
- [x] 브로드캐스트 최적화 — 같은 페이로드를 N명에게 보낼 때 직렬화 1회 (2026-08-11, ADR-0064). `RoomBroadcaster` + 참조 계수 `BroadcastFrame`(헤더+페이로드 1회 조립, 풀링). ⭐ **소유권 문제가 본체였다**: 커넥션 `Output` 단일 라이터 규약을 브로드캐스트가 정면으로 깨는데 방어 장치가 전무했다(조사로 확인). 해법은 `PartitionedMemberSink` — 멤버당 유계 채널 + **그 커넥션 파티션의 배타 슬롯에서 드레인**(핸들러 응답과 같은 큐에서 직렬화, 새 태스크 0개). `FlushResult` 필수 검사(ADR-0051)·실패 싱크 사망·콜백 통지. 와이어 검증은 실제 `FixedHeaderFrameDecoder` 왕복 테스트로
- [x] ⚠ 관심 영역(AOI) — 승계 구현 없음 확정, `MortonCodeM` 출발점 (2026-08-11, ADR-0065). `ChServerM.RealTime.Spatial` 신설(선택 축, Core 만 참조). 모튼 코드 승계(정규화 책임을 그리드로 옮겨 0 나누기·조용한 오매핑 결함 제거 — 인코더는 ushort 만 받아 타입이 강제) + `InterestGrid`(균일 그리드, 셀 키 = 모튼, 범위 밖은 가장자리 클램프 + 실제 위치 별도 보관이라 질의 정답 유지, 전수 검사 대조 테스트) + `InterestSet`(Enter/Leave 집합 차분 승계, 프레임당 new HashSet 대신 스왑+Clear — 레거시 결함 #14 수정). 쿼드트리 탈락 근거는 ADR-0065 결정 2
- [x] 충돌·공간 질의 — 레거시 `BoxColliderM.cs`·`MathM.cs`·`HierachyM.cs` 판정 확정: 🟡 알고리즘 구조만 승계, 코드 전량 재작성 (2026-08-11, ADR-0065). `Aabb`(16B)·`Obb`(20B) — struct 가 배열을 보유하던 레거시 구조(도형당 힙 2~3회) 폐기, SAT 는 반지름 공식으로 무할당. **미수정 버그 8건 전부 이름 붙은 회귀 테스트로 고정**(단위 테스트 먼저 — 로드맵 경고 이행): 축정렬끼리 충돌 불가·회전 무효·접촉점 절댓값 오용·Normalize(0) NaN 등. 경계 규칙 닫힌 구간 하나로 통일, 각도는 라디안 하나(#18), 좌표는 BCL `Vector2`(SIMD, 9.8)
- [x] 스냅샷 / 델타 압축 — 변경분만 전송 (2026-08-11, ADR-0064 결정 4). 수집 단 `DirtySet<T>`(중복 없는 더티 추적 + 무할당 드레인 — 레거시 `NeedPkSendM` 플래그 + `UniqueBufferBlock` 더티 큐 발상 승계) + `InterestSet` 차분(Enter=등장 스냅샷/Leave=소멸) + 1회 인코딩 브로드캐스트가 전송 경로. **필드 수준 델타 인코딩은 의도적으로 제외** — 직렬화 축·앱 도메인의 영역이고, 프레임워크가 넣으면 축 침범이다(변경 종류는 앱의 `[Flags]` enum 몫)
- [x] 벤치마크: 룸 인원 대비 브로드캐스트 비용 (2026-08-11, BENCHMARKS.md 룸 브로드캐스트 절). 128B×10/100/1,000명: 멤버당 384~433ns 로 **인원과 무관하게 일정(선형 확장)**, 전 구간 **0 B** — "조립 1회 + 멤버당 바이트 복사" 주장의 수치 근거

> **✅ 2026-08-11 — Phase 18 완료.** 선택 축 2개 신설: `ChServerM.RealTime.Rooms`(룸·1회
> 인코딩 브로드캐스트·더티 추적, ADR-0064)·`ChServerM.RealTime.Spatial`(모튼·AOI 그리드·
> 집합 차분·무할당 SAT, ADR-0065). 둘 다 Core 만 참조하고 서로도 참조하지 않는다.
> 레거시 충돌 버그 8건이 이름 붙은 회귀 테스트로 고정됐고(테스트 우선 경고 이행),
> 브로드캐스트의 커넥션 Output 소유권 문제는 파티션 배타 슬롯로 풀었다.
> 신규 테스트 58개(Spatial 37 + Rooms 21).
> 후속 관찰: 실전 조립 예제(핸들러→룸 브로드캐스트 종단, Samples)는 Phase 20 샘플
> 정리와 함께. 실 파티션(Concurrency) 위 종단 브로드캐스트 부하 측정은 수요 시.

## Phase 19 — 매치메이킹 & 레이팅 (선택 축)

대기열에서 조건에 맞는 참가자를 묶는 문제는 게임 밖에서도 나타난다 —
대전 매칭, 배차, 상담 배정. 레이팅 공식 자체는 도메인이므로 `Samples/`에 둘 수도 있다.

- [x] 레이팅 시스템 — `Samples/ChServerM.Samples.Matchmaking` 에 **Elo 참조 구현**(ADR-0004 대로 프레임워크 밖). 레거시 Glicko/WengLin 은 참조 0 준비 코드라 승계하지 않았다 — 불확실성 추적 공식이 필요하면 같은 이음새(매치 결과 → 새 레이팅)에 새로 구현한다
- [x] 매치메이킹 큐 — `ChServerM.Matchmaking.Matchmaker` (ADR-0068): **확장 창** — 대기할수록 허용 레이팅 창이 자라고, 호환은 양쪽 창이 서로를 덮을 때만. 최대 대기 초과는 억지 매치가 아니라 만료로 드러난다. 유계 큐(9.6) + 수동 자료구조·단일 소유자 계약(휠과 같은 패턴). 테스트 16개 전부 수동 시계로 결정적
- [x] 파티/그룹 매칭 — 파티는 원자 티켓(쪼개지 않는다), 팀 구성은 최장 대기 앵커 우선 first-fit-decreasing. 전역 최적 빈 패킹은 추구하지 않는다 — 패스당 비용 유계가 우선(ADR-0068 결정 4)
- [x] 매치 결과 반영 / 레이팅 갱신 — **프레임워크 밖이 설계다**(ADR-0068 결정 5): 큐는 결과를 모르고, 샘플의 32명 리그 시뮬레이션이 반영 이음새를 실증한다(1,280매치 성립·만료 0·실력-레이팅 순위 상관)

---

# Part VI — 제품화

프레임워크는 **개발자 경험이 제품**이다. 여기가 비면 아무도 쓰지 않는다.

## Phase 20 — 개발자 경험

- [x] `dotnet new` 템플릿 — `chserverm-server`, `chserverm-client` (`Templates/`. ~~NuGet 발행 전이라 ProjectReference 방식~~ → **2026-08-12 발행 후 메타 패키지 참조로 전환 완료**(`c2c7464`) — nuget.org 복원만으로 설치→생성→빌드→실왕복→제거 종단 재검증. **CI 자동 검증은 미구성** — 수요 시)
- [x] 시작 가이드 — `docs/GETTING-STARTED.md`. **실린 코드 조각 전부를 스크래치 프로젝트로 컴파일 + 실왕복 검증했다** — 그 과정에서 초안의 실제 결함(클라이언트 기본 임계값 < 최대 프레임)을 `CompositionGuard` 가 잡아 문서를 고쳤다
- [x] ⚠ **진단 분석기** — `ChServerM.Analyzers` 신설(ADR-0066, 대역 CHSM3xxx). CHSM3001 async void · CHSM3002 async 경로 블로킹 · CHSM3003 Payload 수명 위반 — 전부 레거시에서 서버를 멈추거나 데이터를 오염시킨 패턴. 기본 Warning + 좁은 판정(오탐이 진단을 끄게 만든다), 규칙마다 "조용해야 한다" 테스트 동수(15개), 샘플 3개에 적용. 후보 규칙(유계 Wait 채널 TryWrite·풀 미반납·옵션 Validate 의 현재 값 누락)은 수요 확인 후
- [x] 축 조합별 샘플 정리 (`Samples/`) — 3개: `EchoServer`(TCP+MemoryPack) · `StatelessWeb`(HTTP/2+Protobuf+병렬 실행 — **stateless-web 프로필의 첫 실행 가능 형태**, 시퀀스로 응답 짝짓기) · `GameRoom`(룸 브로드캐스트, Phase 18 후속 — 1회 인코딩·룸 격리·퇴장 3경로). 셋 다 인자 없이 실행하면 자체 검증 후 exit code 보고
- [ ] 디버깅 지원 — `DebuggerDisplay`, `DebuggerTypeProxy`, 의미 있는 예외 메시지 (진행 중: `[DebuggerDisplay("{ToString(),nq}")]` 값 타입 17종 + 예외 메시지는 아래 항목으로 완결. **`DebuggerTypeProxy` 만 남았다** — 컬렉션형 타입의 실수요 확인 후)
- [x] 에러 메시지 품질 검토 — Server/ 전수 감사(기준: 값+결과+해법) 후 지적 전량 수정. 평가는 "품질보다 일관성이 문제"였다. 수정: `DuplexPipeStream` 무메시지 5개소 · Consul 옵션 2파일(품질 저지대) · `BroadcastFrame` 이중 해제 · `TimerWheel` 오버플로(실제 값 3종) · 상태 기계 가드 7개소(1회용 계약과 재시작 경로) · 직렬화 제공자 중복 등록 · `Failed` 팩토리 허용값 · LZ4 방어 지점 · `ClusterRouteResolver` · Postgres/Redis 식별자. 감사가 제안한 "옵션 Validate 의 현재 값 포함을 CHSM 규칙으로 강제"는 분석기 후보 규칙으로 기록
- [x] API 문서 사이트 — DocFX 2.78.5 로컬 dotnet tool 고정(ADR-0067), `docs/docfx/`. API 페이지 336개 생성, 생성물은 미커밋(XML 주석이 정본). CI 편입·호스팅은 Phase 21. ~~빌드 경고 58건(cref 수준)은 릴리스 전 정리 대상~~ → **2026-08-12 전량 해소(0건)** — 정체는 cref 가 아니라 인프라 중복이었다: PublicApiAnalyzers 의 buildTransitive targets 가 `PublicAPI.*.txt` 를 자동 포함하는데 `Server/Directory.Build.props` 가 수동으로 또 포함(68건) + 메타 패키지가 docfx 제외 대상(Analyzers·SourceGen)을 참조(2건). 수동 포함 제거는 RS0016 음성 테스트로 게이트 생존 확인
- [x] 아키텍처 가이드 — `docs/GUIDE-CHOOSING-AXES.md`. 무상태/상태 유지 첫 질문부터 축별 선택 기준·근거 ADR·참조 샘플. ARCHITECTURE.md 의 낡은 "미확정" 절도 정리(ADR-0001·0013 은 확정)
- [x] 성능 튜닝 가이드 — `docs/GUIDE-PERFORMANCE.md`. BENCHMARKS.md 를 결정 순서로 재구성: 기준선·튜닝 노브(효과 순)·**기각된 최적화 8건**·측정 절차·미측정 목록. 모든 수치에 원문 절 표기
- [x] 마이그레이션 가이드 — `docs/GUIDE-MIGRATION.md`. 레거시 대응표 28건·이전 순서(ID 확정부터)·핸들러 규약 차이(CHSM 진단과 연결)·회귀 방지 체크리스트

## Phase 21 — API 안정성 & 릴리스 엔지니어링

- [x] ⚠ **SemVer 정책 문서화** — `docs/VERSIONING.md` + ADR-0069. **전 패키지 락스텝**(정본: `VersionPrefix` 0.1.0), breaking 은 계약 표면 5개(코드 API·와이어·동작·관측·분석기)별 판정표. Core 축 인터페이스 멤버 추가 = major(DIM 우회 금지), 옵션 기본값 변경 = major, 관측 이름·ID 결번 재사용 금지. 0.x 동안 파괴는 minor 승격 + 노트
- [x] API 호환성 검사 CI — **2026-08-12 활성화**(`b69581c`): `PackageValidationBaselineVersion=0.1.0`(nuget.org 기준선) + `eng/build.ps1` pack 단계(CI·로컬 동일 진입점, 오프라인은 `-SkipPack`). **양방향 검증** — 음성: 존재하지 않는 기준선 → NU1102 실패(속성이 조용히 무시되지 않음, ADR-0031 교훈 적용) · 양성: 33개 전부 0.1.0 대비 통과. 다음 릴리스에서 버전과 함께 기준선을 올린다(VERSIONING.md)
- [ ] `PublicAPI.Shipped.txt` 확정 — 1.0 공개 표면 동결 (1.0 선언 시점의 작업 — VERSIONING.md 절차 3)
- [ ] NuGet 패키징 — 축별 개별 패키지. 메타 패키지 제공 (진행 중: **축별 32개 전부 pack 성공** + 분석기 2종은 `analyzers/dotnet/cs` 경로 패키징(정적 평가 시점 산출 경로 함정·빈 snupkg NU5017 함정 해결) + **로컬 피드 소비 검증** — 패키지만으로 서버 조립·기동, 패키지 분석기에서 CHSM3001 발화 + **메타 패키지 `ChServerM` 구성 완료**(2026-08-12, ADR-0070 — realtime-stateful 최소 조합 8개 의존성, 33번째 패키지. 메타 하나만 참조한 프로젝트로 조립·기동·생성 코드 경로까지 소비 검증. 전이 PackageReference 가 메타의 직접 의존성으로 새는 것을 `PrivateAssets="all"` 로 차단). **⭐ 2026-08-12 v0.1.0 첫 발행 완료** — 저장소 공개 전환(레거시 자격증명 노출 사용자 승인) → `release.yml`(게이트→pack→증명→발행, ADR-0073) → nuget.org 33개 전부 색인 + 공개 피드 실소비 검증(메타 하나로 조립·기동·생성 코드 경로) 통과)
- [x] SourceLink + 심볼 서버 — 사용자가 프레임워크 내부를 디버깅할 수 있게 (SDK 내장 SourceLink + snupkg 30종 구성, `0.1.0+커밋SHA` 스탬프 실측. **2026-08-12 심볼 게시 완료** — `dotnet nuget push` 가 snupkg 30개를 자동 동반 푸시, nuget.org 심볼 서버 게시)
- [x] 결정적 빌드 검증 — `eng/verify-deterministic.ps1`: 같은 커밋 2회 완전 재빌드(CIB=true) SHA-256 비교. **실측: Server DLL 62개 전부 동일 해시.** nupkg 바이트 동일성(zip 메타데이터)은 발행 파이프라인 몫으로 분리
- [x] 패키지 서명 / 출처 증명(provenance) — **2026-08-12 완료**: SLSA 출처 증명(artifact attestation) + nuget.org 저장소 서명 + Trusted Publishing 발행 인증(ADR-0073 — 장수명 시크릿 0). v0.1.0 실발행에서 증명 발행·소비자 검증(`gh attestation verify`, 대상 = 워크플로 아티팩트 — nuget.org 는 저장소 서명으로 해시를 바꾼다, 실측) 통과. 저자 서명(인증서 구매)은 수요 시 별도 ADR
- [x] 릴리스 노트 자동화 — `eng/release-notes.ps1`: Conventional Commits 섹션 분류, `type!` 파괴 승격, `chore(standup)` 제외. breaking 판정 자동화는 의도적으로 안 함 — 표면 5개 점검은 사람이 diff 로(VERSIONING.md)
- [x] 지원 정책 — **2026-08-12 사용자 결정**(ADR-0072, `50c6f9b`): 최신 minor 전부 + 직전 minor 보안 6개월(major 전환기 동일 규칙) · 0.x 는 최신만, 효력은 1.0부터 · 신고 = PVR + 조정 공개 90일 · 신고 범위 명시. 정본 `SECURITY.md`(공개 저장소 Security 탭 노출)
- [x] 라이선스 확정 + 서드파티 라이선스 감사 — **2026-08-12 사용자 결정: Apache-2.0**(ADR-0071 — 특허 3조·상표 6조·기여 5조가 채택 근거, MIT·듀얼·BSL 탈락). `LICENSE`·`NOTICE` 신설(저작권 표기 "The ChServerM Authors" — 명의 확정 시 교체 가능), `PackageLicenseExpression` + 전 패키지 동봉(33개 실측, 누락 0). **감사: `THIRD-PARTY-NOTICES.md`** — 중앙 패키지 전 항목 + Server 전이 의존성까지 nuspec 실물 기준, 충돌 0(카피레프트 0건). 커밋 `653af67`, CI 초록

## Phase 22 — 1.0 출시

- [x] Native AOT 샘플 전체 검증 — 2026-08-12. 샘플 4종 전부 csproj 에 `PublishAot` 선언(eng/build.ps1 의 AOT 게이트가 자동 발견 — 전역 속성 전달은 NETSDK1207 함정) 후 publish(경고=오류) + 실행 자체 검증 통과. StatelessWeb(Kestrel h2c + Protobuf)도 첫 시도 통과. EchoServer `--serve` 에 SIGTERM 정상 종료 추가(PosixSignalRegistration — 없으면 K8s 롤링 업데이트마다 드레인 없이 즉사). **원격 CI 로 양 OS × 4종 확증 완료**(실행 31551677209 — ubuntu 도 linux-x64 publish + 실행 검증 완주. AOT 단계 1개→4개로 잡 +2~3분)
- [ ] 컨테이너 이미지 + 배포 예제 (K8s 매니페스트) (진행 중: `deploy/echo-server/` — Dockerfile(SDK 태그를 global.json 피처 밴드에 고정, Native AOT + runtime-deps + 비루트, 콘텐츠 ~51MB) + 루트 `.dockerignore` + K8s Deployment/Service 매니페스트. 이미지 빌드→기동→외부 TCP 클라이언트 200회 왕복→`docker stop`(SIGTERM) 드레인 exit 0 까지 실증, 매니페스트는 kubeconform -strict 통과. **남은 것: 실클러스터 apply·rollout 검증** — 로컬 K8s 클러스터 부재로 미실증)
- [ ] 전 Phase 게이트 재확인 (진행 중: 2026-08-12 1차 재점검 — **게이트 13개(Phase 0~12) 중 11개 현행 증거로 충족**(CI 3잡 초록 + AOT 4종 양 OS + RS0016 음성 테스트 + 상시 스위트 + BENCHMARKS 기준선 2026-08-08). **② 확장성 5지점 곡선 2026-08-12 완료** — Docker 미실행·ENV-B 정합 확인 후 `eng/scaling-gate.ps1` 전체 곡선 통과(1·2·4·8·16코어 전부 하한 이상, 16코어 14.90×/효율 93.1%, 08-07 기준선 14.67× 유지·소폭 상회). 게이트 도구 함정 1건 수정·푸시(`f7c508b` — 어피니티가 빌드까지 1코어에 묶어 BDN 클린 빌드가 기본 2분 타임아웃에 죽던 거짓 통과, `--buildTimeout 900` 으로 해소). **① 부분 soak 2026-08-13 통과** — 11h48m 연속 churn(`CHSM_SOAK_SECONDS≈42.5k`, InMemory, 세션 독립 프로세스)에서 커넥션 슬롯 0 드레인 + 메모리 임계 내 평탄, `dotnet test` exit 0(1/1). **잔여 = 정식 24h 판**(`=86400`) — 부분 통과로 크게 de-risk 됐으나 게이트 형식 요건은 완전 24h(수치는 통과 시 억제돼 미포착 — 정식 판은 상세 로거로). 상세 표는 standup history 2026-08-12·13)
- [ ] 최종 성능 기준선 공표
- [x] 문서 전체 검토 — 죽은 링크, 낡은 예제 (2026-08-12 `3e5c3fe`: **링크 전수 검사 61개 md → 깨진 링크 0**(레거시 인덱스 7건 수정) · 낡은 서술 정정(VERSIONING·ROADMAP·시작 가이드·README 의 발행 전 문구) · ARCHITECTURE 최신화(Matchmaking·메타 패키지) · DocFX 경고 0(`c8d874f`) · 시작 가이드 조합은 템플릿 종단 검증이 재실증. 기록성 문서(history·ADR·BENCHMARKS)는 추가 전용 원칙대로 불변)
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
