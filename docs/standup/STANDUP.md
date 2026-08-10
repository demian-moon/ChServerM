# ChServerM — 현재 상태

**최종 갱신**: 2026-08-10
**현재 단계**: Phase 14 — 데이터 테이블 & 설정 (3/5) · 전체 136/226

## 완료된 것

- **Part I~II 골격** — Core 무의존 추상화, 고정 헤더 프레이밍, TCP/Kestrel 전송, 직렬화 4종,
  소스 제너레이터 디스패치, 파티션 실행 모델, TLS, 미들웨어 파이프라인
- **Part III 운영 축(Phase 9~12) 완결** — 수용 제어·속도 제한·우아한 열화·헬스체크·크래시
  처리·메트릭/추적/진단·MEL 로깅. **회귀 방어 3종**(할당·비율·확장성) 모두 고의 회귀로 검증
- **성능 기준선 실측** — 169k RPS · 1만 접속 · 프레임당 0B · 코어 확장 14.79×/16코어 ·
  프레임워크 세금(raw Kestrel 대비) 저부하 −13.5% / 고동시성 **p99 −34%**
- **Phase 13 세션 축(10/13)** — `ISessionStore`(바이트+CAS+TTL). **적합성 스위트 21종을 네
  저장소가 통과**: 인메모리 · Redis · **Garnet**(어댑터 코드 0) · **PostgreSQL**. 세션 복구
  (회전 토큰 + CAS 좀비 차단) · 와이어 프로토콜 · 서킷 브레이커 · `docs/CONSISTENCY.md`
- **Phase 14 데이터 테이블(3/5)** — `ChServerM.DataTable` **선택 축**(Core 미참조).
  로딩 시점 **전수 검증** + **참조 무결성을 인덱스 변환과 같은 패스**로 · **핫 리로드**
  (검증 성공 시에만 교체, 읽기 쪽 동기화 0)
- 전 스위트 **923개** 통과, **전체 재빌드** 경고 0

## 진행 중

- 없음(작업 트리 clean)

## 다음 (우선순위 순)

1. **강타입 접근자 소스 제너레이터** — 서수 관리와 문자열 키를 없앤다. Phase 14 의 가장 큰
   값이며 ADR-0041 이 다음 증분으로 지목해 둔 것. Phase 7 디스패치 제너레이터 기반 재사용
2. **클라이언트-서버 표 버전 검증** — `ReloadableStaticTableSet.Generation` 위에 얹힌다
3. **CSV/Excel 빌드 타임 임포트 도구** — 런타임 어셈블리에 Excel 파서를 넣지 않는다
   (레거시 `ExcelLibM`·`ExcelODBCM`·`CsvParser` 는 전부 참조 0)
4. **Redis Cluster 미지원 해소**(Phase 13 잔여) — 쓰기 Lua 가 키 둘을 만져 `CROSSSLOT` 이
   난다. 방안 셋과 대가는 `CONSISTENCY.md` 4절. 클러스터 축(Phase 17)과 함께 결정
5. **미측정 보완** — 경합 아래 확장성 · 전용 머신 GC 재측정(ADR-0031) ·
   24h soak 를 CI 에 스케줄

## 블로커 / 열린 결정

- **⚠ 레거시 자격증명** — `LegacyServer/.../ServerGlobals.cs:103` 의 하드코딩된 MongoDB
  접속 문자열. **어댑터는 PostgreSQL 로 결정**됐으므로(ADR-0037) 재사용 계획은 없다. 다만
  **저장소에 커밋된 사실 자체가 남아 있으므로** 유출로 취급해 폐기·교체할 것인지는 여전히
  사용자 확인 대기
- **GC 기본값은 잠정** — 루프백 측정에서는 Workstation 이 p99 가 더 좋았다. 전용 머신
  재측정 전까지 확정 아님(ADR-0031)
- **CI 스케줄 미정** — 24h soak(비율 게이트는 이미 CI 상시)
- **환경 상태** — Docker Desktop 실행 중(Redis·Garnet·PostgreSQL 테스트용, 사용자 승인).
  진단 도구 3종(`dotnet-trace`/`counters`/`gcdump`) 전역 설치됨
- **보류 유지** — Tsavorite 어댑터(대상 부재, ADR-0038) / Bulkhead 강제 /
  `FramesSent`(FrameWriter static 제약) / Phase 7 누락 핸들러 검출 / 회로 상태 진단 노출

## 참조

- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0042 까지)
- 측정: `docs/BENCHMARKS.md` · 프로파일링: `docs/PROFILING.md` · 일관성: `docs/CONSISTENCY.md`
