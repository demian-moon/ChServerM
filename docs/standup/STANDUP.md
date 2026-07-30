# ChServerM — 현재 상태

**최종 갱신**: 2026-07-30
**현재 단계**: Phase 0 — 기반 (4/7)

## 완료된 것
- 규약 수립 — `CLAUDE.md`(하드 룰, 축 12개, 디자인 패턴, 성능 스택) + `docs/` 5종 + `standup` 스킬 (`b3a6f07`)
- Phase 0 빌드 골격 — `net10.0`/C# 14 props, 중앙 패키지 관리, Performance·Reliability를 error로 올린 `.editorconfig`, `ChServerM.slnx`, `Core` + `Core.Tests`, `eng/build.ps1` + GitHub Actions (`4413e01`)
- **Core 무의존 2중 가드 검증 완료** — `CHSM0001`(MSBuild, 선언 시점) + `CoreDependencyTests`(런타임). 참/거짓 양성 모두 실측
- 최종 파이프라인 통과 — build 0 errors / 0 warnings(`/warnaserror`), test 2/2
- 레거시 분석 — `docs/LEGACY-INVENTORY.md`. `LegacyServer/`는 로컬 참조 전용(gitignore). UML·`.fbs`·`IoPipelineSrvM.cs` 정독, 실제 버그 2건 + 하드 룰 위반 4건 식별
- 환경 — `engineering` 플러그인 설치(스킬 10개), MCP 25개 폴더 한정 차단

## 진행 중
- Phase 0 부분 완료 2건 — 솔루션 폴더(`Client`/`Bench`/`Samples` 미생성), AOT 컴파일 검증(실행 프로젝트 부재로 Phase 2+ 활성화)
- `LegacyServer/` 미판정 자산 — `PacketM.cs`(26K) 등 프레이밍 계열, Pool/Concurrent/Scheduler 계열

## 다음 (우선순위 순)
1. **Phase 1 — Core 추상화.** `IMessageSerializer`부터. 코드는 사용자 지시 후 작성
2. `IFrameDecoder`/`IFrameEncoder` ↔ `IMessageSerializer` 경계 확정 — ADR-0002를 코드로 굳히는 지점
3. `IExecutionModel` — 유저별 순서 보장 계약 반영
4. `LegacyServer/` 프레이밍 계열 정독 → 인벤토리 판정 채우기

## 블로커 / 열린 결정
- **ADR-0003 확인 대기** — 목표 워크로드를 실시간 게임 서버 + 매치메이킹으로 확정할지. 승인 전이라 ROADMAP Phase 순서(4 vs 8)는 손대지 않았다
- **ADR-0001 미결** — raw TCP를 Kestrel Socket Transport 재사용으로 갈지. 레거시가 `TcpClient`+`NetworkStream`이라 Kestrel 쪽으로 기울었으나 Phase 4 벤치마크로 확정
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값. Phase 5에서 4자 벤치마크. 크로스 언어 클라이언트 요구가 생기면 결론이 바뀐다
- 벤치마크 기준선 없음 — 성능 주장을 아직 아무것도 검증할 수 없는 상태

## 작업 방식
- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상 파일·타입·시그니처·근거를 제시하고 승인을 받는다. 조사·분석·문서는 자율
- 커밋: 코드와 문서를 분리한다. 문서는 `/standup wrap`에서 `chore(standup)`으로

## 참조
- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md`
- 레거시 판정: `docs/LEGACY-INVENTORY.md`
