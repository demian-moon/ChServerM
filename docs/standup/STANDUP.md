# ChServerM — 현재 상태

**최종 갱신**: 2026-08-06 (10차)
**현재 단계**: **Part III — Phase 10 복원력 진행 중** (수용 제어 + 속도 제한 축 ✅). Phase 9 보안 ✅ / Phase 11 관측 메트릭 ✅
**진행률**: 100/222 항목 (Phase 0 `13/17` · 1 `15/21` · 2 `7/11` · 3 `4/6` · 4 `10/10` · 5 `12/12` · 6 `7/7` · 7 `5/7` · 8 `8/16` · 9 `13/13` · 10 `2/10` · 11 `4/9`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Part II 데이터 경로 게이트 전부 ✅** — 프레이밍·버퍼(50ns/0B)·TCP(1만 접속·169k RPS)·
  직렬화 3종·디스패치 소스 제너레이터(AOT)·실행 모델(코어 효율 95%)
- **Phase 9 보안 ✅ 완료(13/13)** — 위협 모델 / TLS 1.3 + 인증서 회전 / 버전 협상 /
  상태 화이트리스트 / 인증(+리플레이 가드·해셔) / 인가 2단 / 압축(LZ4) / 시크릿 관리 /
  입력 검증 / `/security-review`(신규 취약점 0건)
- **Phase 11 관측 — 메트릭 축 첫 증분 ✅ (2026-08-06 8차, ADR-0020)** — `IMetricsSink`(Core)
  + `MeterMetricsSink`(BCL `System.Diagnostics.Metrics` — dotnet-counters 즉시, OTel 은 Meter
  구독으로). `UseMetrics()` 한 줄이 커넥션 생명주기·디스패치 지연·처리량·실패를 데코레이터로
  배선. **오버헤드 게이트 충족**: 켠 ~72ns/프레임·**할당 0**, 끈(NullMetricsSink) 6ns
- **Phase 10 복원력 — 수용 제어(연결) + 속도 제한(메시지) ✅ (2026-08-06 9~10차)** —
  수용 제어 `IAdmissionControl` + `ConnectionRateAdmissionControl`(신규 연결 토큰 버킷,
  전송 주입, 거부 = `ConnectionsRejected` 메트릭, ADR-0021, T-14·16). 속도 제한
  `IRateLimiter` + `PerConnectionRateLimiter`(커넥션별 토큰 버킷 — 상태를 `Connection.Features`
  에 둬 순차 디스패치로 락-프리·축출 불필요) + `RateLimitMiddleware`. 거부 =
  `RejectedByRateLimit`(9)→6003 무-종료, 관측은 `DispatchFailures` 자동
- 테스트 **643개** 통과, 전체 게이트(-WarnAsError 클린 빌드·audit·AOT publish+실행) 통과

## 진행 중

- **Phase 10 후속** — **24h soak 하네스**(게이트 후반부 "메모리 평탄" — 게이트 완성) /
  백프레셔 생산 배선(`RejectedByBackpressure`) / 서킷 브레이커·Bulkhead /
  전역·IP별 속도 제한·워터마크 수용 제어(System.Threading.RateLimiting)
- **Phase 11 후속** — 분산 트레이싱(`ActivitySource`) / 프레임당 바이트·큐 깊이·풀 카운터 /
  ZLogger / 헬스체크 엔드포인트 / 런타임 로그 레벨
- **후속 최적화 후보** — 디스패치 데코레이터 async 래퍼가 계측보다 비싸다(Null 싱크
  6→43ns). `next` 동기 완료 시 async 회피 fast-path — 트레이싱 증분에서 함께 판단

## 다음 (우선순위 순)

1. **Phase 10 게이트 완성 — 24h soak 하네스** (추천) — 게이트 후반부("메모리 평탄").
   수용 제어·속도 제한(전반부 "거부하며 생존")이 됐으니 이제 24h 메모리 곡선만 남았다.
   Phase 11 메트릭 활용. 단 장시간 실행이라 별도 도구·CI 설계 필요
2. (대안) Phase 10 후속 — 백프레셔 생산 배선 / 서킷 브레이커·Bulkhead / 전역·IP별 속도 제한
3. (대안) Phase 11 후속(트레이싱·헬스체크) 또는 Part II 잔여(Phase 7·8)

## 블로커 / 열린 결정

- ~~레거시 하드코딩 자격증명~~ — 판정 완료: `ServerGlobals.cs:103` MongoDB 계정·암호는
  커밋 시점 유출 간주. **로컬 개발 외 재사용됐다면 폐기·교체할 것**(사용자 확인 필요).
  새 코드는 `ISecretSource` 가 참조 패턴
- MemoryPack `VersionTolerant` 주의 계약의 제너레이터 진단 승격 여부 — ADR-0013 부정 항목
- LoadRunner 램프업 무한 루프(죽은 서버 대상) — Phase 12 항목 추가됨

## 이번에 배운 것 (같은 실수 반복 방지)

- **게이트가 관측을 전제하면 관측을 먼저 깐다** — Phase 10 을 먼저 했으면 측정 못 하는
  복원력을 만들 뻔했다. 게이트 조건을 읽고 로드맵 의존 순서를 뒤집는 판단
- **데코레이터 async 래퍼가 계측보다 비싸다** — Null 싱크에서도 6→43ns. 인터페이스 가상
  호출 + async 상태 머신. 트레이싱 증분에서도 재등장할 비용
- **이름 계약을 미리 깔아둔 값** — 2026-08-04 메트릭 이름 정비 덕에 관측·수용 제어 증분이
  축 계약과 배선만으로 끝났다. 오타 하나가 대시보드 계약을 깨는 것을 막음
- **저빈도 경로는 락이 옳다** — 9.1 락 금지는 핫패스(프레임당) 대상. 커넥션당 1회 토큰
  버킷(수용 제어)은 락이 CAS 재시도보다 명백히 정확·가독. 규칙을 맥락 없이 적용하지 않는다
- **상태를 파티션 소유자에 두면 락도 축출도 사라진다** — per-connection 속도 제한 버킷을
  Connection.Features 에 두니 순차 디스패치가 동기화를, 커넥션 GC 가 축출을 공짜로 해결.
  전역 맵 만들기 전에 "이 상태의 자연스러운 소유자가 누구인가"를 먼저 본다(9.1)
- **응답 없는 거부는 메트릭이 배리어다** — 드롭 프레임은 클라이언트가 못 봐서 테스트가
  시계 진행과 경합. Phase 11 메트릭을 배리어로 써 "처리·거부됨"을 확정 — 관측이 테스트
  결정성에도 값을 했다
- **raw `dotnet build -warnaserror` 는 게이트보다 엄격** — CA5398(Tls13 하드코드)이 raw
  에선 에러, `eng/build.ps1`·plain build 에선 editorconfig 로 억제됨. 게이트가 authority
- **PowerShell 5.1 은 heredoc(`<<`)이 없다** — 여러 줄 커밋은 `git commit -F <파일>`.
  UTF-8 파일 내용 수정은 Edit/Write 로만

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율. 설계 결정 지점은 선택지로 물어본다
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 커밋: 코드와 문서 분리, 스코프는 어셈블리 축 단위로 쪼갠다. 문서는 `/standup wrap` 에서 `chore(standup)`
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
- **FlatSharp.Compiler 는 `DOTNET_ROLL_FORWARD=LatestMajor` 필요** — 빌드 스크립트가 설정
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
- 메트릭 관찰: `UseMetrics(new MeterMetricsSink())` 조립 후 `dotnet-counters monitor --name <프로세스> ChServerM`
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0001·0002·0004~0021 채택 / 0003 폐기 — 미결 ADR 없음)
- 위협 모델: `docs/THREAT-MODEL.md` (T-04·05·06·08~22 대부분 ✅ — T-14·16 도 2026-08-06)
- 진단 규칙: `docs/DIAGNOSTICS.md` / 메트릭 이름: `DiagnosticNames`/`MetricNames`/`TagNames`
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (최근: `2026-08-06.md` 1~10차)
- 레거시 분석: `docs/legacy/00-overview.md`
