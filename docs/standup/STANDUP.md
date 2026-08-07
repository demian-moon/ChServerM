# ChServerM — 현재 상태

**최종 갱신**: 2026-08-07
**현재 단계**: **Part III — Phase 11 관측**(분산 트레이싱 ✅ / 헬스 체크 축 — 생산·집계·프로그래밍 API, HTTP 노출만 후속). Phase 9 보안 ✅ / Phase 10 복원력 게이트 실질 충족 / Phase 11 메트릭 게이트 충족
**진행률**: 102/222 항목 (Phase 0 `13/17` · 1 `15/21` · 2 `7/11` · 3 `4/6` · 4 `10/10` · 5 `12/12` · 6 `7/7` · 7 `5/7` · 8 `8/16` · 9 `13/13` · 10 `3/10` · 11 `5/9`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Part II 데이터 경로 게이트 전부 ✅** — 프레이밍·버퍼(50ns/0B)·TCP(1만 접속·169k RPS)·
  직렬화 3종·디스패치 소스 제너레이터(AOT)·실행 모델(코어 효율 95%)
- **Phase 9 보안 ✅ 완료(13/13)** — 위협 모델 / TLS 1.3 + 인증서 회전 / 버전 협상 /
  상태 화이트리스트 / 인증(+리플레이 가드·해셔) / 인가 2단 / 압축(LZ4) / 시크릿 관리 /
  입력 검증 / `/security-review`(신규 취약점 0건)
- **Phase 10 복원력 — 게이트 실질 충족 ✅** — 수용 제어 `IAdmissionControl`(ADR-0021) +
  속도 제한 `IRateLimiter` + soak 하네스(짧은 판 평탄, 정식 24h 만 수동/CI). **2026-08-07 후속**:
  파티션 백프레셔 관측 배선(`5c9059b`) — `PartitionWorkRejected`·`PartitionQueueDepth` 방출.
  `RejectedByBackpressure`(DispatchStatus)는 자연 생산자 없음을 문서로 확정(큐잉 디스패치 모델용 예약)
- **Phase 11 관측 — 메트릭 게이트 충족 ✅ (ADR-0020)** — `IMetricsSink`(Core) +
  `MeterMetricsSink`(BCL Meter). `UseMetrics()` 한 줄이 커넥션·디스패치·처리량·실패를 데코레이터로
  배선. 켠 ~72ns·할당 0
- **Phase 11 분산 트레이싱 ✅ 완료 (2026-08-07, ADR-0022)** — `TracingMiddleware` 가
  `ActivitySource`("ChServerM")로 프레임마다 `Dispatch` span(`message_id`·`connection_id`
  태그, 비-Handled → Error). `UseTracing()` 한 줄. **fast-path**: 리스너 없으면 async 래퍼 없이
  통과 → 8ns/0B(기준선 6ns) — 메트릭 미들웨어 async 비용(43ns)을 회피. **크로스 스레드 부모
  전파**(`1417941`): `TracingConnectionHandler` 가 커넥션 span 컨텍스트를 `ConnectionTraceFeature`
  로 실어, 파티션 스레드의 디스패치 span 이 명시적 부모로 읽는다(`Activity.Current` 안 흐름).
  실행 모델 e2e 로 자식 링크 고정. `dotnet-trace` 즉시 연동
- **Phase 11 헬스 체크 축 — 생산·집계·프로그래밍 API ✅ (2026-08-07 `a1c581d`, ADR-0023)** —
  Core `IHealthCheck`/`HealthStatus`/`HealthReport`(무의존) + `HealthCheckService`(최악 우선
  집계·항목별 try/catch). 내장 **readiness**=생명주기(`ServerLifecycleState`, `UnbindAsync`→
  Draining=not-ready) · **liveness**=`PartitionedExecutionModel` 이 `IHealthCheck` 구현(파티션
  스레드 생존, 호스팅이 옵트인 자동 등록). `server.Health.CheckHealthAsync(probe)`. HTTP 노출만 후속
- 테스트 **660개** 통과, 전체 게이트(-WarnAsError 클린 빌드·audit·AOT publish+실행) 통과

## 진행 중

- **헬스 체크 — HTTP 노출만 남음** — 생산·집계·프로그래밍 API 는 됨. `/healthz`·`/readyz`
  엔드포인트는 별개 축(HTTP 관리 서버: HttpListener vs Kestrel ADR)이라 후속. k8s 프로브가
  직접 쓰려면 필요
- **Phase 11 후속** — 프레임당 바이트·풀 카운터 / ZLogger / 런타임 로그 레벨
- **Phase 10 후속(게이트 조건 아님)** — 서킷 브레이커·Bulkhead / 전역·IP별 속도 제한
  (System.Threading.RateLimiting) / 정식 24h soak 를 CI 에 스케줄

## 다음 (우선순위 순)

1. **Phase 11 — HTTP 헬스 노출 어댑터** — `/healthz`·`/readyz`(HttpListener, 별도 admin 포트).
   k8s 프로브가 직접 쓰게. HttpListener vs Kestrel ADR 필요
2. **Phase 11 — 프레임당 바이트·풀 카운터** — 남은 메트릭. 읽기 루프·풀 계측
3. **Phase 10 후속** — IP별 속도 제한(System.Threading.RateLimiting, 새 라이브러리 ADR) / 서킷 브레이커
4. **Part II 잔여** — Phase 7(누락 핸들러 검출) / Phase 8(코어 제한 재측정)

## 블로커 / 열린 결정

- ~~레거시 하드코딩 자격증명~~ — 판정 완료: `ServerGlobals.cs:103` MongoDB 계정·암호는
  커밋 시점 유출 간주. **로컬 개발 외 재사용됐다면 폐기·교체할 것**(사용자 확인 필요)
- MemoryPack `VersionTolerant` 주의 계약의 제너레이터 진단 승격 여부 — ADR-0013 부정 항목
- LoadRunner 램프업 무한 루프(죽은 서버 대상) — Phase 12 항목 추가됨

## 이번에 배운 것 (같은 실수 반복 방지)

- **이름이 같아도 성숙도는 다르다** — "백프레셔"에 방출만 없는 신호(파티션 메트릭)·자연
  생산자 없는 상태(RejectedByBackpressure)·경로 없는 메트릭(BackpressureDuration)이 섞여
  있었다. 억지로 다 배선하지 않고 정직하게 갈랐다. 자연 백프레셔(자기 조절) 경로엔 거부가 없다
- **없는 생산자를 가짜로 만들지 않는다** — `RejectedByBackpressure` 는 주 경로가 자연
  백프레셔라 생산자가 없다. 억지 생산자는 ADR-0008 과 충돌. 문서로 "예약" 을 명확히 하는 게
  가짜 배선보다 오래 옳다
- **추상화는 두 번째 구현이 있을 때만** — 추적은 구독자(ActivityListener)가 교체 지점이라
  방출자 추상화(ITraceSink)는 두 번째 구현이 없다. 메트릭과 대칭이 "일관성"처럼 보여도,
  대칭이 비용(프레임당 span 핸들 할당)만 만들면 대칭을 버린다(ADR-0022)
- **켜짐/꺼짐 신호가 있으면 데코레이터 async 비용을 없앨 수 있다** — 메트릭 미들웨어는 Null
  싱크에서도 43ns(async 래퍼)였는데, 추적은 `HasListeners()` 로 리스너 없을 때 `next` 를
  그대로 반환해 8ns/0B. 데코레이터가 "꺼졌는지" 값싸게 알 수 있으면 상태 머신을 통째로 건너뛴다
- **Core 무의존 ≠ BCL 무사용** — `ActivitySource`·`Meter` 는 공유 프레임워크라 패키지 참조
  없이 Hosting 이 직접 쓸 수 있다. "무의존" 은 서드파티 패키지·벤더 타입 유입 금지이지 BCL 금지가 아니다
- **크로스 스레드 컨텍스트는 값으로 나른다** — `Activity.Current`(AsyncLocal)는 채널·파티션
  스레드 경계를 못 넘는다. 파티션 실행 모델에서 부모-자식 span 을 얻으려면 컨텍스트를
  커넥션 기능에 실어야 한다("수립 시 Set·그 뒤 읽기만" 규약이 락 없이 가능케 함). 전역 상태
  (`ActivityListener`)를 다루는 테스트는 `[Collection]` 병렬 비활성으로 순차화해야 오염이 없다
- **어댑터가 옵트인 인터페이스로 축에 기여한다** — 실행 모델 liveness 는 `PartitionedExecutionModel`
  이 `IHealthCheck` 를 구현하고 호스팅이 `is IHealthCheck` 로 자동 등록한다. Core 축 계약
  (`IExecutionModel`)에 진단 멤버를 얹지 않고, 호스팅이 Concurrency 를 참조하지 않고도 배선된다.
  이미 있는 상태(생명주기 Draining)를 헬스가 공유하지 두 번째 진실을 만들지 않는 것도 같은 규율

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율. 설계 결정 지점은 선택지로 물어본다
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 커밋: 코드와 문서 분리, 스코프는 어셈블리 축 단위. ADR·벤치는 기능 커밋에 동봉,
  스탠드업 문서는 `/standup wrap` 에서 `chore(standup)`
- CI 확인은 `gh` CLI (2.97.0 설치·인증 완료)

## 다른 환경에서 시작하기

```
git clone https://github.com/demian-moon/ChServerM.git
cd ChServerM
dotnet restore ChServerM.slnx
powershell -File eng/build.ps1 -Configuration Release -WarnAsError
```

- SDK 는 `global.json` 이 **10.0.1xx** 로 고정. 밴드가 없으면 dotnet-install 로
  사용자 로컬(`~\.dotnet`) 설치가 가장 싸다 (이 머신은 `~\.dotnet` 에 10.0.110 —
  비대화형 셸에서는 PATH 앞에 수동으로 얹어야 한다)
- **FlatSharp.Compiler 는 `DOTNET_ROLL_FORWARD=LatestMajor` 필요** — 빌드 스크립트가 설정.
  벤치를 직접 `dotnet run` 할 때도 이 환경변수를 손수 얹어야 한다
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
- 메트릭 관찰: `UseMetrics(new MeterMetricsSink())` 조립 후 `dotnet-counters monitor --name <프로세스> ChServerM`
- 추적 관찰: `UseTracing()` 조립 후 `dotnet-trace collect --name <프로세스> --providers ChServerM`
- 24h soak 정식 판: `CHSM_SOAK_SECONDS=86400` 환경변수 + `dotnet test --filter SoakTests`
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0001·0002·0004~0023 채택 / 0003 폐기 — 미결 ADR 없음)
- 위협 모델: `docs/THREAT-MODEL.md` (T-04·05·06·08~22 대부분 ✅)
- 진단 규칙: `docs/DIAGNOSTICS.md` / 메트릭 이름: `DiagnosticNames`/`MetricNames`/`TagNames`
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (최근: `2026-08-07.md`)
- 레거시 분석: `docs/legacy/00-overview.md`
