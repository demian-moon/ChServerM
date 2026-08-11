# ChServerM — 현재 상태

**최종 갱신**: 2026-08-12 (08-11 심야분 랩)
**현재 단계**: Phase 21 릴리스 엔지니어링 (4/10) · 전체 194/239 · **🟢 CI 완전 초록** · 남은 것 대부분이 사용자 결정

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.67×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹(ADR-0068)
- **Phase 20 (9/10)** — 샘플 4종 · 시작 가이드 · 진단 분석기(CHSM3xxx) · 템플릿 ·
  가이드 3종 · DocFX. 잔여: DebuggerTypeProxy(수요 시)
- ⭐ **CI 완전 초록** — 8/4 이후 첫 3잡 동시 통과. 협상 거부의 2겹 경합(서버 Abort
  데이터 파괴 + 클라이언트 ConnectionClosed 취소 경합) 등 실결함 3건과 타이밍 취약
  테스트 5건을 근본 수정. 리눅스 AOT 검증 복원
- ⭐ **Phase 21 (4/10)** — ⚠ SemVer 정책(ADR-0069, 락스텝 0.1.0 + 표면 5개 판정표) ·
  NuGet 패키징 기반(32종 pack + 소비 검증, 분석기 analyzers 경로) ·
  결정적 빌드 검증(DLL 62개 동일 해시 실측) · 릴리스 노트 생성기
- 전 스위트 **1,384개** 통과(26개 프로젝트) · 8/11 총 커밋 26개 전부 푸시됨

## 진행 중

- 없음. 작업 트리 clean (이 랩 커밋 제외)

## 다음 (우선순위 순)

1. **🔶 사용자 결정 3건** — ① **라이선스**(발행 선결 조건 — 패키징 메타데이터에 의도적
   공백) ② 지원 정책(지원 버전·보안 패치 기간) ③ 패키지 서명 인프라
2. **메타 패키지 구성** 또는 **Phase 22 사전 점검**(Native AOT 샘플 전체 검증 ·
   컨테이너 이미지 + K8s 매니페스트)
3. 릴리스 시점 작업(첫 릴리스 후): API 호환성 CI(기준선 = 첫 패키지) · `Shipped` 확정(1.0)
4. 레거시 MongoDB 계정 폐기 확인 — 사용자 몫

## 블로커 / 열린 결정

- **🔶 라이선스·지원 정책·서명** — 사업 결정, 사용자 대기 (위 1번)
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 · 틱 지터
  리눅스 수치 · 쿼드트리 · DebuggerTypeProxy · 분석기 후보 규칙 · DocFX 경고 58건
- **GC 기본값 잠정**(ADR-0031) · CI 24h soak 미정 · 벤치 게이트 buffers 비율 재발 시 명세 재검토
- **환경** — SDK 10.0.201 고정. Docker Desktop 실행 중(Redis·Garnet·PostgreSQL·Consul)

## 참조

- 상세 이력: `docs/standup/history/` (8/11 은 이어서 1~8, 하루 8세션)
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0069 까지)
- 버전 정책: `docs/VERSIONING.md` · 측정: `docs/BENCHMARKS.md` · 진단: `docs/DIAGNOSTICS.md`
- 가이드: `GETTING-STARTED` · `GUIDE-CHOOSING-AXES` · `GUIDE-PERFORMANCE` · `GUIDE-MIGRATION`
