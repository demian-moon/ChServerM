# ChServerM — 현재 상태

**최종 갱신**: 2026-08-07
**현재 단계**: **Part III** — Phase 10 복원력(6/10, 게이트 실질 충족·**복합 방어 증거 확보**) / Phase 11 관측(7/9, 트레이싱·헬스·바이트/풀·로깅 어댑터 ✅ — 남은 것은 런타임 로그 레벨·런타임 진단). Phase 9 보안 ✅
**진행률**: 107/222 항목 (Phase 0 `13/17` · 1 `15/21` · 2 `7/11` · 3 `4/6` · 4 `10/10` · 5 `12/12` · 6 `7/7` · 7 `5/7` · 8 `8/16` · 9 `13/13` · 10 `6/10` · 11 `7/9`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Part II 데이터 경로 게이트 전부 ✅** — 프레이밍·버퍼(50ns/0B)·TCP(1만 접속·169k RPS)·
  직렬화 3종·디스패치 소스 제너레이터(AOT)·실행 모델(코어 효율 95%)
- **Phase 9 보안 ✅ 완료(13/13)** — 위협 모델 / TLS 1.3 + 인증서 회전 / 버전 협상 /
  상태 화이트리스트 / 인증(+리플레이 가드·해셔) / 인가 2단 / 압축(LZ4) / 시크릿 관리 /
  입력 검증 / `/security-review`(신규 취약점 0건)
- **Phase 10 복원력 — 게이트 실질 충족 ✅** — 수용 제어 `IAdmissionControl`(ADR-0021) +
  속도 제한 `IRateLimiter` + soak 하네스(짧은 판 평탄, 정식 24h 만 수동/CI). **2026-08-07 후속 3건**:
  ① 파티션 백프레셔 관측(`5c9059b`) — `RejectedByBackpressure` 는 자연 생산자 없음을 문서로 확정.
  ② **주소별 연결 속도 제한**(`4cb9373`, ADR-0026) — 고정 슬롯 배열이라 **축출 없이 구조적 유계**
  (Dictionary 였다면 소스 주소를 바꾸는 것만으로 OOM 유발 가능), IPv6 는 **/64 프리픽스 집계**,
  표적 충돌은 `HashCode` 프로세스별 랜덤 시드가 차단. ③ **파티션 정지 감지**(`576f393`, ADR-0027) —
  완료 안 하는 핸들러가 파티션을 붙들면 모든 커넥션이 함께 멈추는데 **스레드는 살아 있어 생존
  신호로 안 잡히는** 사각지대를 메움(프레임당 할당 0, 헬스 **Degraded**).
  ④ **크래시 처리**(`79e640f`, ADR-0028) — 수락 루프가 사용자 공급 축(`IAdmissionControl`)의
  예외로 **조용히 죽던 결함** 차단(고장을 상태로 → 전송이 `IHealthCheck` 로 readiness 노출).
  전역 훅은 opt-in 헬퍼, 덤프·재시작은 운영 설정임을 문서화.
  ⑤ **우아한 열화**(`b88267b`, ADR-0029) — `ILoadLevelSource` 축 + `LoadSheddingMiddleware`
  (**차단 순서는 앱이 선언, 미등록=필수**) + `RejectedByLoadShedding` **무-종료** +
  `MemoryLoadLevelSource`(컨테이너 메모리 제한 자동 추종).
  ⑥ **장애 주입**(`56c16d3`) — 생산 코드 0, 결정적 적대 시나리오. **복합 방어 동시 검증**으로
  게이트 주장("거부하며 살아남는다")의 실제 증거 확보
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
- **Phase 11 헬스 체크 ✅ 완료 (2026-08-07, ADR-0023·0024)** — Core `IHealthCheck`/`HealthStatus`/
  `HealthReport`/`HealthProbe`(무의존) + `HealthCheckService`(최악 우선 집계·항목별 try/catch).
  내장 **readiness**=생명주기(`ServerLifecycleState`, `UnbindAsync`→Draining=not-ready) ·
  **liveness**=`PartitionedExecutionModel` 이 `IHealthCheck` 구현(파티션 스레드 생존, 옵트인
  자동 등록). **HTTP 엔드포인트**(`3880020`): `ChServerM.Diagnostics.Http.HealthHttpEndpoint`
  (HttpListener, `/healthz`·`/readyz`, 200/503, Core 만 참조·프로브 델리게이트)로 k8s 프로브 직접 사용
- **Phase 11 바이트·풀 카운터 ✅ (2026-08-07 `5a89ed6`, ADR-0025)** — **바이트**는 전송이 소켓
  경계에서 push(`BytesReceived`/`BytesSent`, 회선 기준·소켓 연산당 1회. 회선 없는 인메모리는
  내지 않음 — 계약 명시). **풀**은 `IMetricsSink.ObserveCounter`(기본 무동작 default 메서드)로
  **pull** — 이미 세어지는 값이라 핫패스 비용 0. `Observability → Buffers` +
  `BufferPoolMetrics.Register(sink)` 로 배선(Buffers 무의존 결정 유지).
  `pool.buffers.leaked != 0` 이면 버그
- **Phase 11 로깅 어댑터 ✅ (2026-08-07 `9ad167b`, ADR-0030)** — **ZLogger 가 아니라 MEL 을
  대상으로**(대상 변경, 근거는 ADR). `IServerLogger.Log<TState>` 가 `ILogger.Log<TState>` 와
  시그니처가 같아 **~30줄 패스스루**로 ZLogger·Serilog·콘솔·Seq 생태계 전체가 열린다.
  **무할당은 이미 충족**돼 있었다 — 로그 지점 29곳 전수 확인 결과 전부 오류·희소 경로이고
  정상 프레임 경로엔 로그가 없다. 상태 무박싱을 테스트로 고정, 범주는 `ChServerM` 통일
- 테스트 **721개** 통과, 전체 게이트(-WarnAsError 클린 빌드·audit·AOT publish+실행) 통과

## 진행 중

- **Phase 11 잔여** — 런타임 로그 레벨(재시작 없이 디버그 활성화) / 런타임 진단
  (커넥션 덤프·스레드·풀 상태 조회)
- **Phase 10 잔여(작은 것)** — 메모리 워터마크(`MemoryLoadLevelSource`)를 `IAdmissionControl` 에
  잇는 배선 / 정식 24h soak 를 CI 에 스케줄(하네스 완비, 실행만 남음)
- **보류 — 대상이 생길 때 만든다**: **서킷 브레이커·재시도**(아웃바운드 호출 지점이 0 — Phase 13
  세션 저장소 / Phase 15 클러스터에서 실물과 함께, ADR-0027)
- **별도 설계 판단 대기**: `FramesSent`(`FrameWriter` 가 static 확장이라 싱크 주입 지점 없음) /
  Bulkhead 강제(핸들러 타임아웃 — 협조적 취소 한계·프레임당 타이머 비용, 감지는 완료)

## 다음 (우선순위 순)

1. **Part II 잔여** — Phase 7(누락 핸들러 검출·리플렉션 폴백) / Phase 8(코어 제한 재측정).
   Part III 를 오래 팠으니 데이터 경로의 미완을 정리할 시점
2. **Phase 11 마무리** — 런타임 로그 레벨 / 런타임 진단(커넥션 덤프·스레드·풀 상태)
3. **Phase 10 마무리** — 워터마크→수용 제어 배선(작음) / 정식 24h soak CI 스케줄
4. **Phase 12** — 성능 검증·회귀 방어(부하 측정 자산은 있음)

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
- **⚠ 게이트가 증분 캐시 위에서 통과하면 잠복 위반을 놓친다** — CA5398(Tls13 하드코딩)이 그동안
  "게이트 authority·editorconfig 억제" 로 알려졌으나 **착오**였다. Tls 프로젝트가 여러 커밋 동안
  안 바뀌어 <b>재분석되지 않았을 뿐</b>, 실제 억제는 없었다. 새 프로젝트 추가가 전체 재분석을
  유발하자 CA5398·CA1515 가 드러났다(클린 CI 라면 실패). **게이트 통과 ≠ 위반 없음** — 잠복이
  의심되면 대상 프로젝트를 클린 빌드(`obj/bin` 삭제 후 `-p:TreatWarningsAsErrors=true`)해 확인한다
- **어댑터는 델리게이트로 소스를 받아 계층을 지킨다** — HTTP 헬스 어댑터가 HealthCheckService
  (Hosting) 대신 프로브 델리게이트(Core 타입)를 받으니 "Server 어셈블리 → Hosting" 첫 사례를
  만들지 않고 일방 의존을 유지. 값 타입(HealthProbe)을 Core 로 올리는 작은 이동이 그것을 가능케 함
- **"이미 세어지고 있는 값"은 push 가 아니라 pull 이다** — 풀 카운터를 push 하면 가장 뜨거운
  경로(대여·반납)에 메트릭 호출이 붙는다. 값이 이미 있으면 수집 주기에 읽어가는 것이 옳고
  (`ObserveCounter`), BCL ObservableCounter 가 정확히 그 계약이다. 세션 수·캐시 항목도 같은 길
- **없는 개념을 억지로 만들지 않는다** — 인메모리 전송에는 "회선을 건넌 바이트"가 없다. 전송 간
  대칭을 위해 PipeReader/Writer 를 래핑하면 핫패스 비용만 는다. 0이 정상임을 계약에 적는 것이
  더 정직하다(`RejectedByBackpressure` 를 예약으로 남긴 것과 같은 규율)
- **의존 방향이 배선의 위치를 정한다** — Buffers 가 Core 를 안 보므로 풀은 자기 카운터를 메트릭
  으로 못 낸다. 그 결정을 깨는 대신 **관측 배선을 관측 어셈블리가 가져가면** 기존 결정을 지키면서
  자동 배선을 얻는다
- **방어 장치가 스스로 공격 표면이 되는지 먼저 본다** — IP별 제한의 상태 맵은 공격자가 소스 주소만
  바꿔도 무한히 자란다. "무제한 큐 금지"(9.6)는 큐만의 규칙이 아니라 **공격자가 성장을 유도할 수
  있는 모든 자료구조**의 규칙이다. 고정 크기로 만들면 축출·스윕·타이머가 통째로 사라진다
- **패턴을 적용하기 전에 대상이 있는지 확인한다** — 서킷 브레이커는 교과서적으로 옳지만 이
  프레임워크엔 아직 외부 호출이 없다. ROADMAP 에 있다고 지금 만들어야 하는 것은 아니다 —
  **보류 근거를 남기는 것이 대상 없는 계약을 만드는 것보다 낫다**(ADR-0027)
- **보장에는 대가가 따르고, 그 대가가 곧 사각지대다** — 배타성 보장(무기한 완료 대기)의 대가가
  "한 핸들러가 파티션을 영구 정지" 다. 보장을 설계할 때 그 대가가 만드는 장애 모드와, 그것이
  기존 신호로 관측되는지(스레드 생존으로는 안 잡힘)를 함께 봐야 한다
- **심각도를 잘못 매기면 관측이 장애를 만든다** — 정지를 liveness 실패로 냈다면 일시적 지연에도
  재시작 루프가 돈다. Degraded/Unhealthy 는 문서상 분류가 아니라 **운영 동작(재시작 여부)의 선택**이다
- **교체 가능한 축은 "남의 코드가 내 루프 안에서 돈다"는 뜻이다** — 수락 루프가 소켓 예외만 잡는
  사이 사용자 공급 `IAdmissionControl` 이 던져 루프가 조용히 죽었다. **교체 지점마다 "여기서
  던지면 무엇이 죽는가"** 를 물어야 한다. 저장만 해둔 배경 태스크는 관측이 아니다 — 고장을
  **상태로 바꿔야** 제때 보인다
- **실수의 방향을 안전한 쪽으로 설계한다** — 열화에서 미등록 메시지를 기본 차단으로 뒀다면 설정
  누락이 "부하가 높을 때만 기능이 사라지는" 최악의 버그가 됐다. 기본값은 실수했을 때 덜 나쁜 쪽으로
- **계약이 표준과 같은 모양이면 어댑터 하나가 생태계 전체를 연다** — `IServerLogger` 가
  `ILogger` 와 같은 시그니처였던 덕에 벤더별 어댑터 대신 MEL 하나로 끝났다(ADR-0030).
  **축을 설계할 때 표준 계약의 모양을 먼저 본다**
- **목표와 수단을 구분해 읽는다** — ROADMAP 의 "ZLogger 어댑터(무할당 구조적 로깅)" 에서
  괄호가 목표였다. 문구를 문자 그대로 따랐으면 벤더를 Core 에 들였을 것이다. **항목의 의도를
  확인하고, 바꿨으면 그 사실이 diff 에 보이게 적는다**
- **적대 조건은 진짜로 적대적인지 먼저 확인한다** — 헤더(16B)보다 짧은 바이트를 "쓰레기" 로 알고
  짠 테스트가 15초 타임아웃으로 실패했다. 디코더에겐 그것이 정상적인 "더 기다림" 이었다

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
  (풀 카운터는 `BufferPoolMetrics.Register(sink)` 를 프로세스당 **1회** 더 부른다 — 두 번 부르면 중복 보고)
- 추적 관찰: `UseTracing()` 조립 후 `dotnet-trace collect --name <프로세스> --providers ChServerM`
- 헬스 프로브: `new HealthHttpEndpoint(server.Health.CheckHealthAsync).Start()` 후
  `curl -i localhost:8081/healthz`(liveness)·`/readyz`(readiness) — 200/503
- 24h soak 정식 판: `CHSM_SOAK_SECONDS=86400` 환경변수 + `dotnet test --filter SoakTests`
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0001·0002·0004~0030 채택 / 0003 폐기 — 미결 ADR 없음)
- 위협 모델: `docs/THREAT-MODEL.md` (T-04·05·06·08~22 대부분 ✅)
- 진단 규칙: `docs/DIAGNOSTICS.md` / 메트릭 이름: `DiagnosticNames`/`MetricNames`/`TagNames`
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (최근: `2026-08-07.md`)
- 레거시 분석: `docs/legacy/00-overview.md`
