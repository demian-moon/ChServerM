# ChServerM — 현재 상태

**최종 갱신**: 2026-08-19 (2차 — D 목록 반영)
**현재 단계**: **v0.2.0 · Phase 22 (1.0 출시 준비)** — 감사 A·B·C·D 전량 반영 · 게이트 7단계 초록

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS(ENV-B) · 코어 확장 14.90×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20~21 + v0.1.0 발행 완결** — 릴리스 엔지니어링 · nuget.org 33개 · API 문서 사이트
- ⭐⭐ **전수 감사 + 전량 반영 (2026-08-18~19, v0.2.0)** — 8영역 정밀 감사(~60건) 후
  **P0 4 + P1 10 + 설계 결정 4(ADR-0074·0075) + D 목록 19건 전부 반영**. 잔여는 E 백로그
  (1.0 후)뿐. ApiCompat 억제 파일 3개로 파괴 변경 의도 명시. 게이트에 gc-config(산출물
  검증) 신설, 결정적 빌드 검증을 release.yml 에 연결
- ⭐ **FlatGameRoom 종합 샘플 + 문서** — FlatBuffers·로그인·세션 재개·룸 브로드캐스트
  총망라, 자체 검증 24체크, 가이드 라이브(`/samples/flatgameroom.html`)
- **CI 5일 적색 해소** — 8-13 SSH.NET High(NU1903)가 원인, 전이 고정으로 수정.
  Consul 회귀 테스트 2종이 ubuntu CI 에서 실행·통과(건너뜀 0) 확인

## 진행 중

- 없음. 마지막 커밋(`f7c62dc` D 목록) **푸시·CI 확인 대기**

## 다음 (우선순위 순)

1. **푸시 + CI 초록 확인** — D 목록 커밋(전송·TLS 표면 변경 포함)
2. **정식 24h soak** (`CHSM_SOAK_SECONDS=86400`, **상세 로거 필수**) — 감사 반영이 끝난
   현재 빌드가 최종 후보다. 완료 시 **최종 성능 기준선 공표**도 함께 닫힌다
3. **1.0 시점 작업 (순서 고정)** — 표면 점검 → Unshipped→Shipped 전량 이동(되돌릴 수
   없음, ApiCompat 억제 파일 3개는 이동 시 정리) → VersionPrefix 1.0 → 전 게이트 최종
   재확인 → v1.0 태그
4. K8s 실클러스터 apply·rollout 검증(클러스터 확보 시)

## 블로커 / 열린 결정

- **1.0 선언 시점(사용자 결정)** — 24h soak · 최종 기준선 · 게이트 재확인이 선결
- **감사 E 백로그(1.0 후)** — `docs/audit/2026-08-18/00-summary.md` E 절이 정본
  (PooledBufferWriter 보유 상한 · AOI SoA/SIMD · Redis EVALSHA · 분석기 CHSM3004/3005 ·
  개방 루프 부하 모드 · 전송 수락 골격 통합 · ISpanFormattable 로깅 벤치 등)
- 선택: 저작권 명의 교체("The ChServerM Authors")
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy
- **GC 기본값 잠정**(ADR-0031) — 단, 산출물 검증은 이제 게이트가 기계 확인
- **환경** — SDK 10.0.201 고정 · Trusted Publishing(whoomch) · Docker Desktop 미실행
  (컨테이너 테스트는 CI 에서) · K8s 비활성
- **⚠ 작업 규약 메모** — 한글 파일 기계 치환은 Edit 도구로 · PS 5.1 커밋 메시지는 `-F`
  파일로 · **PS 5.1 파이프로 patch 를 옮기지 말 것(인코딩 파괴 — 파일 단위 checkout 로 통합)** ·
  **PS 5.1 StrictMode 는 단일 요소 배열을 언롤한다(함수·if 반환 모두) — .Count 앞엔 @()** ·
  게이트 도구는 exit 0 ≠ 통과, 산출물로 확인 · `.gitignore` 는 CP949 — Edit 금지

## 참조

- 상세 이력: `docs/standup/history/` (08-19: 감사 반영 1·2차)
- **감사**: `docs/audit/2026-08-18/` (00-summary 반영 현황 = A·B·C·D 완료, E 잔존)
- 계획: `docs/ROADMAP.md` · 결정: `docs/DECISIONS.md` (ADR-0075 까지) ·
  측정: `docs/BENCHMARKS.md` (기준선 ENV-B 정정됨)
- 발행: https://www.nuget.org/profiles/whoomch ·
  API 문서: https://demian-moon.github.io/ChServerM/ ·
  샘플 가이드: `/samples/flatgameroom.html`
