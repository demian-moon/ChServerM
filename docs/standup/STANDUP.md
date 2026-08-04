# ChServerM — 현재 상태

**최종 갱신**: 2026-08-04 (2차)
**현재 단계**: Phase 0 ✅ · **Phase 5 게이트 ✅** — Part I~II 병행 진행 중
**진행률**: 56/221 항목 (Phase 0 `13/17` · 1 `12/21` · 2 `7/11` · 4 `7/10` · 5 `9/12` · 8 `8/16`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Core 추상화** — 무의존 2중 가드(런타임 가드는 닫힌 허용 목록으로 강화)
- **프레이밍** — 16B 고정 헤더, 무상태 디코더, 퍼징(위치·내용 불변식 포함) 통과, 할당 0
- **전송 2종** — 인메모리 + raw TCP. 감사 결함(H1~H3 등) 전부 수정, idle timeout·
  소켓 옵션·거부 통지(`ConnectionRejected` 40004)·종료 상한까지 완결
- **실행 모델 — ADR-0008 액터형** — 배타성을 완료 대기로 보장. 계약 테스트 3종.
  **프레임당 할당 ~0B**(184B 에서 제거), 실부하 근사 오버헤드 +0.26µs/프레임
- **정밀 감사(2026-08-04)** — 5축 병렬. 치명 1건(파티션 고정 반증→ADR-0008) 포함
  동작 결함 전부 수정, 검증 장치·문서·계획 보강 완료
- ✅ **Phase 5 게이트** — **1만 동시 접속**(10,000/10,000, 실패 0, 잔존 0, 커넥션당 ~8KB),
  에코 146-169k RPS, **지연 바닥 p50 104µs / p99 162µs** (ENV-B 루프백, 자체 부하 러너 ADR-0009)
- 테스트 **300개** 통과, 전체 게이트(-WarnAsError·AOT) 통과

## 진행 중

- **Phase 5 잔여 3건(게이트 조건 아님)** — ⚠ ADR-0001(Kestrel 벤치 대결),
  크로스 플랫폼 CI(**오늘 커밋 12개 미푸시** — 푸시가 선행), 송신 배칭 벡터드 send(실측 동반)
- **Phase 1 잔여** — `ISessionStore`, `IMetricsSink`, `IPayloadCodec`, `ITransportSecurity` 등
- **감사 보류분(설계 결정 대기)** — H4 프레이밍 계약 결박(Phase 4 ⚠),
  `MessageContext` 내부 메서드 public(ADR 후보), Phase 13~22 게이트 정의

## 다음 (우선순위 순)

1. **푸시 + 원격 CI** — 액터 전환 + Phase 5 분량의 Linux 검증. 가장 저렴한 위험 제거
2. **Phase 6 직렬화 어댑터** — 두 번째 구현으로 직렬화 축 증명 + ADR-0002 잔여
   (페이로드 기본값, 4자 벤치마크)
3. **H4 프레이밍 계약 방향 결정** — varint 디코더 착수와 함께. Shipped 승격 전이 마지막 싼 시점

## 블로커 / 열린 결정

- **ADR-0001 미결** — Kestrel Socket Transport 벤치마크 대결
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값(Phase 6)
- **H4 방향** — 헤더를 어댑터 소유로 내리나, 디스패치 최소 계약만 Core 에 남기나
- **레거시 하드코딩 자격증명** — `ServerGlobals.cs:103` (기존 항목 유지)

## 이번에 배운 것 (같은 실수 반복 방지)

- **검증 장치는 실전 경로를 검증해야 한다** — 파티션 고정은 테스트·벤치마크·통합이 전부
  녹색인 채로 깨져 있었다. "무엇을 검증하는가"가 "통과하는가"보다 먼저다
- **측정이 나쁘게 나오면 그대로 기록한다** — 역확장·할당 실측을 남겼고, 그 기록이
  다음 교정(할당 제거·바쁜 소비자 측정)의 로드맵이 됐다
- **취소 가능 토큰은 공짜가 아니다** — `WaitToReadAsync(token)` 하나가 채널 싱글턴
  waiter 재사용을 막아 프레임당 ~90B 를 만들고 있었다. 종료 신호가 다른 경로로
  보장되면 토큰을 빼는 것이 맞다
- **PS5.1 로 UTF-8(BOM 없음) 파일을 만지지 않는다** — `Get-Content` 가 ANSI 로 읽어
  파일을 통째로 손상시킨다. 파일 편집은 전용 도구로
- **게이트는 커밋마다** — 경고 2건이 `dotnet test`만 돌린 커밋에 섞여 들어가
  다음 게이트에서 터졌다

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 분석기 정책 완화 시 근거 기록. CI 확인은 `gh` CLI
- 커밋: 코드와 문서 분리. 문서는 `/standup wrap` 에서 `chore(standup)`

## 다른 환경에서 시작하기

```
git clone https://github.com/demian-moon/ChServerM.git
cd ChServerM
dotnet restore ChServerM.slnx
powershell -File eng/build.ps1 -Configuration Release -WarnAsError
```

- SDK 는 `global.json` 이 **10.0.1xx** 로 고정. 밴드가 없으면 dotnet-install 로
  사용자 로컬(`~\.dotnet`) 설치가 가장 싸다
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0002·0004·0005·0006·0007·0008·**0009** 채택 / 0001 미결 / 0003 폐기)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (오늘: `2026-08-04.md` 1·2차)
- 레거시 분석: `docs/legacy/00-overview.md`
