# 감사 08 — 관측·로깅·분석기·샘플·빌드 인프라 (횡단 관심사)

> 전수 감사 2026-08-18. 대상: `ChServerM.Observability` · `ChServerM.Diagnostics.Http` ·
> `ChServerM.Logging.Extensions` · `ChServerM.Analyzers` · `ChServerM.Security.AspNetIdentity` ·
> `Samples/` · `Directory.Build.props` · `Directory.Packages.props` · `global.json` · `eng/` ·
> `.github/workflows/` 정독. 우선순위: P0=정확성/1.0 필수 · P1=중요 · P2=권장 · P3=선택.
> 인덱스: [00-summary.md](00-summary.md)

## 요약

전반적으로 매우 잘 관리된 영역이다. GC 설정 오타 사고(ADR-0031)의 재발 방지는
선언(`Directory.Build.props`)과 산출물(`EchoServer.runtimeconfig.json`의 `System.GC.Server: true`,
`System.Runtime.TieredPGO: true`) 양쪽에서 교차 확인했고 현재 정상이다. 샘플 4종은 하드 룰
위반이 전무했으며(`Task.Run`/`.Result`/`Thread.Sleep`/무제한 채널/`lock`/`async void` 0건), 유계
채널 + `WriteAsync` 사용을 주석으로 가르치는 교본 품질이다. 큐 깊이·거부 메트릭(9.6)은
`ExecutionPartition`에서 실제로 방출되고, 빌드 스크립트는 조용한 실패를 막는 방어(감사 JSON
파싱, AOT 게이트 소멸 시 fail-closed, 벤치 필터 함정 문서화)가 인상적이다.

다만 헬스 엔드포인트 accept 루프의 조기 종료 가능성(P1) 1건, 그리고 "선언만 있고 산출물 검증이
없는" 패턴의 잔재(미방출 메트릭 상수, Shipped 파일 미이동, CI의 GC/결정성 검증 부재) 몇 건이
1.0 전에 정리할 대상이다.

## 발견 사항

### [P1] O-1. `HealthHttpEndpoint` accept 루프가 일시적 예외 하나로 영구 종료된다

- **위치**: `Server/ChServerM.Diagnostics.Http/HealthHttpEndpoint.cs:149-176`
- **현재 구현**: `AcceptLoopAsync`가 `GetContextAsync()`의 `HttpListenerException`을 무조건 "종료
  신호"로 해석하고 `return`. `ObjectDisposedException`/`InvalidOperationException`도 동일.
- **문제**: `HttpListenerException`은 종료(Stop) 외에도 일시적 원인(클라이언트 abort, 커널 큐
  오류 등)으로 발생할 수 있다. 그 경우 서버 본체는 살아 있는데 **헬스 엔드포인트만 조용히
  죽는다** — 요청별 격리(9.2)를 문서로 표방하면서 accept 단계는 격리가 없다. k8s 환경이면
  liveness 프로브 실패 → 정상 프로세스가 재시작당하는 역설이 생긴다.
- **대안**: catch 블록에서 `_stopping.IsCancellationRequested || !_listener.IsListening`일 때만
  `return`, 그 외에는 `continue`(+ 선택적으로 실패 카운터).
- **1.0 전 필수**: 예 (정확성). / **난이도**: 낮음

### [P2] O-2. `chserverm.backpressure.duration` — 선언만 있고 아무 데서도 방출되지 않는 공개 메트릭 계약

- **위치**: `Server/ChServerM.Core/Diagnostics/DiagnosticNames.cs:86` (전 저장소 grep 참조 0건)
- **문제**: VERSIONING.md는 메트릭 이름을 계약 표면(제거 = major)으로 규정한다. 나오지 않는
  메트릭을 계약으로 실은 상태 — 사용자가 대시보드를 만들면 영원히 0이고, 나중에 지우면 major다.
- **대안**: 1.0 전에 (a) 백프레셔 대기 측정을 실제 구현하거나 (b) 상수를 제거(0.x 동안은 minor
  승격으로 가능).
- **1.0 전 필수**: 예 (계약 동결 전 정리). / **난이도**: 구현 시 중간 / 제거 시 낮음

### [P2] O-3. 히스토그램·카운터에 단위/버킷 메타데이터가 없다 — OTel 익스포터를 붙이는 순간 지연 히스토그램이 무의미해진다

- **위치**: `Server/ChServerM.Observability/MeterMetricsSink.cs:71-82`
  (`CreateHistogram<double>(n)` — unit·advice 미지정), `DiagnosticNames.cs:74` (초 단위 규약)
- **문제**: 지금의 소비자(dotnet-counters/dotnet-monitor)는 분위수 집계라 문제가 안 보이지만,
  ADR-0020이 예고한 "OTel을 Meter 구독으로 얹는" 순간 OTel 기본 명시 버킷(0, 5, 10, 25…)에 초
  단위 값(~0.001)이 전부 첫 버킷으로 들어가 p50/p99가 무의미해진다.
- **대안**: `CreateHistogram`에 `unit: "s"` + `InstrumentAdvice<double>`로 초 스케일 버킷(예:
  0.001~10) 지정. bytes 카운터도 `"By"` 단위. `IMetricsSink` 시그니처 변경 없이 어댑터가 알려진
  이름(`MetricNames`)에 대해 메타데이터를 붙이면 된다.
- **1.0 전 필수**: 권장 (어댑터 내부 수정만으로 끝나는 지금이 싸다). / **난이도**: 낮음

### [P2] O-4. GC/runtimeconfig 산출물 검증이 여전히 "수동 grep 안내"뿐이다

- **위치**: `Directory.Build.props:48-66` (검증 방법 주석), `.github/workflows/ci.yml` ·
  `Tests/` (자동 검증 부재 확인)
- **현재 상태**: 철자는 정확하고 산출물도 정상임을 이번 감사에서 수동 확인. 그러나 3개월 잠복
  사고의 재발 방지 장치가 주석의 grep 안내뿐이다.
- **대안**: `eng/build.ps1` AOT 단계(또는 build 직후)에 샘플 `*.runtimeconfig.json`에서
  `"System.GC.Server": true` 부재 시 exit 1 하는 5줄짜리 게이트 추가.
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P2] O-5. v0.1.0 발행 후 `PublicAPI.Shipped`/`AnalyzerReleases.Shipped` 미이동 + VERSIONING.md 자기모순

- **위치**: 전 어셈블리 `PublicAPI.Shipped.txt`(빈 파일),
  `Server/ChServerM.Analyzers/AnalyzerReleases.Shipped.md:1`, `docs/VERSIONING.md:10` vs `:54`
- **문제**: 0.1.0이 발행됐는데 모든 API가 `Unshipped`에 남아 있다. VERSIONING.md 10행은 "1.0에
  전량 이동", 54행(릴리스 절차 3)은 "릴리스마다 이동"으로 서로 다르게 읽힌다. 현재 상태에서는
  "Shipped에서 줄 제거 = 파괴적 변경" 게이트가 실질 무력(PackageValidation 기준선 0.1.0이
  보완하고 있어 실해는 없음). 문서 모순은 1.0 절차에서 혼선을 만든다.
- **대안**: 0.x 정책을 "이동은 1.0에 1회"로 확정한다면 VERSIONING.md 54행에 "(1.0 이후)" 단서를
  달고 CLAUDE.md 8.1 서술과의 관계를 명시. 릴리스마다 이동이 맞다면 지금 0.1.0 분량을 이동.
- **1.0 전 필수**: 예 (1.0 선언 자체가 이 이동을 요구한다 — 그 전에 규칙을 하나로).
- **난이도**: 낮음

### [P2] O-6. 결정적 빌드 검증(`verify-deterministic.ps1`)이 어느 파이프라인에도 연결돼 있지 않다

- **위치**: `eng/verify-deterministic.ps1`, `.github/workflows/release.yml`
- **문제**: 출처 증명(attestation)까지 하는 릴리스 파이프라인에서 정작 "같은 커밋 = 같은
  바이너리"는 검증되지 않는다. 결정성 회귀(비결정적 소스 생성기 출력 등)는 조용히 들어온다.
- **대안**: release.yml의 pack 앞(또는 주간 스케줄 워크플로)에 스텝 추가. 2회 전체 재빌드 비용이
  크면 릴리스 태그 시에만.
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P2] O-7. 분석기 커버리지 공백 — 레거시에서 실제 데이터를 유실시킨 패턴 2개가 아직 사람 주석에만 의존

- **위치**: `Server/ChServerM.Analyzers/`(CHSM3001~3003이 전부), 주석 방어만 있는 곳:
  `Samples/ChServerM.Samples.EchoServer/Program.cs:268-270, 322-324`
- **문제**: async void(3001)·async 내 블로킹(3002)·Payload 수명(3003)은 구현 품질이 좋다. 그러나
  CLAUDE.md 하드 룰 중 컴파일 타임 강제가 가능한 두 가지가 빠져 있다: ①
  `Channel.CreateUnbounded` 사용(9.6), ② `BoundedChannelFullMode.Wait` 채널에 대한 `TryWrite`
  (9.6 — 레거시가 실제로 부하 시 패킷을 유실한 조합, 지금은 샘플 주석 2곳이 유일한 방어).
- **대안**: CHSM3004(CreateUnbounded 경고), CHSM3005(Wait 모드 유계 채널 + TryWrite). ②는 생성
  지점과 사용 지점이 떨어져 있어 휴리스틱(같은 메서드/필드 초기화 추적)부터 시작 — 오탐 시
  사용자에게 끄게 만드는 것이 최악이라는 기존 원칙 유지.
- **1.0 전 필수**: 아니오 (minor 추가 가능). / **난이도**: ① 낮음, ② 중간~높음

### [P2] O-8. 헬스 프로브 처리가 순차 + 무타임아웃 — 느린 프로브가 liveness까지 지연시킨다

- **위치**: `Server/ChServerM.Diagnostics.Http/HealthHttpEndpoint.cs:174, 239`
- **문제**: readiness 체크가 외부 저장소(예: Redis 헬스 체크)에 걸려 수십 초 매달리면 뒤에 온
  liveness 요청도 응답을 못 받아 kubelet timeout → 재시작. "순차가 단순·결정적"이라는 근거는
  프로브가 빠르다는 전제 위에서만 성립.
- **대안**: 프로브 호출에 `CancellationTokenSource(옵션화된 타임아웃, 예: 5s)` 연결 후 타임아웃 시
  503. 동시 처리까지는 불필요.
- **1.0 전 필수**: 권장 (`HealthHttpOptions` 타임아웃 옵션 추가는 비파괴). / **난이도**: 낮음

### [P3] O-9. MetricsMiddleware 실패 태그 — `error_code` 태그에 DispatchStatus를 싣고, 실패마다 `enum.ToString()` 할당

- **위치**: `Server/ChServerM.Hosting/Dispatch/MetricsMiddleware.cs:79-82`
- **문제**: 문서에는 "상태명 태그"라 쓰고 실제 태그 이름은 `TagNames.ErrorCode`("error_code")를
  재사용 — `ErrorCode` 태그의 문서 계약은 `Diagnostics.ErrorCode` 값인데 여기선 `DispatchStatus`
  이름이 들어가 대시보드에서 두 의미가 한 태그에 섞인다. 실패 경로 한정이지만 enum
  `ToString()`은 호출마다 할당(→ 02-hosting H-4와 동일 사안의 태그 의미 측면).
- **대안**: `status` 전용 태그 이름 추가(또는 문서를 실태에 맞춤) + 상태별 문자열 사전 캐시.
- **1.0 전 필수**: 태그 의미는 관측 계약이므로 1.0 전 정리 권장. / **난이도**: 낮음

### [P3] O-10. `MeterMetricsSink.ObserveCounter` 중복 등록 무방비 + Meter 소유권

- **위치**: `Server/ChServerM.Observability/MeterMetricsSink.cs:91-105`
- **문제**: 같은 이름으로 두 번 부르면 관측 계측기가 2개 생겨 값이 중복 보고된다. 방지는
  `BufferPoolMetrics` 문서의 "한 번만 부른다" 문장뿐. Counter/Histogram은 캐시로 막으면서
  Observable만 무방비인 비대칭.
- **대안**: 이름 기준 등록 캐시로 두 번째 등록을 무시하거나 던진다.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] O-11. `Directory.Packages.props` — ItemGroup Label 부정확, 밴드 불일치

- **위치**: `Directory.Packages.props:128-146` (Label="Compression" 안에
  StackExchange.Redis·Npgsql), `:86` (System.IO.Hashing 10.0.5 vs MEL 계열 10.0.10)
- **대안**: `Label="Persistence"` 분리, Hashing은 하한(≥10.0.5) 제약이므로 10.0.10 밴드로 정렬.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] O-12. 패키지 업그레이드 후보 목록 (메이저는 ADR 필요 — 목록만)

- **xunit 2.9.3 → v3**: 주석 스스로 "Assert.Skip은 v3 기능"이라 적었다. v3 전환 시
  `Xunit.SkippableFact` 의존 제거 + 적합성 스위트의 건너뛰기 의미가 1급 기능이 된다. 메이저 →
  ADR 대상.
- **Microsoft.NET.Test.Sdk 17.14.1**: .NET 10 시대의 MTP(Microsoft.Testing.Platform) 전환 검토와
  묶어서.
- **BenchmarkDotNet 0.15.8 / MessagePack 3.1.8 / Grpc.Tools 2.83.0 / Google.Protobuf 3.35.1**: 정기
  패치 점검 대상(보안 릴리스 추적은 audit 게이트가 이미 커버).
- **Microsoft.CodeAnalysis.CSharp 4.14.0**: 의도적 고정(호스트 컴파일러 하위호환) — 올리지 말 것.
- **1.0 전 필수**: 아니오. / **난이도**: 항목별 상이

### [P3] O-13. 다음 감사 라운드 메모 — Samples는 3종이 아니라 4종

- `Samples/ChServerM.Samples.GameRoom`이 추가로 존재하며 다른 샘플과 같은
  규율(`PublishAot=true` + CHSM 분석기)을 따른다. 이번 라운드에서는 위험 패턴 grep만 수행(0건),
  룸 축 사용법 정밀 감사는 07 문서의 격리 검증으로 커버됨.

## 잘 된 부분 (관점별 확인 결과)

- **런타임 설정**: 철자 정확, 산출물 교차 확인 통과. `IsAotCompatible` 전역 + Analyzers만 명시적
  opt-out(netstandard2.0 근거 주석). AOT는 분석기(정적)와 publish+실행(동적)의 이중 증명
  구조이고, 게이트 소멸(선언 삭제) 시 fail-closed.
- **관측**: 이름은 `chserverm.` 접두 + 소문자·점 구분으로 OTel 관례 정합. 카디널리티 규약이
  `TagNames` 문서에 명문화(커넥션 ID는 span 전용), 핫패스 태그는 파티션별 사전
  할당(`ExecutionPartition._metricTags`)·`TagList` struct 경유로 무할당. 큐 깊이/거부 메트릭
  실방출로 9.6 충족. 풀 카운터는 push 대신 pull(ObserveCounter)로 핫패스 비용 0.
- **로깅**: 어댑터는 `TState` 재포장 없는 pass-through라 구조체 상태 박싱이 없고, "프레임당
  로깅을 하지 않는다"는 설계 결정(ADR-0030)이 무할당 주장의 근거로 문서화. `LoggerMessage`
  소스젠 부재는 이 설계에서는 결함이 아니다.
- **자체 분석기**: operation/symbol action, 컴파일레이션당 타입 해석 1회, 동시 실행 활성 등
  최신 API 사용. 오탐 회피를 명시적 설계 기준으로 삼은 점이 좋다.
- **CI**: 액션 버전 최신(checkout@v5, setup-dotnet@v5, cache@v4 등), global.json 단일 정본, NuGet
  캐시 키 적절, 로컬=CI 단일 스크립트, 벤치 게이트의 비율 판정 + 고의 회귀로 임계를 역산한
  캘리브레이션(`bench-gate.json`의 `regressionRatio`)은 모범 사례 수준.
- **샘플 4종**: 하드 룰 위반 0건. 유계 채널 + `WriteAsync` 사용을 주석으로 가르치는 교본 품질.
