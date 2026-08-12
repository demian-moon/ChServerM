# ChServerM — 현재 상태

**최종 갱신**: 2026-08-12 (3차 랩)
**현재 단계**: Phase 21 잔여 = 사용자 결정 + Phase 22 진행 · **🟢 CI 완전 초록**

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.67×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20 (9/10)** — 샘플 4종 · 가이드 4종 · 진단 분석기 · 템플릿 · DocFX
  (⭐ **경고 58건 전량 해소 → 0건** — 정체는 cref 가 아니라 PublicAPI 수동 포함 중복)
- **Phase 21 자율 항목 전부 소진** — SemVer(ADR-0069) · 32개 + 메타 패키지(ADR-0070)
  패키징·소비 검증 · 결정적 빌드 · 릴리스 노트
- **Phase 22 진행** — AOT 샘플 4종 양 OS CI 확증 · 컨테이너 이미지 실증 + K8s
  매니페스트 · ⭐ **전 Phase 게이트 1차 재점검: 13개 중 11개 충족**(표는 history
  08-12) · 드레인 플레이크 종결(`bf2f913`)

## 진행 중

- **⚠ 미커밋 2파일** — `Server/Directory.Build.props`(PublicAPI 중복 포함 제거,
  RS0016 음성 테스트로 게이트 생존 확인) · `docs/docfx/docfx.json`(메타 제외).
  커밋 제안: `build(eng): DocFX 경고 58건 전량 해소`
- 게이트 잔여 2건(의도된 보류): **24h soak 정식 판** · **확장성 5지점 곡선**
  (둘 다 조용한 환경에서 수동 실행 — 릴리스 직전)
- K8s 실클러스터 apply·rollout 검증(로컬 클러스터 부재)

## 다음 (우선순위 순)

1. 미커밋 2파일 커밋 → 푸시 → CI 확인
2. **🔶 사용자 결정 3건** — ① 라이선스(발행 선결) ② 지원 정책 ③ 패키지 서명
3. Phase 22 잔여 — 문서 전체 검토(죽은 링크·낡은 예제) · 최종 보안 검토 ·
   확장성 곡선/24h soak(조용한 환경) · 최종 성능 기준선 공표
4. 릴리스 시점 작업(첫 릴리스 후): API 호환성 CI · `Shipped` 확정 · 템플릿 전환

## 블로커 / 열린 결정

- **🔶 라이선스·지원 정책·서명** — 사업 결정, 사용자 대기
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙
- **GC 기본값 잠정**(ADR-0031) · 레거시 MongoDB 계정 폐기 확인(사용자)
- **환경** — SDK 10.0.201 고정(Dockerfile FROM 태그와 락스텝). Docker Desktop 실행 중,
  K8s 클러스터 비활성

## 참조

- 상세 이력: `docs/standup/history/` (08-12 는 3세션)
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0070 까지)
- 버전 정책: `docs/VERSIONING.md` · 측정: `docs/BENCHMARKS.md` · 배포: `deploy/README.md`
- 가이드: `GETTING-STARTED` · `GUIDE-CHOOSING-AXES` · `GUIDE-PERFORMANCE` · `GUIDE-MIGRATION`
