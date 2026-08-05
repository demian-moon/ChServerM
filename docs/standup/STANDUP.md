# ChServerM — 현재 상태

**최종 갱신**: 2026-08-05 (3차)
**현재 단계**: Phase 0 ✅ · Phase 5 게이트 ✅ · Phase 6 ✅ · **Phase 7 게이트 ✅ (5/7)** — Part I~II 병행 진행 중
**진행률**: 72/221 항목 (Phase 0 `13/17` · 1 `12/21` · 2 `7/11` · 4 `9/10` · 5 `11/12` · 6 `7/7` · 7 `5/7` · 8 `8/16`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Core 추상화** — 무의존 2중 가드. ADR-0010 논리 엔벨로프/와이어 헤더 분리
- **프레이밍 축** — 고정 16B + varint 두 구현으로 증명. varint 디코드가 오히려 2.1× 빠름(ENV-B 쌍 측정)
- **전송 축 — 세 실증(InMemory·Tcp·Kestrel 프로토타입)** — ADR-0001 확정: 순수 Socket 유지
  (Kestrel 대결 전 항목 ±3.2% 동률, p99 는 순수 소켓 우위). Phase 5 게이트 ✅ (1만 접속, 에코 146-169k RPS)
- **직렬화 축 — 실동 어댑터 3종으로 증명 완료(Phase 6 ✅)** — MemoryPack·Protobuf·FlatSharp 이
  같은 핸들러 코드로 동작(`SerializerSwapTests`). **기본값 = MemoryPack(ADR-0013)**,
  4자 벤치·스키마 진화 3포맷 테스트 완료
- **실행 모델 — ADR-0008 액터형** — 프레임당 할당 ~0B, 물리 코어 효율 95%
- **디스패치 소스 제너레이터 — Phase 7 게이트 ✅(ADR-0014)** — `[MessageHandler]` 발견 +
  CHSM1001~1007 컴파일 타임 검증(`docs/DIAGNOSTICS.md`) + `MapGeneratedHandlers` 생성.
  디스패치 벤치로 switch 직생성 탈락(배열 0.69ns < switch 0.88ns < Dict 1.93ns),
  생성 경로가 **Native AOT publish + 바이너리 실행 검증**까지 통과
- 테스트 **398개** 통과, 전체 게이트(-WarnAsError·audit·AOT publish+실행) 통과

## 진행 중

- **Phase 7 잔여 2건(게이트 조건 아님)** — 누락 핸들러 검출(메시지 레지스트리 설계 필요),
  리플렉션 폴백 디스패처(20.5ns+32B 실측 — 프로덕션 비권장 근거 확보됨)
- **Phase 4 잔여 1건** — 조각 재조립(`Fragmented`/`EndOfMessage`, 재조립 버퍼 상한 필수)
- **Phase 5 잔여 1건** — 송신 배칭 벡터드 send (실측 동반)
- **Phase 1 잔여** — `ISessionStore`, `IMetricsSink`, `IPayloadCodec`, `ITransportSecurity` 등
- **감사 보류분** — `MessageContext` 내부 메서드 public(ADR 후보), ConnectionId 세대 활용(세션 계층)

## 다음 (우선순위 순)

1. **Phase 5 마지막 잔여** — 송신 배칭 벡터드 send, 소량-다패킷 실측과 함께
2. **Phase 8 잔여** — 실제 코어 제한 재측정(`taskset`/`start /affinity`), 스케줄러 공정성
3. **Phase 2 잔여 결정** — `.UseSerializer()` 전역 등록 여부 (직렬화 축이 실물이 된 지금이 판단 시점)

## 블로커 / 열린 결정

- **레거시 하드코딩 자격증명** — `ServerGlobals.cs:103` (기존 항목 유지)
- MemoryPack 기본값의 주의 계약(롤링 배포 메시지는 `VersionTolerant` 명시)을
  Phase 7 제너레이터 진단으로 승격할지 — ADR-0013 부정 항목

## 이번에 배운 것 (같은 실수 반복 방지)

- **스키마 진화는 방향까지 검증한다** — MemoryPack 기본 모드는 구데이터→신리더만 허용.
  롤링 배포에서 반드시 발생하는 방향(신→구)이 실패 쪽이다. 호환성 주장은 양방향 테스트 없이 적지 않는다
- **교차 환경 비교 금지가 실제로 오판을 막았다** — 고정 헤더를 ENV-B 로 재측정하니 ENV-A 수치와
  달랐다(34 vs 29ns). 규칙 없이는 잘못된 비율을 기록했을 것
- **벤치 대결은 불리한 쪽에 기울여 설계한다** — Kestrel 쪽에 유리한 조건에서도 동률 → 결론이 강하다
- **빌드 타임 도구의 런타임 요구는 SDK 고정과 충돌한다** — FlatSharp.Compiler(net9) ↔ global.json(10.x).
  롤포워드를 빌드 스크립트에 고정해 로컬·CI 동일 재현
- **전역 MSBuild 속성은 참조 그래프 전체에 흐른다** — `--property:PublishAot=true` 가
  netstandard2.0 제너레이터까지 AOT 대상으로 만들어 게이트가 깨졌다. csproj 선언으로 충분하면 전역을 쓰지 않는다
- **서드파티 ILC 경고를 억제하면 실행 검증으로 상쇄한다** — 정적으로 증명 못 하는 "그 경로는
  실행 안 된다"는 바이너리를 돌려 증명한다. AOT 게이트에 publish 후 실행 검증(exit 0)이 추가된 이유다

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 커밋: 코드와 문서 분리. 문서는 `/standup wrap` 에서 `chore(standup)`
- CI 확인은 `gh` CLI (2.97.0 설치·인증 완료, `C:\Program Files\GitHub CLI\gh.exe`)

## 다른 환경에서 시작하기

```
git clone https://github.com/demian-moon/ChServerM.git
cd ChServerM
dotnet restore ChServerM.slnx
powershell -File eng/build.ps1 -Configuration Release -WarnAsError
```

- SDK 는 `global.json` 이 **10.0.1xx** 로 고정. 밴드가 없으면 dotnet-install 로
  사용자 로컬(`~\.dotnet`) 설치가 가장 싸다 (이 머신은 `~\.dotnet` 에 10.0.110 —
  비대화형 셸에서는 PATH 앞에 수동으로 얹어야 한다)
- **FlatSharp.Compiler 는 `DOTNET_ROLL_FORWARD=LatestMajor` 필요** — 빌드 스크립트가
  설정하므로 IDE 단독 빌드에서만 수동 설정
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
  (서버 모드 `--transport socket|kestrel` — ADR-0001 재현용)
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0001·0002·0004~0014 채택 / 0003 폐기 — 미결 ADR 없음)
- 진단 규칙: `docs/DIAGNOSTICS.md` (CHSM0xxx 가드 · CHSM1xxx 제너레이터)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (오늘: `2026-08-05.md`)
- 레거시 분석: `docs/legacy/00-overview.md`
