# ChServerM — 현재 상태

**최종 갱신**: 2026-08-06 (5차)
**현재 단계**: **Part III — Phase 9 보안 진행 중** (구현 항목은 입력 검증 하나 남음 — 그 뒤 `/security-review` 로 마감)
**진행률**: 92/222 항목 (Phase 0 `13/17` · 1 `15/21` · 2 `7/11` · 3 `4/6` · 4 `10/10` · 5 `12/12` · 6 `7/7` · 7 `5/7` · 8 `8/16` · 9 `11/13`)

## 완료된 것

- **규약** — `CLAUDE.md`: 하드 룰, 축 12개, 9절 병렬성 규약, 8.1 공개 API 게이트, 8.2 주석 규약
- **레거시 전수 분석** — 27,300줄 → `docs/legacy/` 14종
- **Part II 데이터 경로 게이트 전부 ✅** — 프레이밍(고정16B+varint, 조각 재조립 ADR-0015) /
  버퍼(`PooledBufferWriter` 50ns/0B, ADR-0016) / TCP(순수 Socket 확정 ADR-0001, 1만 접속·169k RPS) /
  직렬화 3종(기본 MemoryPack, ADR-0013) / 디스패치 소스 제너레이터(ADR-0014, AOT 실증) /
  실행 모델(ADR-0008, 물리 코어 효율 95%)
- **Phase 9 골격 ✅** — 위협 모델(경계 5·표면 9·위협 22 전 매핑) / 전송 보안 TLS 1.3
  (ADR-0017, 실측 RPS −2.5%) / 상태별 화이트리스트(T-19, 기본 거부)
- **버전 협상 ✅ (2026-08-06)** — `VersionHandshakeCodec`(Core, 영구 동결 — R-2:
  부트스트랩은 어느 축에도 안 얹는다) + `UseVersionNegotiation()`(보안이 바깥 = 협상은
  TLS 안, R-4). 거부 = 서버 구간 실은 40004(R-3), 무응답 = 타임아웃 절단(T-16)
- **인증 축 ✅ (2026-08-06)** — `AuthenticationMiddleware`: 실패 = 옵션 무관 6000 종료
  (전용 `RejectedByAuthentication`, T-20 구조 봉쇄), 성공 = `GrantedStates` 상태 대체
  전이(T-19 와 한 몸). `ITokenReplayGuard` + 유계·TTL 인메모리(T-05, 검증→클레임 순서
  계약). `ChServerM.Security.AspNetIdentity`(ADR-0018) — 레거시 해시 형식 호환 +
  결함 4종 역해소
- **인가 축 ✅ (2026-08-06 2차, T-21)** — 2단 구조: 메시지 수준은 T-19+`GrantedStates`
  기본 거부가 담당, 자원 수준(소유자 검사 등)은 `AuthorizationMiddleware` 보호 목록 +
  `IAuthorizationPolicy`. 거부 = 6001+옵션(인증과 의도적 비대칭). 조립 순서
  **필터→인증→인가**는 `Build()` 가 검증. Phase 1 인증·인가 계약 마감
- **압축 축 ✅ (2026-08-06 3차, ADR-0019)** — `IPayloadCodec`: 해제 상한 **필수 인자**
  (T-18 생략 불가) + 자기서술 블롭(버퍼 확보 전 선언 검증). 첫 어댑터 LZ4(K4os) —
  비압축성 최악 경로에서 Brotli 대비 11~35× 실측 우위. 송신 플래그 자동 부착 +
  문턱·제외 목록(T-11)·이득 판정, 수신 재조립→해제. 1GiB 폭탄 = 할당 0 거부.
  "압축이 실제로 실행됨" 테스트(레거시 무동작의 역)
- **Tls 인증서 운영 경로 ✅ (2026-08-06 4차)** — `IServerCertificateSource`(핸드셰이크별
  해석 — 회전이 재시작 없이 반영) + `FileCertificateSource`(PFX/PEM, Windows ephemeral
  함정 내장, mtime 폴링 + 명시 `Reload()`). 재적재 실패 = 기존 유지+경고(가용성),
  구세대 1세대 보관. Phase 9 Tls 항목 완결
- **시크릿 관리 ✅ (2026-08-06 5차)** — `ISecretSource`(Core) + Env/Directory 원천
  (12-factor·k8s 마운트, 캐시 없음 = 회전 즉시). **빈 값 = 부재**(빈 암호 조용한 진행
  금지). 가짜 메모리 보안 타입 없음(정직성). 레거시 하드코딩 자격증명 블로커 판정 완료
- 테스트 **576개** 통과(`dotnet test` 전 스위트 합산 — 집계 기준 오늘부터 이것),
  전체 게이트(-WarnAsError 클린 빌드·audit·AOT publish+실행) 통과

## 진행 중

- **Phase 9 잔여(게이트 조건 아님)** — 입력 검증·퍼징 확대 / `/security-review`(사용자 실행)
- **Phase 7·8 잔여(게이트 조건 아님)** — 누락 핸들러 검출, 리플렉션 폴백, 코어 제한 재측정
- **감사 보류분** — `MessageContext` 내부 메서드 public(ADR 후보), ConnectionId 세대 활용(세션 계층)
- **의도적 보류** — 인증·버전협상 실패의 클라이언트 와이어 통지(Phase 10 거부 통지
  체계와 함께) / varint×협상 조립 모순의 조립 시점 검증(Core 프레이밍 계약에 버전
  표면이 없어 불가 — v2 실존 시 계약 확장과 함께)

## 다음 (우선순위 순)

1. **입력 검증·퍼징 확대** — Phase 9 마지막 구현 항목. 페이로드 필드 범위 검사
   패턴 + 기존 프레이밍 퍼징의 확장 범위 판단부터. 설계 제시 → 승인 → 구현 순서
2. `/security-review` 실행(**사용자 명령**) + 결과 반영 → **Phase 9 마감**
3. 다음 Phase 후보: Phase 10 복원력(`IRateLimiter`·`IAdmissionControl`) 또는 Phase 7·8 잔여

## 블로커 / 열린 결정

- ~~레거시 하드코딩 자격증명~~ — **판정 완료(2026-08-06 5차)**: `ServerGlobals.cs:103` 의
  MongoDB 계정·암호는 커밋 시점에 유출 간주. **로컬 개발 외 어디서든 재사용됐다면
  폐기·교체할 것**(사용자 확인 필요). 레거시 트리는 참조 전용이라 코드 불변.
  새 코드 경로는 `ISecretSource` 가 참조 패턴
- MemoryPack `VersionTolerant` 주의 계약의 제너레이터 진단 승격 여부 — ADR-0013 부정 항목
- LoadRunner 램프업 무한 루프(죽은 서버 대상) — Phase 12 항목 추가됨
- 게이트 첫 실행 간헐 실패(직전 `dotnet test` 직후에만, 4회) — 테스트 호스트 잔존
  프로세스 잠금 추정. 재현 확정 시 eng/build.ps1 사전 정리 단계 검토

## 이번에 배운 것 (같은 실수 반복 방지)

- **"최선인가?"에는 방어가 아니라 공격으로 답한다** — 설계 재검토에서 중복 인자·
  수명 위반 경로·가드 포화 DoS·조립 순서 함정 4개를 스스로 찾았다. 전부 구현 후에야
  드러났을 것들이다
- **벤더 예외는 어댑터가 값으로 변환한다** — `PasswordHasher` 의 손상 해시
  `FormatException` 을 테스트가 잡았다. "오염 저장소 = 값 실패"(T-16)는 어댑터 몫
- **동결 계약의 의도적 중복은 교차 검증 테스트와 쌍으로** — Core/Framing 헤더
  레이아웃 일치를 테스트가 지킨다
- **여러 줄 치환 편집은 삭제를 만들 수 있다** — 체크박스 편집에서 연속된 다음 줄을
  빠뜨려 ROADMAP 항목 하나를 조용히 지웠다(발견·복원). 여러 줄 치환 후 주변 줄 보존 확인
- **PS 5.1 로 UTF-8 파일을 고치지 않는다** — `Set-Content` 치환이 한글 주석을 전부
  깨뜨렸다(ANSI 재해석). 파일 내용 수정은 Edit/Write 도구로만
- **LZ4 블록엔 무결성이 없다** — 내용 손상은 같은 길이로 "성공"한다. 무결성 주장은
  AEAD 계층에서만, 코덱에 가짜 무결성 장치를 만들지 않는다(레거시 가짜 체크섬의 역)
- **로드를 거친 인증서의 개인키는 재수출 불가일 수 있다**(Windows 키 저장소 정책) —
  테스트 파일 소재는 로드 전 원본 키에서 직접 뽑는다
- **PowerShell 5.1 + 여러 줄 커밋 메시지 = `git commit -F <파일>`** — 인수 안 `"` 가 깨진다
  (오늘 또 밟았다 — 예외 없이 -F 로)

## 작업 방식

- **코드는 사용자 지시 후에만 작성한다.** 먼저 대상·시그니처·근거를 제시하고 승인받는다.
  조사·분석·문서는 자율. 설계 결정 지점은 선택지로 물어본다
- 주석은 한글 4계층(8.2) / public 표면 변경 시 승인 파일 갱신(8.1) / 동시성은 9절 선행
- 커밋: 코드와 문서 분리, 스코프는 어셈블리 축 단위로 쪼갠다. 문서는 `/standup wrap` 에서 `chore(standup)`
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
  (ADR-0000·0001·0002·0004~0019 채택 / 0003 폐기 — 미결 ADR 없음)
- 위협 모델: `docs/THREAT-MODEL.md` (T-05·06·11~15·17~21 ✅ / 새 축·표면 추가 시 갱신)
- 진단 규칙: `docs/DIAGNOSTICS.md` (CHSM0xxx 가드 · CHSM1xxx 제너레이터)
- 성능 수치: `docs/BENCHMARKS.md` (ENV-A: 9900X 12/24 · ENV-B: 7945HX 16/32)
- 상세 이력: `docs/standup/history/` (최근: `2026-08-06.md`)
- 레거시 분석: `docs/legacy/00-overview.md`
