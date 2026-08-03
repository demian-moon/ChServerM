# ChServerM — 현재 상태

**최종 갱신**: 2026-08-03 (2차)
**현재 단계**: Phase 0 게이트 통과 ✅ — Part I~II 병행 진행 중
**진행률**: 48/218 항목 (Phase 0 `13/17` · 1 `12/21` · 2 `7/11` · 4 `7/9` · 5 `3/12` · 8 `6/15`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트 절차, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종. 승계 자산 22종, 치명 버그 40건+.
  **레거시 트리 자체도 저장소에 있다**(승계 대상은 아님, 솔루션 미포함)
- **Core 추상화** — 44 타입 / 16 인터페이스. 무의존 2중 가드 유지
- **프레이밍** — 16B 고정 헤더. 무상태 디코더, 퍼징 통과, 프레임당 할당 0
- **전송 2종** — 인메모리 루프백 + raw `Socket`+Pipelines TCP. 둘 다 3단 종료
- **실행 모델** — 파티션당 전용 스레드 + 단일 FIFO. 읽기 루프 고정으로 프레임당 큐 비용 0
- **빌더 + 샘플** — Native AOT 1.9MB 정상 동작
- ✅ **ADR-0004 충족** — 같은 핸들러가 인메모리·TCP 양쪽에서 동작 (14항목 × 2전송)
- ✅ **ADR-0005 검증 조건 충족** — 물리 코어 구간 효율 **95% 이상**, 순서 보장 비용 **3.9%**
- ✅ **Phase 0 게이트 통과** — `eng/build.ps1` 6단계 + public API 게이트 +
  **원격 CI ubuntu·windows 양쪽 통과**
- 테스트 **291개** 통과. 커버리지: Framing 95.0% / InMemory 83.4% / Concurrency 76.5% /
  Tcp 70.2% / Hosting 66.2% / Core 60.8%

## 진행 중

- **Phase 5 TCP 미완 9건** — **1만 동시 접속 미검증(게이트 조건)**, 에코 RPS·p99 레이턴시,
  idle timeout, 송신 배칭, 종료 레이스, 소켓 옵션 확대, 거부 이유 통지
- **Phase 8 미완 9건** — 유저/세션 단위 고정(현재 커넥션 단위), CPU 어피니티,
  스케줄러 공정성, 작업 상자 풀 파티션별 분리, 실제 코어 제한 재측정, NUMA 재확인
- **Phase 1 미완 9건** — `ISessionStore`, `IMetricsSink`, `IPayloadCodec`, `ITransportSecurity`,
  인증·과부하·클러스터 계약, `docs/ARCHITECTURE.md`
- **Phase 2 미완 4건** — DI 통합, `IConfiguration`, `.UseSerializer()` 전역화, 편의 문법 위치
- **Phase 4 미완 2건** — varint 디코더(두 번째 구현이 없어 `IFrameDecoder`는 아직 가설),
  조각 재조립
- **Phase 0 미완 4건** — 게이트 조건은 아니다. `Client/` 솔루션 폴더(클라이언트 어셈블리 필요),
  SDK 업그레이드(의도적으로 미룸), 커버리지 임계값, ReportGenerator

## 다음 (우선순위 순)

1. **Phase 5 게이트 — 1만 동시 접속 + p99 레이턴시 기준선.** Phase 0 이 열렸으니 여기다.
   종단 부하는 NBomber 가 필요하다
2. **Phase 6 직렬화 어댑터** — 두 번째 구현으로 직렬화 축을 증명하고
   ADR-0002 남은 부분(페이로드 기본값) 확정
3. **`IMetricsSink`** — 지금까지 발견한 조용한 실패가 전부 "관측되지 않음"으로 수렴한다.
   큐 깊이·드롭 수·거부 수가 보이지 않으면 관리되지 않는다

## 블로커 / 열린 결정

- **ADR-0001 미결** — 순수 `Socket` 프로토타입 완성. Kestrel Socket Transport 버전과
  벤치마크로 붙여야 확정된다
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값. Phase 6 4자 벤치마크
- **ADR-0005 잔여 검증 2건** — 파티션 수 스윕은 코어 제한의 **근사**다
  (`Process.ProcessorAffinity` 가 리눅스 미지원이라 `taskset` 으로 감싼 재측정 필요).
  그리고 단일 소켓 머신 한 대의 측정이라 NUMA 재확인이 남았다
- **ADR-0007 미해결 항목** — 조각 재조립이 생기면 재조립 버퍼 상한·만료가 별도로 필요하다
- **편의 문법 의존 방향** — `.UseTcp(port)` 는 전송이 `Hosting` 을 참조해야 성립하는데
  `Hosting → 어댑터 → Core` 방향을 뒤집는다
- **레거시 하드코딩 자격증명** — `ServerGlobals.cs:103` 의 `mongodb://smck:smck4@localhost:27017`.
  localhost 개발용이고 이미 `docs/legacy/` 에 인용돼 있었다. 그 인스턴스가 개발 머신 밖에
  존재한다면 교체할 것

## 이번에 배운 것 (같은 실수 반복 방지)

- **축 간 제약은 조립 계층에서만 검사할 수 있다.** 각 축은 자기 설정만 검증한다.
  `CompositionGuard` 형태로 계속 늘려간다 (ADR-0007)
- **로컬과 CI 의 SDK 가 다르면 CI 에서만 깨진다.** `global.json` 으로 고정했다.
  SDK 업그레이드는 이제 "새 분석기 규칙을 받아들이겠다"는 의도적 결정이다
- **테스트가 실패 지점을 특정하면 한쪽 플랫폼에서만 통과한다.** RST 도착 시점처럼
  타이밍에 달린 것은 "어느 호출에서 실패한다"가 아니라 "동작하지 않는다"를 검증한다
- **자동화 장치는 실패 경로를 실측해야 한다.** 취약점 감사를 켠 뒤 실제 취약 패키지를
  넣어보니 내 코드 버그로 **"발견했는데 exit 0"** 이었다 — 막으려던 실패 형태 그 자체다
- **"통과"가 "옳음"이 아니다.** 200KB 페이로드가 TCP 에서 통과한 것은 커널 버퍼가 흡수한 우연이었다
- **분석기가 지적한 기본값이 레거시 실패 패턴과 겹치는 일이 잦다.** RS0026 을 만나면
  기본값부터 의심한다 (CLAUDE.md 8.1)

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상 파일·타입·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율
- **주석은 한글, 모듈 단위 4계층** (`CLAUDE.md` 8.2)
- **public 표면을 바꾸면 승인 파일도 갱신한다** (`CLAUDE.md` 8.1). 게이트를 끄지 않는다
- 동시성 코드는 9절을 먼저 읽는다. 9.1(공유 대신 파티셔닝)·9.2(`finally` 복원)·9.6(유계 큐)
- 승계 대상 구현 전 `docs/legacy/` 의 해당 문서를 읽고 **"절대 옮기면 안 되는 것"** 을 체크리스트로 쓴다
- 분석기 정책을 풀 때는 **근거를 적는다** — `.editorconfig` 또는 해당 지점의 `#pragma`.
  지금까지: CA1028·CA1711·CA1716·CA2025, Tests/Samples/Bench 스코프 다수
- CI 확인은 `gh` CLI (`gh run list`, `gh run view --log-failed`)
- 커밋: 코드와 문서를 분리한다. 문서는 `/standup wrap` 에서 `chore(standup)` 으로

## 다른 환경에서 시작하기

```
git clone https://github.com/demian-moon/ChServerM.git
cd ChServerM
dotnet restore ChServerM.slnx
powershell -File eng/build.ps1 -Configuration Release -WarnAsError
```

- SDK 는 `global.json` 이 **10.0.1xx** 로 고정한다. 그 밴드가 설치돼 있어야 한다
- `gh` CLI 는 CI 확인용이라 선택이다
- `.claude/settings.json` 이 MCP 차단 설정을 들고 따라간다
  (`settings.local.json` 은 사용자 전역 gitignore 에 걸려 공유되지 않는다)
- `Client/` 빈 디렉터리는 git 이 추적하지 못한다 — 클라이언트 어셈블리를 만들 때 해소된다

## 참조

- 레거시 분석: `docs/legacy/00-overview.md`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0002·0004·0005·0006·0007 채택 / 0001 미결 / 0003 폐기)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: Ryzen 9 9900X, 물리 12 / 논리 24)
- 상세 이력: `docs/standup/history/`
