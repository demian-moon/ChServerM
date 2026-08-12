# ChServerM — 현재 상태

**최종 갱신**: 2026-08-12 (5차 랩)
**현재 단계**: ⭐⭐ **v0.1.0 첫 발행 완료** — Phase 21 (8/10, 잔여 = 릴리스 후속 2건) · **🟢 CI·릴리스 초록**

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.67×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20 (9/10)** — 샘플 4종 · 가이드 4종 · 진단 분석기 · 템플릿 · DocFX(경고 0)
- **Phase 21 (8/10)** — SemVer(ADR-0069) · 메타 패키지(ADR-0070) · 결정적 빌드 ·
  릴리스 노트 · 라이선스 Apache-2.0(ADR-0071) · 지원 정책(ADR-0072, SECURITY.md) ·
  Trusted Publishing + 출처 증명(ADR-0073) · 심볼 게시
- ⭐⭐ **v0.1.0 첫 발행** (2026-08-12) — 저장소 공개(자격증명 노출 사용자 승인) ·
  PVR 활성 · README 신설 · release.yml(게이트→pack→증명→발행) ·
  **nuget.org 33개 전수 색인 + 공개 피드 실소비 검증 + 출처 증명 소비자 검증**
- **Phase 22 진행** — AOT 4종 양 OS · 컨테이너 실증 + K8s 매니페스트 ·
  게이트 11/13 재점검 · 플레이크 2건 종결(드레인 창·TTL 판정 불능 Skip)

## 진행 중

- 없음. 작업 트리 clean, 전 커밋 푸시·CI 초록 (이 랩 커밋 제외)

## 다음 (우선순위 순)

1. **발행 후속 4건 (전부 자율 작업 가능)** — ① 템플릿 ProjectReference →
   PackageReference 전환 ② GETTING-STARTED·README 의 "미발행" 문구 갱신
   ③ API 호환성 CI 활성화(`PackageValidationBaselineVersion=0.1.0` — 기준선 생김)
   ④ GitHub Release 노트(eng/release-notes.ps1)
2. Phase 22 잔여 — 문서 전체 검토(죽은 링크·낡은 예제) · 최종 보안 검토 ·
   확장성 곡선/24h soak(조용한 환경) · 최종 성능 기준선 공표 · K8s 실클러스터
3. 1.0 시점 작업: `Shipped` 확정(공개 표면 동결) · 전 Phase 게이트 최종 재확인

## 블로커 / 열린 결정

- **없음 — 사용자 결정 3건 전부 해소** (라이선스·지원 정책·서명/공개). 남은
  사용자 몫은 선택 사항뿐: 저작권 명의 교체("The ChServerM Authors" → 법인/개인)
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙
- **GC 기본값 잠정**(ADR-0031)
- **환경** — SDK 10.0.201 고정(Dockerfile FROM 락스텝). **저장소 공개** ·
  nuget.org 정책 = whoomch · Docker Desktop 실행 중, K8s 비활성

## 참조

- 상세 이력: `docs/standup/history/` (08-12 는 5세션)
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0073 까지)
- 발행: https://www.nuget.org/profiles/whoomch · 라이선스: `LICENSE` · 보안: `SECURITY.md`
- 버전·릴리스 절차: `docs/VERSIONING.md` · 측정: `docs/BENCHMARKS.md` · 배포: `deploy/README.md`