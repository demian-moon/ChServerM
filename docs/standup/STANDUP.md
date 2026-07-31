# ChServerM — 현재 상태

**최종 갱신**: 2026-07-31
**현재 단계**: Phase 0 — 빌드 기반 & 품질 게이트 (6/13)
**로드맵 규모**: Phase 0~22 (Part I~VI), 201 항목. 번호 대응표는 `docs/ROADMAP.md` 상단

## 완료된 것
- 규약 — `CLAUDE.md`: 하드 룰, 축 12개, 디자인 패턴, 성능 스택, **9절 병렬성 규약**(9.1~9.9)
- Phase 0 빌드 골격 — `net10.0`/C# 14 props, 중앙 패키지 관리, Performance·Reliability를 error로 올린 `.editorconfig`, `.gitattributes`, `ChServerM.slnx`, `Core` + `Core.Tests`, `eng/build.ps1` + GitHub Actions (`4413e01`, `72af7e8`)
- **Core 무의존 2중 가드 검증 완료** — `CHSM0001`(MSBuild, 선언 시점) + `CoreDependencyTests`(런타임). 참/거짓 양성 모두 실측
- **로드맵 상업용 재구성** — 68항목/12Phase → 201항목/23Phase. 보안·복원력·성능회귀·DX·게임프리미티브 신설, DoD 5조건 + Phase별 게이트 (`db0e88f`)
- **레거시 전수 정밀 분석** — 27,300줄 → `docs/legacy/` 14종/272KB. 승계 자산 22종, 치명 버그 40건+, 초기 판정 15건 정정 (`f3eacfc`·`dd44d24`·`fcf5059`)
- 분석 결과를 ROADMAP·규약에 반영 (`70d5955`), 병렬성 규약 신설 (`6d4ea16`)
- 파이프라인 통과 — build 0 errors / 0 warnings(`/warnaserror`), test 2/2

## 진행 중
- Phase 0 부분 완료 2건 — 솔루션 폴더(`Client`/`Bench`/`Samples` 미생성), AOT 컴파일 검증(실행 프로젝트 부재로 Phase 2 활성화)
- Phase 0 신규 품질 게이트 5건 미착수 — `Bench/` 골격, 커버리지, public API 게이트, 취약점 감사, 의존성 자동화

## 다음 (우선순위 순)
1. **Phase 1 — Core 추상화.** **기본 계약 5건이 축 인터페이스보다 먼저다** (에러 모델 / 생명주기·취소 / ID 타입 / 시간 추상화 / 진단 계약). 전부 ⚠이고 레거시 분석이 각 항목에 구체적 요구를 붙여놨다
2. 시작 지점 후보 — **에러 모델**(핫패스 예외 금지의 실체) 또는 **ID 타입**(`ObjectId` 노드 성분을 지금 정해야 Phase 15가 막히지 않는다)
3. `IFrameDecoder`/`IFrameEncoder` ↔ `IMessageSerializer` 경계 — ADR-0002를 코드로 굳히는 지점
4. Phase 0 잔여 품질 게이트 5건 (Phase 1과 병행 가능)

## 블로커 / 열린 결정
- **ADR-0001 미결** — raw TCP를 Kestrel Socket Transport 재사용으로 갈지. 레거시가 `TcpClient`+`NetworkStream`이라 성능 상한이 낮음이 확인됐고 Kestrel 쪽으로 기울었으나 Phase 5 프로토타입 벤치마크로 확정
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값. Phase 6 4자 벤치마크
- **ADR-0005 검증 조건** — 키 기반 샤딩이 **코어 수 대비 선형성**을 증명하지 못하면 무효. Phase 8 게이트
- 벤치마크 기준선 없음 — `Bench/` 골격 미생성. 다만 **정량 근거는 레거시 실측으로 확보**(`docs/legacy/00-overview.md` 5절)
- 미푸시 커밋 10개 — CI가 한 번도 돌지 않았다

## 작업 방식
- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상 파일·타입·시그니처·근거를 제시하고 승인을 받는다. 조사·분석·문서는 자율
- 동시성 코드는 `CLAUDE.md` **9절을 먼저 읽는다.** 특히 9.1(공유 대신 파티셔닝)·9.2(`finally` 복원)·9.6(유계 큐)
- 승계 대상 구현 전 `docs/legacy/`의 해당 문서를 읽고, **"새 코드에 절대 옮기면 안 되는 것"** 을 체크리스트로 쓴다
- 커밋: 코드와 문서를 분리한다. 문서는 `/standup wrap`에서 `chore(standup)`으로

## 참조
- 레거시 분석: `docs/legacy/00-overview.md` ← 승계 자산·결함 유형·정량 근거
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md` (ADR-0000·0002·0004·0005 채택 / 0001 미결 / 0003 폐기)
- 상세 이력: `docs/standup/history/`
