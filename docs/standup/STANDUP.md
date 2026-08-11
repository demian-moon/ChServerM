# ChServerM — 현재 상태

**최종 갱신**: 2026-08-11 (4차)
**현재 단계**: Phase 17 — 틱 & 시간 동기화 **완료** (6/6) · 전체 172/239

## 완료된 것

- **Part I~II 골격** — Core 무의존 추상화, 고정 헤더 프레이밍, 직렬화 4종, 소스 제너레이터
  디스패치, 파티션 실행 모델, TLS, 미들웨어 파이프라인
- **Part III 운영 축(Phase 9~12) 완결** — 수용 제어·속도 제한·열화·헬스·크래시·관측.
  성능 기준선 169k RPS · 1만 접속 · 코어 확장 14.79×/16코어
- **Phase 13 세션(11/13) · Phase 14 데이터 테이블(7/8) · Phase 15 클러스터(17/17)**
- **Phase 16 전송 5종 완성(8/9, UDP 조건부 보류)** — 인메모리·TCP·HTTP·WS·QUIC 이
  같은 핸들러로 `CrossTransportTests` 14항목 통과. 전송 세금·다중화 5.9× 역전 실측
- ⭐ **Phase 17 실시간 프리미티브 완료** — `ChServerM.RealTime`(선택 축 첫 사례: Core 만
  참조·Core 는 모름·Core 에 계약 없음). 틱 루프(절대 스케줄+유계 캐치업+예산 초과 관측),
  계층 타이밍 휠(레거시 최고 자산 승계+결함 전수 수정, ABA 는 세대+상태 패킹 CAS),
  시간 동기화(µs 고정 단위로 serverFrequency 환산 소멸·NTP 4-타임스탬프·IQR RTT).
  실측: 휠 17.9ns/발화·0 B, 지터는 스핀 구간 > OS 해상도(16ms)일 때만 p99 0µs
- 전 스위트 **1,295개** 통과(22개 프로젝트), 전체 재빌드 경고 0

## 진행 중

- 없음. 작업 트리 clean

## 다음 (우선순위 순)

1. **방향 결정** — Phase 18(룸/존 & AOI — Part V 계속. ⚠ 충돌 판정은 단위 테스트 먼저,
   레거시 미수정 버그 8건·공간 분할은 `MortonCodeM` 만 생존) vs Phase 20(개발자 경험)
2. **원격 CI 실행** — 5전송 매트릭스 + ubuntu msquic 지원 확인(미지원이면 건너뜀 기록)
3. **레거시 MongoDB 계정 폐기 확인** — 사용자 몫(2026-08-11 결정: 저장소 조치 없음)

## 블로커 / 열린 결정

- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 ·
  TCP 전송 위의 다중 노드
- **⚠ 노드 번호 자동 배정 없음**(ADR-0056) · **리더는 상호 배제가 아니다**(ADR-0054) ·
  **Redis 난수 버전은 재사용 금지가 확률적**(2⁻⁶⁴, ADR-0058)
- **조건부 보류** — UDP 전송(ADR-0060) · Excel→CSV(ADR-0046) · Tsavorite(ADR-0038) ·
  WS·QUIC 성능 측정(수요 시) · wss/HTTPS 옵션 노출 · 틱 지터 리눅스 수치 ·
  휠 멀티 프로듀서 경합 측정
- **GC 기본값 잠정**(ADR-0031) · **CI 24h soak 스케줄 미정**
- **환경 상태** — SDK **10.0.201** 고정. Docker Desktop 실행 중(Redis·Garnet·PostgreSQL·Consul)

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0063 까지)
- 측정: `docs/BENCHMARKS.md` · 프로파일링: `docs/PROFILING.md` · 일관성: `docs/CONSISTENCY.md`
- 진단 대역: `docs/DIAGNOSTICS.md` (CHSM0xxx · CHSM1xxx · CHSM2xxx)
