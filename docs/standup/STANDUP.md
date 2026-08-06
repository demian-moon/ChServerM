# ChServerM — 현재 상태

**최종 갱신**: 2026-08-06 (7차)
**현재 단계**: **Part III — Phase 9 보안 ✅ 완료(13/13)**. 다음 Phase 착수 결정 대기
**진행률**: 94/222 항목 (Phase 0 `13/17` · 1 `15/21` · 2 `7/11` · 3 `4/6` · 4 `10/10` · 5 `12/12` · 6 `7/7` · 7 `5/7` · 8 `8/16` · 9 `13/13`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Part II 데이터 경로 게이트 전부 ✅** — 프레이밍(고정16B+varint, 조각 재조립 ADR-0015) /
  버퍼(`PooledBufferWriter` 50ns/0B, ADR-0016) / TCP(순수 Socket 확정 ADR-0001, 1만 접속·169k RPS) /
  직렬화 3종(기본 MemoryPack, ADR-0013) / 디스패치 소스 제너레이터(ADR-0014, AOT 실증) /
  실행 모델(ADR-0008, 물리 코어 효율 95%)
- **Phase 9 보안 ✅ 완료(13/13)** — 위협 모델(경계 5·표면 9·위협 22 전 매핑) / 전송 보안
  TLS 1.3(ADR-0017, 실측 −2.5%) + 인증서 로딩·회전 운영 경로 / 버전 협상(동결 코덱, R-1~5) /
  상태별 화이트리스트(T-19) / 인증(T-20, `AuthM` 승계 ADR-0018) + 리플레이 가드(T-05) /
  인가 2단(T-21) / 압축(ADR-0019, LZ4 — T-11·18) / 시크릿 관리(Env/Directory 원천) /
  입력 검증(T-22, `IMessageValidator`) + 퍼징 확대
- **`/security-review` ✅** — 3단계 리뷰(식별→병렬 필터→confidence 8 미만 제거),
  **신규 취약점 0건**. 검증 경로 10종 건전 확인, 임계치 미만 관찰 2건 위양성 확정
- 테스트 **596개** 통과, 전체 게이트(-WarnAsError 클린 빌드·audit·AOT publish+실행) 통과

## 진행 중

- 없음 (Phase 9 종료, 작업 트리 clean)

## 다음 (우선순위 순)

1. **다음 Phase 착수 결정** — 사용자와 함께 택:
   - **Phase 10 복원력** — `IRateLimiter`·`IAdmissionControl`(과부하 시 신규 연결 거부,
     "거부가 붕괴보다 낫다") · 서킷 브레이커·Bulkhead · 24h soak 테스트. 게이트: 과부하에서
     거부하며 생존 + 24h 메모리 평탄
   - **Phase 11 관측** — `IMetricsSink`(OpenTelemetry+Prometheus). 리플레이·인증 실패·
     버전 분포·큐 깊이 카운터가 이미 로그 이벤트로 있으니 메트릭 파이프라인만 얹으면 된다
   - **Part II 잔여** — Phase 7(누락 핸들러 검출·리플렉션 폴백) / Phase 8(코어 제한 재측정)
2. 착수 전 해당 Phase 의 `docs/legacy/` 문서 선독(승계 대상이면)
3. 설계 선택이 갈리면 대안·트레이드오프 제시 후 승인받고 구현(합의된 작업 방식)

## 블로커 / 열린 결정

- ~~레거시 하드코딩 자격증명~~ — **판정 완료**: `ServerGlobals.cs:103` MongoDB 계정·암호는
  커밋 시점 유출 간주. **로컬 개발 외 재사용됐다면 폐기·교체할 것**(사용자 확인 필요).
  레거시 트리는 참조 전용, 새 코드는 `ISecretSource` 가 참조 패턴
- MemoryPack `VersionTolerant` 주의 계약의 제너레이터 진단 승격 여부 — ADR-0013 부정 항목
- LoadRunner 램프업 무한 루프(죽은 서버 대상) — Phase 12 항목 추가됨

## 이번에 배운 것 (같은 실수 반복 방지)

- **"간헐적" 진단은 대개 관측 조건의 차이다** — 게이트 첫 실행 실패를 "파일 잠금"으로
  두 번 오진했는데, 실은 벤치 커밋의 분석기 위반이 증분 빌드에서 스킵되다 클린 빌드에서만
  터진 결정적 실패였다(`e437901`). 재현 안 되면 "간헐" 분류 전에 증분 vs 클린을 의심
- **벤더 예외는 어댑터가 값으로 변환한다** — `PasswordHasher`·LZ4 디코더 둘 다 악의
  입력에 던진다. "던지지 않는다"는 벤더 계약이 아니므로 어댑터가 값으로 바꾼다(T-16)
- **동결 계약의 의도적 중복은 교차 검증 테스트와 쌍으로** — Core/Framing 헤더 일치를 테스트가 지킨다
- **로드를 거친 인증서의 개인키는 재수출 불가일 수 있다**(Windows) — 테스트 소재는 원본 키에서 직접
- **PowerShell 5.1 + 여러 줄 커밋 메시지 = `git commit -F <파일>`** / **UTF-8 파일은 Edit/Write 로만**

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율. 설계 결정 지점은 선택지로 물어본다
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 커밋: 코드와 문서 분리, 스코프는 어셈블리 축 단위로 쪼갠다. 문서는 `/standup wrap` 에서 `chore(standup)`
- CI 확인은 `gh` CLI (2.97.0 설치·인증 완료, `C:\Program Files\GitHub CLI\gh.exe`)

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
- **FlatSharp.Compiler 는 `DOTNET_ROLL_FORWARD=LatestMajor` 필요** — 빌드 스크립트가
  설정하므로 IDE·단독 `dotnet test` 에서만 수동 설정
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
  (`--transport socket|kestrel` ADR-0001 재현 / `--tls true|false` ADR-0017 A/B)
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0001·0002·0004~0019 채택 / 0003 폐기 — 미결 ADR 없음)
- 위협 모델: `docs/THREAT-MODEL.md` (T-04·05·06·08~22 대부분 ✅ / 새 축·표면 추가 시 갱신)
- 진단 규칙: `docs/DIAGNOSTICS.md` (CHSM0xxx 가드 · CHSM1xxx 제너레이터)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (최근: `2026-08-06.md` 1~7차)
- 레거시 분석: `docs/legacy/00-overview.md`
