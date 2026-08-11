# ChServerM — 현재 상태

**최종 갱신**: 2026-08-11 (8차)
**현재 단계**: Phase 19 완료 · Phase 20 사실상 완료(9/10) · 전체 191/239 · **CI 안정화 후 Phase 21 진입**

## 완료된 것

- **Part I~III 완결** — 기준선 169k RPS · 1만 접속 · 코어 확장 14.67×/16코어(효율 91.7%)
- **Phase 13~18** — 세션 · 데이터 테이블 · 클러스터 · 전송 5종 · 실시간 프리미티브 · 룸/존 & AOI
- **Phase 20 (9/10)** — 샘플 4종 · 시작 가이드 · ⚠ 진단 분석기(CHSM3xxx, ADR-0066) ·
  템플릿 2종 · 가이드 3종 · DocFX 327페이지(ADR-0067) · 예외 메시지 감사 완결
- **Phase 19 완료** — `ChServerM.Matchmaking` 확장 창 대기열 + 파티 FFD(ADR-0068), Elo 는 샘플
- ⭐ **CI 안정화 (8차)** — 8/4 이후 첫 초록화 작업. **버전 협상 거부의 2겹 경합 수정**
  (서버 Abort 의 송신 데이터 파괴 + 클라이언트 읽기의 ConnectionClosed 취소 경합 —
  지배적 원인은 후자), build.ps1 리눅스 AOT 탐색 잠복 결함, 테스트컨테이너 타임아웃,
  타이밍 취약 단언 2건. 상세: history (이어서 7)
- 전 스위트 **1,384개** 통과(26개 프로젝트). 오늘 커밋 18개 전부 푸시됨

## 진행 중

- **`617d32c` CI 실행 결과 확인 대기** — 초록이면 리눅스 AOT 탐색 수정까지 실증된다
  (ubuntu 는 매번 테스트에서 막혀 AOT 단계 미도달이었다)

## 다음 (우선순위 순)

1. **CI 초록 확인** — `gh run list` 로 `617d32c` 실행 결과. 새 플레이크가 보이면
   같은 방식(유계 폴링·재정착)으로 정비
2. **Phase 21 릴리스 엔지니어링** — ⚠ SemVer 정책 문서화 · API 호환성 검사 CI ·
   `PublicAPI.Shipped` 확정 · NuGet 패키징(축별 + 분석기, 템플릿 PackageReference 전환)
3. 레거시 MongoDB 계정 폐기 확인 — 사용자 몫

## 블로커 / 열린 결정

- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 · 틱 지터
  리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙 · DocFX 경고 58건
- **GC 기본값 잠정**(ADR-0031) · CI 24h soak 미정 · 벤치 게이트 buffers 비율은 러너
  소음으로 판정됐으나 재발하면 명세 기준 재검토
- **환경** — SDK 10.0.201 고정. Docker Desktop 실행 중

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0068 까지)
- 측정: `docs/BENCHMARKS.md` · 진단 대역: `docs/DIAGNOSTICS.md`
- 가이드: `docs/GETTING-STARTED.md` · `GUIDE-CHOOSING-AXES` · `GUIDE-PERFORMANCE` · `GUIDE-MIGRATION`
