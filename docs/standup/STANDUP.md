# ChServerM — 현재 상태

**최종 갱신**: 2026-08-11 (6차)
**현재 단계**: Phase 20 — 개발자 경험 (4/10) · 전체 182/239 · **Phase 19 는 사용자 결정으로 Phase 20 뒤로**

## 완료된 것

- **Part I~III 골격 완결** — Core 무의존 추상화 → 운영 축(수용 제어·관측·크래시)까지.
  성능 기준선 169k RPS · 1만 접속 · 코어 확장 14.79×/16코어
- **Phase 13~18** — 세션 · 데이터 테이블 · 클러스터(17/17) · 전송 5종(같은 핸들러) ·
  실시간 프리미티브(틱·휠·시간 동기화) · 룸/존 & AOI(1회 인코딩 브로드캐스트, ~400ns/멤버)
- ⭐ **Phase 20 착수, 4/10** (2026-08-11 6차, **미커밋**):
  샘플 3종(`EchoServer`·`StatelessWeb` — stateless-web 프로필 첫 실행체·`GameRoom`) ·
  시작 가이드(코드 전부 실검증) · ⚠ 진단 분석기 `ChServerM.Analyzers`(CHSM3001~3003,
  ADR-0066) · `dotnet new` 템플릿 2종(종단 검증). 부분: DebuggerDisplay 17종 ·
  예외 메시지 감사+상위 수정
- 전 스위트 **1,368개** 통과(25개 프로젝트), 전체 재빌드 경고 0

## 진행 중

- **⚠ 이번 세션 산출물 전부 미커밋** — 커밋 분할 제안이 history 에 있다. 사용자 확인 대기
- 디버깅 지원(DebuggerTypeProxy 수요 시) · 에러 메시지 잔여(상태 가드 7개소 등)

## 다음 (우선순위 순)

1. **미커밋 산출물 커밋** — feat(samples) / feat(analyzers) / feat(templates) /
   docs(guide) / chore: DebuggerDisplay·에러 메시지
2. **Phase 20 잔여** — 가이드 문서 3종(아키텍처·성능 튜닝·마이그레이션) + DocFX 검토.
   분량이 커서 새 세션 집중 권장
3. **Phase 19 매치메이킹** — 큐 설계 ADR(대기 시간 vs 품질, 틱 vs 이벤트 구동)부터.
   레이팅 공식(Glicko/WengLin)은 Samples 행(ADR-0004)
4. 원격 CI 실행 — 신규 Analyzers 테스트 포함 매트릭스 확인

## 블로커 / 열린 결정

- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Excel→CSV(ADR-0046) · Tsavorite(ADR-0038) ·
  WS·QUIC 성능 측정 · 틱 지터 리눅스 수치 · 쿼드트리 · 분석기 후보 규칙(TryWrite 등)
- **GC 기본값 잠정**(ADR-0031) · **CI 24h soak 미정** · 템플릿 CI 검증(Phase 21 패키징 때)
- **환경** — SDK 10.0.201 고정. Docker Desktop 실행 중(Redis·Garnet·PostgreSQL·Consul)

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0066 까지)
- 측정: `docs/BENCHMARKS.md` · 진단 대역: `docs/DIAGNOSTICS.md` (CHSM0~3xxx)
- 시작 가이드: `docs/GETTING-STARTED.md`
