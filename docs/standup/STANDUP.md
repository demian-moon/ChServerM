# ChServerM — 현재 상태

**최종 갱신**: 2026-08-13 (공개 API 문서 발행 반영)
**현재 단계**: **v0.1.0 발행 완료 · Phase 22 (1.0 출시 준비)** — 최종 보안 검토 통과 · 확장성 게이트 재검증 통과 · **🟢 CI 초록**

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · **코어 확장 14.90×/16코어(2026-08-12 재검증)** ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20~21 (릴리스 엔지니어링)** — SemVer 락스텝 · 메타 패키지 · 결정적 빌드 ·
  Apache-2.0 · Trusted Publishing + 출처 증명 · API 호환성 게이트(기준선 0.1.0)
- ⭐⭐ **v0.1.0 발행 완결** — nuget.org 33개 색인·실소비 검증 · GitHub Release(자산 63개)
- **Phase 22 진행** — AOT 4종 · 컨테이너/K8s · 문서 전체 검토(링크 0건) ·
  ⭐ **최종 보안 검토 통과**(Phase 10~22 283파일/~29k 삽입, 신규 취약점 0건) ·
  ⭐ **확장성 게이트 재검증 통과**(16코어 14.90×/효율 93.1%, 08-07 기준선 유지) ·
  **부분 soak 11h48m 통과**(2026-08-13, 슬롯 0 드레인 + 메모리 평탄, exit 0) ·
  ⭐ **공개 API 문서 발행**(DocFX → GitHub Pages, https://demian-moon.github.io/ChServerM/)

## 진행 중

- 없음. 작업 트리 clean, 전 커밋 푸시·CI 초록

## 다음 (우선순위 순)

1. **정식 24h soak** (`CHSM_SOAK_SECONDS=86400`) — **2026-08-13 부분 soak(11h48m) 통과**
   (슬롯 0 드레인 + 메모리 평탄, exit 0)로 de-risk. 형식 요건인 완전 24h 판만 남음(별도 세션,
   수치 남기는 상세 로거로). 완료 시 **최종 성능 기준선 공표**도 함께 닫힌다
2. **1.0 시점 작업 (순서 고정, VERSIONING.md 절차)** — 표면 점검 → `Unshipped→Shipped`
   전량 이동(**2,280줄 / 32어셈블리 — 공개 표면 동결은 1.0 선언과 동시, 되돌릴 수 없다**) →
   VersionPrefix 1.0 → 전 Phase 게이트 최종 재확인 → v1.0 태그
3. K8s 실클러스터 apply·rollout 검증(클러스터 확보 시)
4. (선택) Consul `BuildView` 신뢰성 버그 수정 — 노드 ID 범위/중복 미처리로 멤버십 루프 정지
   (가용성 영향, 보안 무관 — 2026-08-12 보안 검토에서 발견)

## 블로커 / 열린 결정

- **1.0 선언 시점(사용자 결정)** — Shipped 동결은 줄 제거가 곧 파괴적 변경이라 되돌릴 수 없다.
  24h soak · 최종 성능 기준선 · 전 Phase 게이트 최종 재확인이 선결
- 선택: 저작권 명의 교체("The ChServerM Authors")
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙
- **GC 기본값 잠정**(ADR-0031)
- **환경** — SDK 10.0.201 고정 · 저장소 공개 · nuget.org Trusted Publishing(whoomch) ·
  **Docker Desktop 미실행 확인(2026-08-12)** · K8s 비활성
- **⚠ 작업 규약 메모** — 한글 파일 기계 치환은 Edit 도구로(PS 5.1 인코딩이 UTF-8 을 파괴한
  사고 이력) · PS 5.1 커밋 메시지는 `-F` 파일로(따옴표 인자 경계 파손) ·
  **게이트 도구는 exit 0 ≠ 통과, 산출물(배수 표·측정 JSON)로 확인** ·
  **`.gitignore` 는 CP949 인코딩 — Edit 로 건드리지 말 것**(생성물 폴더 내 authored 파일은 `git add -f`)

## 참조

- 상세 이력: `docs/standup/history/` (08-12 7세션 · 08-13 soak+문서발행)
- 계획: `docs/ROADMAP.md` · 결정: `docs/DECISIONS.md` (ADR-0073 까지) · 측정: `docs/BENCHMARKS.md` (2026-08-12 확장성 재검증)
- 발행: https://www.nuget.org/profiles/whoomch ·
  릴리스: https://github.com/demian-moon/ChServerM/releases/tag/v0.1.0 ·
  **API 문서**: https://demian-moon.github.io/ChServerM/
- 버전·릴리스: `docs/VERSIONING.md` · 보안: `SECURITY.md` · 배포: `deploy/README.md`
