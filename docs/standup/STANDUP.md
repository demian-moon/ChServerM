# ChServerM — 현재 상태

**최종 갱신**: 2026-08-12 (4차 랩)
**현재 단계**: Phase 21 (8/10 — ⭐ 라이선스 확정) + Phase 22 진행 · **🟢 CI 완전 초록**

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.67×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20 (9/10)** — 샘플 4종 · 가이드 4종 · 진단 분석기 · 템플릿 · DocFX(경고 0)
- **Phase 21 (8/10)** — SemVer(ADR-0069) · 32개 + 메타 패키지(ADR-0070) · 결정적
  빌드 · 릴리스 노트 · ⭐ **라이선스 Apache-2.0 확정**(`653af67`, ADR-0071 —
  LICENSE·NOTICE·전 패키지 동봉 + **서드파티 전수 감사 충돌 0**. 발행의 법적
  선결 조건 해소). 잔여 = 지원 정책·서명(사용자) + 릴리스 시점 작업 2건
- **Phase 22 진행** — AOT 4종 양 OS CI 확증 · 컨테이너 실증 + K8s 매니페스트 ·
  게이트 13개 중 11개 재점검 충족 · 드레인 플레이크 종결

## 진행 중

- 없음. 작업 트리 clean, 전 커밋 푸시·CI 초록 (이 랩 커밋 제외)
- 게이트 잔여 2건(의도된 보류): 24h soak 정식 판 · 확장성 5지점 곡선(조용한 환경)
- K8s 실클러스터 apply·rollout 검증(로컬 클러스터 부재)

## 다음 (우선순위 순)

1. **🔶 사용자 결정 잔여 2건** — ② 지원 정책: 지원 버전 범위 + 보안 패치 기간
   (권고: 최신 minor + 직전 minor 보안 6개월. 결정 시 SECURITY.md·VERSIONING.md
   반영은 자율) ③ 서명: 무료 1단계 attestation 유력 — **저장소 공개 전환 시점**
   결정 필요(현 private, attestation 은 공개 저장소 무료. 결정 시 release.yml
   골격은 자율)
2. 저작권 명의 확정(선택) — 현재 "The ChServerM Authors" 관례, 교체 가능
3. Phase 22 잔여 — 문서 전체 검토(죽은 링크·낡은 예제) · 최종 보안 검토 ·
   확장성 곡선/24h soak(조용한 환경) · 최종 성능 기준선 공표
4. 릴리스 시점 작업(첫 릴리스 후): API 호환성 CI · `Shipped` 확정 · 템플릿 전환

## 블로커 / 열린 결정

- **🔶 지원 정책·서명(+저장소 공개 시점)** — 사용자 대기 (라이선스는 해소됨)
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙
- **GC 기본값 잠정**(ADR-0031) · 레거시 MongoDB 계정 폐기 확인(사용자)
- **환경** — SDK 10.0.201 고정(Dockerfile FROM 태그 락스텝). Docker Desktop 실행 중,
  K8s 클러스터 비활성

## 참조

- 상세 이력: `docs/standup/history/` (08-12 는 4세션)
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0071 까지)
- 라이선스: `LICENSE` · `NOTICE` · `THIRD-PARTY-NOTICES.md`
- 버전 정책: `docs/VERSIONING.md` · 측정: `docs/BENCHMARKS.md` · 배포: `deploy/README.md`
- 가이드: `GETTING-STARTED` · `GUIDE-CHOOSING-AXES` · `GUIDE-PERFORMANCE` · `GUIDE-MIGRATION`
