# ChServerM — 현재 상태

**최종 갱신**: 2026-08-19
**현재 단계**: **v0.2.0 (전수 감사 반영) · Phase 22 (1.0 출시 준비)** — 게이트 6단계 전부 통과 · 로컬 커밋 미푸시

## 완료된 것

- **Part I~III + Phase 13~19 완결** — 기준선 169k RPS · 코어 확장 14.90×/16코어 ·
  전송 5종 · 클러스터 · 실시간 프리미티브 · 룸/AOI · 매치메이킹
- **Phase 20~21 + v0.1.0 발행 완결** — 릴리스 엔지니어링 전체 · nuget.org 33개 색인 ·
  API 문서/시각 가이드 발행(https://demian-moon.github.io/ChServerM/)
- **Phase 22 진행** — AOT 4종 · 컨테이너/K8s · 문서 검토 · 최종 보안 검토 · 확장성 재검증 ·
  부분 soak 11h48m 통과
- ⭐⭐ **전수 감사 + 전량 반영 (2026-08-18~19, v0.2.0)** — 전 어셈블리 8영역 정밀 감사
  (`docs/audit/2026-08-18/`, ~60건). **P0 4건 + P1 10건 전부 수정**(TickLoop 틱 유실 ·
  TimerWheel 풀 ABA · Consul 멤버십 루프 정지 · DispatchStatus 기본값=성공 · TLS 핸드셰이크
  타임아웃 · 서킷 브레이커 취소 오염 등) + **설계 결정 4건**(노드 0 예약 ADR-0074 ·
  FrameCodecCapabilities ADR-0075 · Room.Disband 스냅샷 · 미방출 메트릭 제거).
  파괴적 변경 → 0.2.0 승격 + ApiCompat 억제 파일. SSH.NET High 취약점 전이 고정

## 진행 중

- 없음. 작업 트리 clean. **커밋 3개(`e22b4cc`·`9e8451c`·standup) 미푸시 — CI 미검증**

## 다음 (우선순위 순)

1. **푸시 + CI 초록 확인** — 특히 신규 Consul 회귀 테스트 2종(로컬 Docker 부재로 건너뜀)
2. **감사 D 목록(1.0 전 권장) 반영** — `docs/audit/2026-08-18/00-summary.md` D 절
   (전송 축 비대칭 · SslStreamCertificateContext · 기본값 재조정 · 운영 하드닝 게이트류)
3. **정식 24h soak** (`CHSM_SOAK_SECONDS=86400`, 상세 로거 필수) — 감사 반영된 최종
   빌드로. 완료 시 **최종 성능 기준선 공표**도 함께 닫힌다
4. **1.0 시점 작업 (순서 고정)** — 표면 점검 → Unshipped→Shipped 전량 이동(되돌릴 수 없음)
   → VersionPrefix 1.0 → 전 게이트 최종 재확인 → v1.0 태그
5. K8s 실클러스터 apply·rollout 검증(클러스터 확보 시)

## 블로커 / 열린 결정

- **1.0 선언 시점(사용자 결정)** — 24h soak · 최종 기준선 · D 목록 처리 · 게이트 재확인이 선결
- 감사 E 백로그(1.0 후) — PooledBufferWriter 보유 상한 · AOI SoA/SIMD · Redis EVALSHA ·
  분석기 CHSM3004/3005 · 개방 루프 부하 모드 · 전송 수락 골격 통합 등(00-summary E 절)
- 선택: 저작권 명의 교체("The ChServerM Authors")
- **⚠ 검증되지 않은 클러스터 조건** — 별도 OS 프로세스 · 실제 네트워크 분단 · TCP 위 다중 노드
- **조건부 보류** — UDP(ADR-0060) · Tsavorite(ADR-0038) · WS·QUIC 성능 측정 ·
  틱 지터 리눅스 수치 · 쿼드트리 · DebuggerTypeProxy
- **GC 기본값 잠정**(ADR-0031)
- **환경** — SDK 10.0.201 고정 · nuget.org Trusted Publishing(whoomch) ·
  Docker Desktop 미실행(Consul·Redis·PG 통합 테스트는 CI 에서) · K8s 비활성
- **⚠ 작업 규약 메모** — 한글 파일 기계 치환은 Edit 도구로(PS 5.1 인코딩 사고 이력) ·
  PS 5.1 커밋 메시지는 `-F` 파일로 · 게이트 도구는 exit 0 ≠ 통과, 산출물로 확인 ·
  `.gitignore` 는 CP949 — Edit 금지(생성물 폴더 내 authored 파일은 `git add -f`)

## 참조

- 상세 이력: `docs/standup/history/` (08-19 감사+반영 세션)
- **감사**: `docs/audit/2026-08-18/` (00-summary 가 실행 계획의 정본)
- 계획: `docs/ROADMAP.md` · 결정: `docs/DECISIONS.md` (**ADR-0075 까지**) ·
  측정: `docs/BENCHMARKS.md`
- 발행: https://www.nuget.org/profiles/whoomch ·
  API 문서: https://demian-moon.github.io/ChServerM/ ·
  버전·릴리스: `docs/VERSIONING.md`(0.x Shipped 정책 명문화됨)
