# ChServerM — 현재 상태

**최종 갱신**: 2026-08-12
**현재 단계**: Phase 21 릴리스 엔지니어링 (4/10, 잔여 대부분 사용자 결정) + Phase 22 사전 점검 착수 · **🟢 CI 초록 유지**

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.67×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹(ADR-0068)
- **Phase 20 (9/10)** — 샘플 4종 · 가이드 4종 · 진단 분석기 · 템플릿 · DocFX
- **Phase 21 (4/10 + 패키징 잔여 소진)** — SemVer 정책(ADR-0069) · 축별 32개 패키징 ·
  결정적 빌드 검증 · 릴리스 노트 생성기 · ⭐ **메타 패키지 `ChServerM`**(ADR-0070,
  최소 조합 8개 — 메타 하나로 조립·기동·생성 코드 경로 소비 검증)
- ⭐ **Phase 22 사전 점검** — **Native AOT 샘플 4종 전체 검증**(전부 CI 게이트 편입,
  경고=오류 publish + 실행 통과) · EchoServer SIGTERM 정상 종료(실결함 수정) ·
  **컨테이너 이미지 실증**(AOT ~51MB, 외부 TCP 200회 왕복 + SIGTERM 드레인 exit 0) ·
  K8s 매니페스트(kubeconform -strict 통과)

## 진행 중

- **⚠ 2026-08-12 세션 산출물 전부 미커밋** — 메타 패키지(`Server/ChServerM` + slnx) ·
  샘플 csproj 3종 + EchoServer SIGTERM · `.dockerignore` + `deploy/` · ADR-0070.
  커밋 분할 제안은 history 2026-08-12 참조
- 컨테이너·K8s 항목의 잔여: **실클러스터 apply·rollout 검증**(로컬 K8s 클러스터 부재)

## 다음 (우선순위 순)

1. **미커밋 코드 커밋(사용자 확인) → 푸시 → 원격 CI 확인** — AOT 게이트가 1개→4개로
   늘어난 매트릭스의 첫 실행이자 리눅스 쪽 AOT 4종 확증
2. **🔶 사용자 결정 3건** — ① 라이선스(발행 선결 조건) ② 지원 정책 ③ 패키지 서명 인프라
3. 릴리스 시점 작업(첫 릴리스 후): API 호환성 CI · `Shipped` 확정(1.0) ·
   템플릿 PackageReference 전환
4. Phase 22 잔여 — K8s 실클러스터 검증 · 전 Phase 게이트 재확인 · 최종 보안 검토 ·
   문서 전체 검토(DocFX 경고 58건) · 최종 성능 기준선 공표

## 블로커 / 열린 결정

- **🔶 라이선스·지원 정책·서명** — 사업 결정, 사용자 대기
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 · 틱 지터
  리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙 · DocFX 경고 58건
- **GC 기본값 잠정**(ADR-0031) · CI 24h soak 미정 · 레거시 MongoDB 계정 폐기 확인(사용자)
- **환경** — SDK 10.0.201 고정(Dockerfile FROM 태그와 락스텝 — global.json 올릴 때 함께).
  Docker Desktop 실행 중, K8s 클러스터는 비활성

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0070 까지)
- 버전 정책: `docs/VERSIONING.md` · 측정: `docs/BENCHMARKS.md` · 배포: `deploy/README.md`
- 가이드: `GETTING-STARTED` · `GUIDE-CHOOSING-AXES` · `GUIDE-PERFORMANCE` · `GUIDE-MIGRATION`
