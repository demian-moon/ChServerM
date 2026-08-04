# ChServerM — 현재 상태

**최종 갱신**: 2026-08-04 (3차)
**현재 단계**: Phase 0 ✅ · Phase 5 게이트 ✅ · **H4 해소(ADR-0010)** — Part I~II 병행 진행 중
**진행률**: 59/221 항목 (Phase 0 `13/17` · 1 `12/21` · 2 `7/11` · 4 `9/10` · 5 `10/12` · 8 `8/16`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Core 추상화** — 무의존 2중 가드. **ADR-0010 으로 와이어 포맷 지식이 Core 에서 제거됨**
  (`MessageEnvelope` 논리 계약 / `FrameHeader` 는 Framing 어댑터 소유)
- **프레이밍 축 — 두 구현으로 증명 완료** — 고정 16B 헤더 + varint(가변 2~8B, LEB128
  정규형, 헤더 오버헤드 1/8). 같은 에코 핸들러가 고정/varint × 인메모리/TCP 4조합에서
  동작(교체 테스트). 퍼징 통과, 프레임당 할당 0
- **전송 2종** — 인메모리 + raw TCP. 감사 결함 전부 수정, idle timeout·소켓 옵션·
  거부 통지(40004)·종료 상한까지 완결. **크로스 플랫폼 CI 통과**(ubuntu·windows)
- **실행 모델 — ADR-0008 액터형** — 배타성을 완료 대기로 보장. 프레임당 할당 ~0B
- **Phase 5 게이트** — 1만 동시 접속(실패 0, 커넥션당 ~8KB), 에코 146-169k RPS,
  지연 바닥 p50 104µs / p99 162µs (ENV-B 루프백, ADR-0009)
- 테스트 **337개** 통과, 전체 게이트(-WarnAsError·audit·AOT) 통과, 원격 CI 녹색

## 진행 중

- **Phase 4 잔여 1건** — 조각 재조립(`Fragmented`/`EndOfMessage`, 재조립 버퍼 상한 필수)
- **Phase 5 잔여 2건(게이트 조건 아님)** — ⚠ ADR-0001(Kestrel 벤치 대결),
  송신 배칭 벡터드 send(실측 동반)
- **Phase 1 잔여** — `ISessionStore`, `IMetricsSink`, `IPayloadCodec`, `ITransportSecurity` 등
- **감사 보류분** — `MessageContext` 내부 메서드 public(ADR 후보), ConnectionId 세대 활용
  (세션 계층), Phase 13~22 게이트 정의

## 다음 (우선순위 순)

1. **Phase 6 직렬화 어댑터** — MemoryPack 첫 구현 + 두 번째 구현으로 직렬화 축 증명,
   ADR-0002 잔여(페이로드 기본값, 4자 벤치마크)
2. **varint 벤치마크 수치 기록** — `VarintCodecBenchmarks` 는 있고 실행만 남았다.
   Phase 6 벤치와 묶어서
3. **ADR-0001 Kestrel 벤치 대결** — Phase 5 잔여 중 가장 무거운 것

## 블로커 / 열린 결정

- **ADR-0001 미결** — Kestrel Socket Transport 벤치마크 대결
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값(Phase 6)
- **레거시 하드코딩 자격증명** — `ServerGlobals.cs:103` (기존 항목 유지)

## 이번에 배운 것 (같은 실수 반복 방지)

- **Unshipped 단계가 계약 수술의 마지막 싼 시점이다** — H4(4개 결박 지점, 37파일 여파)를
  파괴적 변경 0으로 해소했다. Shipped 승격 뒤였다면 전부 파괴적 변경이었다
- **"없는 필드"의 두 방향은 대칭이 아니다** — 디코더가 기본값을 채우는 것은 사실의 표현,
  인코더가 기본값 아닌 값을 조용히 버리는 것은 조용한 실패. 후자는 예외로 막는다(ADR-0010)
- **가변 표현은 정규형을 강제한다** — 같은 값의 varint 표현이 여럿이면 바이트 단위
  검증(AEAD·리플레이)이 흔들린다. 디코딩 시점에 비정규를 거부하는 것이 가장 싸다
- **분리 커밋은 중간 상태를 단독 검증한다** — varint 를 빼고 1번 커밋을 빌드+테스트한 뒤
  커밋했다. bisect 가 깨진 중간 커밋을 만나지 않는다
- 인코딩 손상은 전수 스캔으로 범위를 확정한다 — 모지바케 특수 문자 grep 으로
  손상 파일이 1개뿐임을 확인하고 복원했다

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 커밋: 코드와 문서 분리. 문서는 `/standup wrap` 에서 `chore(standup)`
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
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0002·0004·0005·0006·0007·0008·0009·**0010** 채택 / 0001 미결 / 0003 폐기)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (오늘: `2026-08-04.md` 1·2·3차)
- 레거시 분석: `docs/legacy/00-overview.md`
