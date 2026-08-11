# ChServerM — 현재 상태

**최종 갱신**: 2026-08-11 (3차)
**현재 단계**: Phase 16 — 대체 전송 **사실상 완료** (8/9, 보류 1) · 전체 165/235

## 완료된 것

- **Part I~II 골격** — Core 무의존 추상화, 고정 헤더 프레이밍, 직렬화 4종, 소스 제너레이터
  디스패치, 파티션 실행 모델, TLS, 미들웨어 파이프라인
- **Part III 운영 축(Phase 9~12) 완결** — 수용 제어·속도 제한·열화·헬스·크래시·관측.
  성능 기준선 169k RPS · 1만 접속 · 코어 확장 14.79×/16코어
- **Phase 13 세션(11/13)** — 적합성 21종 × 4저장소 + ⭐ Redis Cluster 지원(ADR-0058)
- **Phase 14 데이터 테이블(7/8)** · **Phase 15 클러스터 완료(17/17)**
- ⭐ **Phase 16 전송 5종 완성** — 인메모리·TCP·HTTP(ADR-0057)·WebSocket(ADR-0059)·
  QUIC(ADR-0060)이 `CrossTransportTests` 14항목을 **같은 핸들러 코드**로 통과.
  `stateless-web` 프로필 2노드 세션 외부화 증명(ADR-0004 합격 기준).
  전송 세금 실측: 지연 바닥 −6%/+9µs, **고동시성은 다중화가 5.9× 역전**
- 전 스위트 **1,234개** 통과(21개 프로젝트), 전체 재빌드 경고 0

## 진행 중

- 없음. 작업 트리 clean

## 다음 (우선순위 순)

1. **방향 결정** — Part V(Phase 17 틱·시간 동기화, 선택 축) vs Part VI(Phase 20 개발자
   경험·패키징). Part III~IV 가 완결됐으므로 어느 쪽도 게이트 위반이 아니다
2. **원격 CI 실행** — 5전송 매트릭스 + ubuntu 러너의 msquic 지원 확인
   (미지원이면 QUIC 은 건너뜀으로 기록된다 — 깨지지 않는다)
3. **레거시 MongoDB 계정 폐기 확인** — 사용자 몫(2026-08-11 결정: 저장소 조치 없음)

## 블로커 / 열린 결정

- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 ·
  TCP 전송 위의 다중 노드
- **⚠ 노드 번호 자동 배정 없음**(ADR-0056) · **리더는 상호 배제가 아니다**(ADR-0054) ·
  **Redis 난수 버전은 재사용 금지가 확률적**(2⁻⁶⁴, ADR-0058)
- **조건부 보류** — UDP 전송(비신뢰 데이터그램 수요 시, ADR-0060) · Excel→CSV(ADR-0046) ·
  Tsavorite(ADR-0038) · WS·QUIC 성능 측정(수요 시) · wss/HTTPS 옵션 노출
- **GC 기본값 잠정**(ADR-0031) · **CI 24h soak 스케줄 미정**
- **환경 상태** — SDK **10.0.201** 고정. Docker Desktop 실행 중(Redis·Garnet·PostgreSQL·Consul)

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0060 까지)
- 측정: `docs/BENCHMARKS.md` · 프로파일링: `docs/PROFILING.md` · 일관성: `docs/CONSISTENCY.md`
- 진단 대역: `docs/DIAGNOSTICS.md` (CHSM0xxx · CHSM1xxx · CHSM2xxx)
