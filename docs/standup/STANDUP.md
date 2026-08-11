# ChServerM — 현재 상태

**최종 갱신**: 2026-08-11 (7차)
**현재 단계**: Phase 19 **완료**(4/4) · Phase 20 **사실상 완료**(9/10) · 전체 191/239 → **다음은 Phase 21 릴리스 엔지니어링**

## 완료된 것

- **Part I~III 완결** — Core 무의존 추상화 → 운영 축. 기준선 169k RPS · 1만 접속 ·
  코어 확장 14.67×/16코어(효율 91.7%)
- **Phase 13~18** — 세션 · 데이터 테이블 · 클러스터 · 전송 5종(같은 핸들러) ·
  실시간 프리미티브 · 룸/존 & AOI
- ⭐ **Phase 20 개발자 경험 9/10** — 샘플 4종(전 조합 자체 검증) · 시작 가이드(전 코드
  실검증) · ⚠ 진단 분석기 `ChServerM.Analyzers`(CHSM3001~3003, ADR-0066) · `dotnet new`
  템플릿 2종 · DebuggerDisplay 17종 · 예외 메시지 전수 감사 15건 전량 수정 ·
  가이드 3종(축 선택·성능·마이그레이션) · DocFX API 사이트 327페이지(ADR-0067)
- ⭐ **Phase 19 매치메이킹 완료** — `ChServerM.Matchmaking`(선택 축, ADR-0068): 확장 창
  대기열·상호 창 호환·만료로 드러나는 최대 대기·파티 원자 티켓 FFD 패킹·유계 큐.
  레이팅(Elo)은 샘플에 — 공식과 결과 반영은 프레임워크 밖이 설계다
- 전 스위트 **1,384개** 통과(26개 프로젝트), 전체 재빌드 경고 0. 오늘 커밋 13개

## 진행 중

- 없음. 작업 트리 clean (이 랩 커밋 제외)

## 다음 (우선순위 순)

1. **Phase 21 릴리스 엔지니어링** — ⚠ SemVer 정책 문서화 · API 호환성 검사 CI ·
   `PublicAPI.Shipped` 확정 · NuGet 패키징(축별 + 분석기 패키지, 템플릿 PackageReference
   전환) · SourceLink · 결정적 빌드 검증
2. **원격 CI 실행** — 오늘 커밋 13개(Matchmaking·Analyzers 테스트 포함) 매트릭스 확인
3. 레거시 MongoDB 계정 폐기 확인 — 사용자 몫

## 블로커 / 열린 결정

- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy(수요 시) ·
  분석기 후보 규칙(TryWrite·Validate 현재 값 강제) · DocFX 경고 58건(릴리스 전 정리)
- **GC 기본값 잠정**(ADR-0031) · **CI 24h soak 미정** · 템플릿 CI 검증(Phase 21 패키징 때)
- **환경** — SDK 10.0.201 고정. Docker Desktop 실행 중(Redis·Garnet·PostgreSQL·Consul)

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0068 까지)
- 측정: `docs/BENCHMARKS.md` · 진단 대역: `docs/DIAGNOSTICS.md` (CHSM0~3xxx)
- 가이드: `docs/GETTING-STARTED.md` · `docs/GUIDE-CHOOSING-AXES.md` ·
  `docs/GUIDE-PERFORMANCE.md` · `docs/GUIDE-MIGRATION.md`
