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

- [ ] `ChServerM.sln` 생성, `Server/` `Client/` `Tests/` `Bench/` `Samples/` 솔루션 폴더 구성 (진행 중: `.NET 10` SDK가 `ChServerM.slnx`로 생성. `Server/`·`Tests/` 폴더만 존재 — `dotnet sln add`가 프로젝트 없이 폴더를 만들 수 없어 `Client/`·`Bench/`·`Samples/`는 첫 프로젝트와 함께 추가)
- [x] `Directory.Build.props` — `net10.0`, C# 14, nullable, `AllowUnsafeBlocks`, `IsAotCompatible`, ServerGC, TieredPGO
- [x] `Directory.Packages.props` — 중앙 패키지 버전 관리 활성화
- [x] `.editorconfig` — 코드 스타일 + 분석기 심각도 (Performance·Reliability 카테고리를 error로 승격)
- [x] `.gitattributes` — 줄바꿈 정규화를 저장소 제어로. `core.autocrlf` 의존 제거, `.editorconfig`와 정합
- [x] `ChServerM.Core` 프로젝트 생성 — 서드파티 의존 0 검증 테스트 포함 (2중 가드: `CHSM0001` MSBuild + `CoreDependencyTests`. 참/거짓 양성 모두 검증)
- [ ] CI 스크립트 (build + test + AOT 컴파일 검증) (진행 중: `eng/build.ps1` + GitHub Actions 매트릭스 동작 확인. AOT 컴파일 검증은 실행 프로젝트가 없어 미수행 — 스크립트가 사유를 출력하며 Part II에서 자동 활성화)
- [ ] `Bench/` 골격 — BenchmarkDotNet 프로젝트. 측정 환경 프로필을 `docs/BENCHMARKS.md`에 기록
- [ ] 코드 커버리지 수집 (coverlet) + CI 리포트. 임계값은 Core 추상화 확정 후 설정
- [ ] ⚠ **public API 승인 파일 게이트** — `Microsoft.CodeAnalysis.PublicApiAnalyzers`. `PublicAPI.Shipped.txt`/`Unshipped.txt`로 공개 표면 변경을 리뷰에 노출시킨다. 상업용 라이브러리에서 이걸 나중에 켜면 이미 굳은 API를 되돌릴 수 없다
- [ ] NuGet 취약점 감사 — `dotnet list package --vulnerable --include-transitive`를 CI에서 실패 조건으로
- [ ] 의존성 업데이트 자동화 — Dependabot 또는 Renovate
- [x] `LegacyServer/` + `LagacyClient/` 전수 정독 — 27,300줄 / 문서 14종. 결과: `docs/legacy/`(인덱스: [00-overview](legacy/00-overview.md))

**게이트**: CI가 build + test + 취약점 감사를 통과하고, public API 게이트가 켜져 있을 때.

## Phase 1 — Core 추상화

**가장 중요한 단계.** 여기서 그은 경계가 프레임워크의 확장성을 결정한다. 구현은 넣지 않는다.
Core에 들어간 인터페이스는 되돌리기 비용이 가장 크다 — 전부 ⚠로 취급한다.

### 기본 계약 (다른 모든 축이 이것에 의존한다)

- [ ] ⚠ **에러 모델** — 핫패스는 예외를 쓰지 않는다. `TryXxx` + 결과 구조체(`OperationResult`/`FrameReadResult` 등) 규약과 에러 코드 체계를 먼저 확정한다. 이걸 나중에 바꾸면 전 축의 시그니처가 흔들린다
- [ ] ⚠ **생명주기·취소 계약** — `CancellationToken` 전파 규칙, `IAsyncDisposable` 규약, graceful vs abortive 종료 구분
- [ ] ⚠ **ID 타입** — `ConnectionId`, `SessionId`, `MessageId`, `NodeId`, `ObjectId`, `JobId`. `readonly struct` + 강타입. `long`/`int` 원시 타입을 API에 노출하지 않는다
  - ⚠ **`ObjectId`에 노드 성분을 반드시 포함한다.** 레거시 `GlobalM.MakeGameOid()`는 프로세스 전역 단조 카운터라 다중 노드에서 충돌하고 재시작 시 재사용된다 — **Phase 15의 선결 조건이며, 지금 `long` 증분으로 굳히면 되돌릴 수 없다.** Snowflake 계열 또는 노드별 블록 할당 ([06-session-user](legacy/06-session-user.md#globalm--compressandencryptmanm))
  - **`SessionHandle`은 세대(generation) 카운터를 포함**해 삭제된 세션 접근을 할당 없이 O(1)로 판별한다 (레거시 `UserM` 래퍼가 매 조회 힙 할당 + null 분기 버그)
  - **존재하지 않는 세션의 기본값은 가장 제한적인 값**으로. 레거시는 `AllowedPkState` 기본값이 `A_SC_ANY_STATE`(전부 허용)였다
- [ ] **시간 추상화** — `IClock` / `ITimeProvider`(.NET `TimeProvider` 채택 검토). 틱·타임아웃·재시도 테스트가 실제 시간에 의존하면 테스트가 불안정해진다
- [ ] **진단 계약** — `ActivitySource`/`Meter` 이름 규약, 이벤트 ID 체계. 관측 축(Phase 11)이 이것에 붙는다

### 축 인터페이스

- [ ] `IMessageSerializer` / `IMessageSerializer<T>` — `Span<byte>` 기반, 할당 없는 시그니처
- [ ] ⚠ `IFrameDecoder` / `IFrameEncoder` — `ReadOnlySequence<byte>` 입력. **ADR-0002를 코드로 굳히는 지점.** 헤더는 고정 `struct`, 직렬화는 페이로드 전용. 이 경계가 프레이밍/직렬화 두 축의 독립 교체를 가능하게 한다
- [ ] `IServerTransport` / `IClientTransport` / `IConnection` — 전송 중립 커넥션 추상화
- [ ] `IMessageDispatcher` / `IMessageHandler<T>` — 디스패치 계약
- [ ] `IServerMiddleware` + 파이프라인 델리게이트 타입 — Chain of Responsibility 계약
- [ ] ⚠ **`IExecutionModel` — 유저별 순서 보장을 *표현할 수 있어야* 한다**. 계약이 이 전략을 강제하는 것이 아니라, 필요한 프로필이 선택할 수 있어야 한다는 뜻이다. `realtime-stateful`은 이 전략을 쓰고 `stateless-web`은 병렬 실행을 쓴다 — **하나의 계약이 양쪽을 수용해야 한다.** 근거: 레거시 `UserM.MemPkActionBlock`(TPL Dataflow, 유저 단위 직렬) vs `NetworkM.gMemPkActionBlock`(글로벌) ([01-network-transport](legacy/01-network-transport.md#sendpacketgroupm))
- [ ] `ISessionStore` / `ISession` — 상태 저장 추상화
- [ ] `IServerLogger` / `IMetricsSink` — 관측 추상화
- [ ] `IPayloadCodec` — 압축 계약
- [ ] `ITransportSecurity` — 전송 보안 계약
- [ ] `IAuthenticator` / `IAuthorizationPolicy` — 인증·인가 계약 (Phase 9에서 구현)
- [ ] `IRateLimiter` / `IAdmissionControl` — 과부하 제어 계약 (Phase 10에서 구현)
- [ ] `IClusterMembership` — 클러스터 계약 (Phase 15에서 구현)

### 마감

- [ ] 각 축의 `XxxOptions` 타입 + `IValidateOptions<T>` 검증 계약
- [ ] Core 공개 표면 리뷰 — 인터페이스 개수·메서드 수를 세고 줄일 수 있는 것을 줄인다. 추상화 자체가 비용이다
- [ ] `docs/ARCHITECTURE.md`에 의존 방향·확장 지점 확정 기록

**게이트**: Core가 컴파일되고, 무의존 가드를 통과하고, 모든 축 인터페이스에 XML 문서가 있을 때.

## Phase 2 — 호스팅 & 조립 (Builder)

축을 실제로 "골라 끼우는" 표면. Phase 1 추상화가 진짜 조립 가능한지 검증하는 단계다.

- [ ] `ServerBuilder` 플루언트 API — `.UseTransport()` `.UseSerializer()` `.Use<TMiddleware>()`
- [ ] DI 컨테이너 통합 (`Microsoft.Extensions.DependencyInjection`)
- [ ] 미들웨어 파이프라인 컴파일 — 델리게이트 체인. **조립 비용은 시작 시점에 지불하고 핫패스에 동적 결정을 남기지 않는다**
- [ ] 서버 생명주기 — 시작 / graceful shutdown / 커넥션 드레인 / 강제 종료 타임아웃
- [ ] 옵션 검증 — 잘못된 축 조합을 **시작 시점에** 실패시킨다 (런타임에 발견되면 프로덕션 장애)
- [ ] 설정 소스 — `IConfiguration` 통합, 환경별 오버레이. 레거시 INI 방식은 폐기 — 1073줄 파서로 IP·포트 2개를 읽고 있었다 ([11-data-table](legacy/11-data-table.md#inifilem--inioptionm))
- [ ] `ClientBuilder` 대칭 구성
- [ ] ⚠ **인메모리 루프백 전송** — `ChServerM.Transport.InMemory`. 소켓 없이 파이프라인을 끝까지 도는 전송. **전송 축의 두 번째 구현체 역할**을 싸게 수행해 추상화가 진짜 전송 중립인지 조기에 증명한다. 통합 테스트의 기본 전송이 되어 테스트 속도도 올라간다 (Kestrel의 인메모리 전송과 같은 발상)
- [ ] 조립 테스트 — 축을 교체해도 컴파일·동작이 유지되는지 검증
- [ ] 첫 실행 가능 프로젝트(`Samples/`) — 이 시점부터 CI의 **AOT 컴파일 검증이 활성화**된다

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

- [ ] ⚠ **와이어 헤더 레이아웃 확정** — 고정 크기 `struct` + `MemoryMarshal`/`BinaryPrimitives`. **버전 필드를 반드시 포함한다** (레이아웃 변경이 와이어 호환성을 직접 깨므로). 엔디안 규약 명시
- [ ] length-prefix 디코더 — varint / fixed32 두 가지
- [ ] 부분 프레임 처리 — `ReadOnlySequence<byte>` 세그먼트 경계를 넘는 헤더/페이로드
- [ ] **프레임 오류 처리 정책** — 체크섬 불일치·길이 이상 시 커넥션 종료. 레거시는 예외를 잡고 루프를 계속해 상태 머신이 어긋난 채 파싱을 이어갔다(프레이밍 desync). `TryXxx`로 처리하고 오류 프레임은 커넥션을 닫는다
- [ ] 최대 프레임 크기 상한 — 상한 없는 length-prefix는 메모리 고갈 공격 벡터다
- [ ] 프레임 조립 상태 머신 — 레거시 `eToReadState` 5단 구조를 참고하되 할당 없이 재작성
- [ ] 퍼징 테스트 — 임의 바이트열·잘린 프레임·거대 길이 필드를 던져 크래시/무한 루프가 없음을 확인
- [ ] 벤치마크: 프레임당 파싱 비용, 할당 0 확인

**게이트**: 퍼징이 크래시 없이 통과하고 프레임당 할당이 0일 때.

## Phase 5 — TCP 전송 (첫 실동 구현)

- [ ] ⚠ Kestrel Socket Transport 재사용 vs 순수 `Socket`+Pipelines — 양쪽 프로토타입 벤치마크 후 **ADR-0001 확정**
- [ ] `ChServerM.Transport.Tcp` — accept 루프, 수신/송신 Pipelines
- [ ] 백프레셔 — `PipeOptions` pause/resume 임계값 **명시적 설정**. 레거시는 기본값에 방치했다
- [ ] 커넥션 생명주기 — idle timeout, half-open 감지(keepalive), graceful close, RST 처리
- [ ] **종료 레이스 처리** — 로그인 완료 전 연결이 끊기는 경우. 레거시는 1초 지연 타이머로 대응했다(실전에서 나온 장치). 동등한 보장을 구조적으로 제공
- [ ] 송신 배칭 — 작은 패킷 다수를 묶어 syscall 수를 줄인다. 레거시 `SendPacketGroupM` 참고
- [ ] Nagle / `TCP_NODELAY` 정책 — 실시간 서버는 보통 비활성화. 옵션으로 노출
- [ ] 소켓 옵션 노출 — 버퍼 크기, linger, reuseaddr
- [ ] 통합 테스트: 연결/에코/대량 동시 접속/비정상 종료
- [ ] 크로스 플랫폼 검증 — Linux/Windows 소켓 동작 차이 (CI 매트릭스가 이미 양쪽을 돌린다)
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


- [ ] `ChServerM.Concurrency` — 채널 워커 풀 모델
- [ ] ⚠ **유저별 순서 보장 구현** — `IExecutionModel` 계약의 실체. 유저 단위 직렬 실행 + 글로벌 병렬 실행 분리
- [ ] 스레드-퍼-코어 모델 + CPU 어피니티
- [ ] false sharing 회피 — 캐시 라인 패딩. 샤드별 카운터·상태를 같은 라인에 두지 않는다 (9.4)
- [ ] 스케줄러 공정성 — 한 유저가 워커를 독점하지 못하게
- [ ] 데드락·경합 테스트 — **반복 실행**으로 재현. 동시성 버그는 단발 테스트로 안 잡힌다 (9.9)
- [ ] ⚠ **예외 안전성 테스트** — 작업 핸들러가 던졌을 때 실행기가 계속 동작하는지. 레거시는 `finally` 부재로 **디스패처와 스레드가 영구 정지**했다 (9.2)
- [ ] 액터 모델 어댑터 검토 (Orleans / Proto.Actor) — Core에 침투 금지
- [ ] ⚠ **벤치마크: 코어 수 대비 확장성 곡선** — 1·2·4·8·16코어. "병렬화했다"가 아니라 **"선형에 근접한다"** 를 증명한다 (`CLAUDE.md` 9.9)
- [ ] 유저별 순서 보장 오버헤드 측정 — 샤딩 유/무 비교
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
