# ChServerM — 현재 상태

**최종 갱신**: 2026-08-05 (7차 — wrap 누락으로 2026-08-06 소급 기록)
**현재 단계**: **Part III — Phase 9 보안 진행 중** (⚡ **게이트 두 조건 충족** — 위협 모델 전 항목 매핑 + 인증 전 패킷 차단 테스트. 잔여 항목은 게이트와 무관하게 계속)
**진행률**: 84/222 항목 (Phase 0 `13/17` · 1 `13/21` · 2 `7/11` · 3 `4/6` · 4 `10/10` · 5 `12/12` · 6 `7/7` · 7 `5/7` · 8 `8/16` · 9 `5/13`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Part II 데이터 경로 게이트 전부 ✅** — 프레이밍(고정16B+varint, 조각 재조립 ADR-0015) /
  버퍼(`PooledBufferWriter` 50ns/0B, ADR-0016) / TCP(순수 Socket 확정 ADR-0001, 1만 접속·169k RPS) /
  직렬화 3종(기본 MemoryPack, ADR-0013) / 디스패치 소스 제너레이터(ADR-0014, AOT 실증) /
  실행 모델(ADR-0008, 물리 코어 효율 95%)
- **위협 모델 ✅** — `docs/THREAT-MODEL.md`: 신뢰 경계 5 · 공격 표면 9 · 위협 22(STRIDE)
  전 항목 완화책 매핑 + 레거시 결함 14종 역매핑. 버전 협상 요구사항 R-1~R-5 고정
- **전송 보안 축 실동 ✅ (ADR-0017)** — TLS 1.3(`SslStream`) 위임, 자체 암호 금지.
  `ITransportSecurity`(Core) → `ChServerM.Security.Tls`(의존 0) → `UseTransportSecurity()`.
  같은 에코 핸들러가 InMemory/TCP × TLS 동작. **실측: RPS −2.5%·p50 +50µs**
- **상태별 화이트리스트 ✅ (T-19, Phase 9 게이트)** — `IConnectionStateFeature`(상태
  비트마스크, 의미는 앱 정의 — ADR-0004) + `MessageStateFilterMiddleware`(FrozenDictionary
  + 비트 AND 프레임당 1회, **기본 거부**). 레거시 `AllowedPkState` 기본 전부 허용 결함의 역.
  인증 전 특권 메시지 = 응답 없이 커넥션 종료(4001)가 2전송 테스트로 확인
- 테스트 **423개** 통과(413 + 신규 10: 상태 필터 종단 6 · 조립 검증 4),
  전체 게이트(-WarnAsError·audit·AOT publish+실행) 통과

## 진행 중

- **Phase 9 잔여(게이트 조건 아님)** — ⚠ 버전 협상 **와이어 구현**(설계는 ADR-0017
  확정: TLS 채널 안 ClientHello`[min,max]`→ServerHello, 헤더 v1 영구 동결) /
  `IAuthenticator` / 인가 미들웨어 / `IPayloadCodec`(압축→암호화 순서, T-11·18) /
  시크릿 관리 / 입력 검증 / Tls 인증서 파일 로딩·회전 운영 경로 / `/security-review`
- **Phase 7·8 잔여(게이트 조건 아님)** — 누락 핸들러 검출, 리플렉션 폴백, 코어 제한 재측정
- **감사 보류분** — `MessageContext` 내부 메서드 public(ADR 후보), ConnectionId 세대 활용(세션 계층)

## 다음 (우선순위 순)

1. **버전 협상 핸드셰이크 와이어 구현** — `ClientHello/ServerHello` 고정 레이아웃
   (영구 동결, 직렬화 축 비의존) + `FrameworkMessageIds` + 호스팅 통합
2. `IAuthenticator` — 레거시 `AuthM`(PasswordHasher) 승계(싱글턴·옵션 명시) +
   1회용·만료 토큰(크로스 커넥션 리플레이, ADR-0017 결정 4 잔여 몫)
3. (후보) `IPayloadCodec` 압축(압축→암호화 순서 고정) 또는 Tls 인증서 운영 경로

## 블로커 / 열린 결정

- **레거시 하드코딩 자격증명** — `ServerGlobals.cs:103` (기존 항목 유지)
- MemoryPack `VersionTolerant` 주의 계약의 제너레이터 진단 승격 여부 — ADR-0013 부정 항목.
  버전 협상 와이어 구현과 함께 판단
- LoadRunner 램프업 무한 루프(죽은 서버 대상 — 고아 프로세스 유발) — Phase 12 항목 추가됨

## 이번에 배운 것 (같은 실수 반복 방지)

- **증분 빌드는 분석기를 건너뛴다** — CA2000 위반이 증분 빌드에서 잠복하다가
  Rebuild 에서야 드러났다. 분석기 게이트를 믿으려면 클린 빌드가 조건이다
- **세션을 wrap 없이 닫으면 이력이 샌다** — 7차 세션이 커밋만 남기고 종료돼
  다음 날 소급 재구성했다. 코드 커밋 직후가 wrap 시점이다
- **PowerShell 5.1 + 여러 줄 커밋 메시지 = `git commit -F <파일>`** — 인수 안 `"` 가 깨진다

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
  설정하므로 IDE·단독 `dotnet test` 에서만 수동 설정
- 부하 측정: `dotnet run -c Release --project Bench/ChServerM.Bench.LoadRunner -- server|client ...`
  (`--transport socket|kestrel` ADR-0001 재현 / `--tls true|false` ADR-0017 A/B)
- 측정 환경이 다르면 `BENCHMARKS.md` 에 ENV 프로필을 새로 등록한다(교차 비교 금지)

## 참조

- 계획: `docs/ROADMAP.md` / 설계 결정: `docs/DECISIONS.md`
  (ADR-0000·0001·0002·0004~0017 채택 / 0003 폐기 — 미결 ADR 없음)
- 위협 모델: `docs/THREAT-MODEL.md` (Phase 9 의 근거 문서 — 새 축·표면 추가 시 갱신)
- 진단 규칙: `docs/DIAGNOSTICS.md` (CHSM0xxx 가드 · CHSM1xxx 제너레이터)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (최근: `2026-08-05.md` 1~7차)
- 레거시 분석: `docs/legacy/00-overview.md`
