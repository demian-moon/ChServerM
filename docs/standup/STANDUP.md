# ChServerM — 현재 상태

**최종 갱신**: 2026-07-30
**현재 단계**: Phase 0 — 기반 (0/6)

## 완료된 것
- 프로젝트 방향 확정: .NET 10 / C# 14 기반 모듈형 초고성능 서버·클라이언트 프레임워크
- `CLAUDE.md` 작성 — 아키텍처 하드 룰, 교체 가능한 축 12개, 적용 디자인 패턴, 성능 스택, 커밋 규약
- `docs/ROADMAP.md` 작성 — Phase 0~11 + 백로그
- `standup` 스킬 신규 작성 (`.claude/skills/standup/SKILL.md`) — 기존 설치본이 없어 프로젝트 스킬로 직접 정의

## 진행 중
- 없음 (코드 작업 미착수)

## 다음 (우선순위 순)
1. Phase 0 — `ChServerM.sln` + 솔루션 폴더 골격 생성
2. Phase 0 — `Directory.Build.props` / `Directory.Packages.props` / `.editorconfig` 확정
3. Phase 1 — `ChServerM.Core` 추상화 설계 착수 (`IMessageSerializer`부터)

## 블로커 / 열린 결정
- **ADR-0001 (미결)**: raw TCP 전송을 Kestrel Socket Transport 재사용으로 갈지, 순수 `Socket`+Pipelines로 직접 구현할지 — Phase 4 진입 전 결정 필요
- **ADR-0002 (미결)**: 기본 직렬화 축 선정 — Phase 5 벤치마크 결과로 확정
- 목표 워크로드 미확정 (게임 실시간 / 일반 API / 혼합) — 전송·동시성 모델 우선순위가 여기에 달림

## 참조
- 상세 이력: `docs/standup/history/`
- 계획: `docs/ROADMAP.md`
- 설계 결정: `docs/DECISIONS.md`
