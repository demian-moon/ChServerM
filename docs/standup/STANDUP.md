# ChServerM — 현재 상태

**최종 갱신**: 2026-08-12 (6차 랩)
**현재 단계**: **v0.1.0 발행 + 후속 완결** — Phase 21 (9/10, 잔여 = 1.0 시점의 Shipped 확정) · **🟢 CI 초록**

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.67×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20 (9/10)** — 샘플·가이드·분석기·DocFX(경고 0) · 템플릿(⭐ 메타 패키지
  참조로 전환 — nuget.org 복원 종단 검증)
- **Phase 21 (9/10)** — SemVer · 메타 패키지 · 결정적 빌드 · 릴리스 노트 ·
  Apache-2.0 · 지원 정책 · Trusted Publishing+출처 증명 · 심볼 ·
  ⭐ **API 호환성 게이트**(기준선 0.1.0 + pack 단계, 양방향 검증)
- ⭐⭐ **v0.1.0 발행 완결** — nuget.org 33개 색인·실소비 검증 ·
  **GitHub Release**(노트 + 자산 63개 = attestation 영구 검증 대상)
- **Phase 22 진행** — AOT 4종 · 컨테이너/K8s · 게이트 1차 11/13 ·
  ⭐ **문서 전체 검토 완료**(링크 61개 md 전수 0건 · 낡은 서술 정정 · ARCHITECTURE
  최신화) · 플레이크 3건 종결(드레인 창 · TTL 판정 불능 Skip · 틱-휠 측정 원점)

## 진행 중

- 없음. 작업 트리 clean, 전 커밋 푸시·CI 초록 (이 랩 커밋 제외)

## 다음 (우선순위 순)

1. **최종 보안 검토** (Phase 22) — Phase 9 방식(다단계 리뷰 + confidence 필터)
   재적용. 새 세션 집중 권장
2. **조용한 환경 측정 2건** — `eng/scaling-gate.ps1` 전체 곡선 + 24h soak
   (`CHSM_SOAK_SECONDS=86400`) → 이때 최종 성능 기준선 공표도 함께
3. 1.0 시점 작업: `Shipped` 확정(공개 표면 동결) → 전 Phase 게이트 최종 재확인
   → 1.0 태그
4. K8s 실클러스터 검증(클러스터 확보 시) · 템플릿 CI 자동 검증(수요 시)

## 블로커 / 열린 결정

- **없음** — 사용자 결정 전부 해소. 선택: 저작권 명의 교체("The ChServerM Authors")
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙
- **GC 기본값 잠정**(ADR-0031)
- **환경** — SDK 10.0.201 고정. **저장소 공개** · nuget.org Trusted Publishing
  정책(whoomch) · Docker Desktop 실행 중, K8s 비활성
- **⚠ 작업 규약 메모** — 한글 파일의 기계 치환은 Edit 도구로(PS 5.1 기본 인코딩이
  UTF-8 파일을 파괴한 사고 1회, 즉시 복구)

## 참조

- 상세 이력: `docs/standup/history/` (08-12 는 6세션)
- 계획: `docs/ROADMAP.md` · 결정: `docs/DECISIONS.md` (ADR-0073 까지)
- 발행: https://www.nuget.org/profiles/whoomch ·
  릴리스: https://github.com/demian-moon/ChServerM/releases/tag/v0.1.0
- 버전·릴리스: `docs/VERSIONING.md` · 보안: `SECURITY.md` · 측정: `docs/BENCHMARKS.md` ·
  배포: `deploy/README.md`
