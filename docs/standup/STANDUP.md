# ChServerM — 현재 상태

**최종 갱신**: 2026-08-11 (2차)
**현재 단계**: Phase 16 — 대체 전송 (3/8) · 전체 160/234

## 완료된 것

- **Part I~II 골격** — Core 무의존 추상화, 고정 헤더 프레이밍, TCP/Kestrel 전송, 직렬화 4종,
  소스 제너레이터 디스패치, 파티션 실행 모델, TLS, 미들웨어 파이프라인
- **Part III 운영 축(Phase 9~12) 완결** — 수용 제어·속도 제한·우아한 열화·헬스체크·크래시
  처리·메트릭/추적/진단·MEL 로깅. 성능 기준선 169k RPS · 1만 접속 · 코어 확장 14.79×/16코어
- **Phase 13 세션 축(11/13)** — 적합성 스위트 21종을 네 저장소가 통과.
  ⭐ **Redis Cluster 지원**(ADR-0058: 난수 버전으로 `CROSSSLOT` 구조적 해소, 클러스터 모드
  컨테이너가 상시 회귀 게이트)
- **Phase 14 데이터 테이블(7/8)** · **Phase 15 클러스터 완료(17/17)** — Consul 멤버십,
  랑데뷰 라우팅, 뷰-유도 리더+정족수, 무중단 드레인, 노드 번호 임차
- ⭐ **Phase 16 착수 — `ChServerM.Transport.Http`**(ADR-0057): HTTP/2 스트림=커넥션,
  KestrelServer 직접 호스팅(NuGet 0). **`stateless-web` 프로필 완성** — 같은 핸들러가
  TCP·인메모리·HTTP 3전송 + 2노드 세션 외부화에서 동작(ADR-0004 합격 기준 성립)
- 전 스위트 **1,206개** 통과(21개 프로젝트), 전체 재빌드 경고 0

## 진행 중

- 없음. 작업 트리 clean

## 다음 (우선순위 순)

1. **Phase 16 계속** — HTTP 전송 성능 측정(프레임워크 세금 — ADR-0057 이 마감 전 조건으로
   명시) 또는 WebSocket 전송
2. **UDP 전송 방향 결정** — 자체 구현 vs LiteNetLib/ENet 어댑터. ⚠ ADR 필요
3. **레거시 자격증명 폐기 여부** — 여러 세션째 확인 대기

## 블로커 / 열린 결정

- **⚠ 레거시 자격증명** — `LegacyServer/.../ServerGlobals.cs:103` 의 하드코딩된 MongoDB
  접속 문자열. 재사용 계획은 없지만(ADR-0037) **저장소에 커밋된 사실 자체**를 유출로 취급해
  폐기·교체할 것인지 **여러 세션째 확인 대기**
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 ·
  TCP 전송 위의 다중 노드. 지금 검증은 인메모리 전송 + 단일 프로세스다
- **⚠ 노드 번호 자동 배정 없음**(ADR-0056) · **리더는 상호 배제가 아니다**(ADR-0054)
- **Redis 버전 계약의 성격** — 난수 버전은 재사용 금지가 확률적(2⁻⁶⁴, ADR-0058).
  절대 보장이 필요한 소비자는 카운터 기반 저장소(인메모리·PostgreSQL)를 고른다
- **Excel → CSV 변환 도구 조건부 보류**(ADR-0046) · **GC 기본값 잠정**(ADR-0031) ·
  **CI 24h soak 스케줄 미정**
- **환경 상태** — SDK **10.0.201** 고정. Docker Desktop 실행 중(Redis·Garnet·PostgreSQL·Consul)
- **보류 유지** — Tsavorite(ADR-0038) / Bulkhead 강제 / `FramesSent` /
  Phase 7 누락 핸들러 검출 / 회로 상태 진단 노출 / HTTP 전송 TLS(h2+ALPN)·수용 제어 배선

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0058 까지)
- 측정: `docs/BENCHMARKS.md` · 프로파일링: `docs/PROFILING.md` · 일관성: `docs/CONSISTENCY.md`
- 진단 대역: `docs/DIAGNOSTICS.md` (CHSM0xxx · CHSM1xxx · CHSM2xxx)
