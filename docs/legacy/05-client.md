# 05 — 레거시 클라이언트 (`LagacyClient/`)

**대상**: `ClientM.cs`(749), `IoPipelineClaM.cs`(408), `ClientTimeM.cs`(66), `TimerClaM.cs`(59), `IniOptionClaM.cs`(16) — **전량 정독 완료**

`LegacyServer/`와 동일하게 **로컬 참조 전용**으로 둔다(`.gitignore`에 `/LagacyClient/`, 상속 차단 스토퍼 배치).

---

## 🔴 구조적 발견 — 서버·클라이언트가 소스를 공유한다

`ClientM.csproj`는 서버 프로젝트를 **참조**하지 않는다. 대신 **소스 파일을 링크**해 자기 어셈블리에 함께 컴파일한다.

```xml
<Compile Include="..\ServerM\PublicLib\PacketM.cs" />
<Compile Include="..\ServerM\PublicLib\UserM.cs" />
... 총 36개
```

### 공유되는 36개 파일 (= "서버·클라 공용"의 정확한 정의)

| 그룹 | 파일 |
|---|---|
| `FbsClassM/` (11) | `FbsPkHeadM`, `FbsContentHeadM`, `FbsEncryptHeadM`, `FbsEncryptKey`, `FbsLogInIdPw`, `FbsLoginOk`, `FbsLoginFin`, `FbsServerTick`, `FbsStrArray`, `FbsMetaData`, `FbsProgressBar` |
| `PublicLib/` (18) | `PacketM`, `MemPacketM`, `PkObjM`, `SendPacketM`, `UserM`, `MembersM`, `NetWorkM`, `AllowedPacketM`, `CompressAndEncryptM`, `ConcurSeqTaskExecM`, `CommonInterfaceM`, `GlobalM`, `IniOptionM`, `AbTableBaseM`, `SrvClaFuncM`, `Logger/LogM` |
| `PublicLib/FileM/` (5) | `FileM`, `InIFileM`, `MetaDataM`, `LoadableDataInStructM`, `StringAnalyzerM` |
| `PublicUtil/` (3) | `TickTimeM`, `TimerM`, `StatisticsM` |
| `BasicLibM/` (1) | `Memory/UnsafeCopyBlock` |

### 이 사실이 설명해 주는 것들

앞선 문서에서 "왜 이렇게 되어 있지?"로 남겨둔 의문들이 여기서 풀린다.

| 앞선 관찰 | 설명 |
|---|---|
| `PublicLib/`가 왜 별도 폴더인가 (문서 01·02) | **클라이언트와 공유되는 소스 집합**이기 때문 |
| `SendPacketM`(비샤딩 + UI ActionBlock)이 왜 존재하나 (문서 02) | **클라이언트의 송신 경로**다. 서버는 `SendPacketGroupM`(샤딩)을 쓴다 |
| `IUIThreadCheck` / UI 스레드 디스패치가 왜 서버 코드에 있나 (문서 01·02·04) | 공유 소스라서 **클라이언트의 UI 요구가 서버에 흘러들었다** |
| `AbNetworkBase`의 static 상태가 왜 문제가 안 됐나 (문서 01) | 서버와 클라가 **별도 프로세스**라 실제로 충돌하지 않았다 |
| `AllowedPacketMan`이 왜 공용인가 (문서 02) | 클라이언트도 수신 패킷을 검증한다 |
| 코드에 `.NET Framework 4.8` 제약이 왜 보이나 | **클라이언트가 v4.8 타깃**이라 공유 소스가 v4.8 호환이어야 했다 (`Random.Shared` 등 최신 API 사용 불가) |

### 타깃 프레임워크 불일치

| | 타깃 |
|---|---|
| `ServerM.csproj` | `.net9` (표기 오류 — `net9.0`이어야 함) |
| `ClientM.csproj` | **`v4.8`** (.NET Framework) |

**공유 소스가 두 런타임에서 컴파일되어야 한다.** 이것이 레거시가 최신 API를 쓰지 못한 근본 제약이다.

> **ChServerM 시사점**: `ClientBuilder`(Phase 2)를 설계할 때 **소스 링크 방식을 답습하지 않는다.** Core 추상화를 담은 어셈블리를 서버·클라가 **패키지로 참조**하고, 클라 전용(UI 동기화 등)은 `ChServerM.Client.*`에만 둔다. 그러면 클라이언트의 요구가 서버 코드에 새어들지 않는다 — 이번에 확인한 오염 경로가 구조적으로 차단된다.

`#if UNITY_EDITOR` 분기(`ClientM.cs:117`)와 `BuildEventCopyClientLibM.bat`으로 보아 **Unity 클라이언트**가 최종 소비자다.

---

## 🔴 인증 핸드셰이크 전체 흐름 (양쪽 코드로 확정)

문서 01에서 서버 측만 보고 기술했던 것을 클라이언트 측과 맞춰 **정확한 순서**로 확정한다.

```
클라                                                 서버
─────────────────────────────────────────────────────────────────
LoginFunc(id, pw)
  RSA 2048 키쌍 생성
  _privateKeyMadeByClient 보관
  ── PSC_RSA (클라 공개키, 평문) ──────────────────▶
                                          DoPkRSAForSever
                                            RSA 2048 키쌍 생성
                                            CompressAndEncryptM 등록
  ◀───────────── PSC_RSA (서버 공개키, 평문) ──────
DoPkRSA
  compEnc 생성(클라 개인키 + 서버 공개키)
  _LoginFuncStep1
    CreateEncDecType(Enc=AES, Dec=XOR)
    AES key/iv를 서버 공개키로 RSA 암호화
  ── PSC_COMP_ENC_CHANGE (AES key/iv) ────────────▶
                                DoPkCompressAndEncryptForServer
                                  RSA 개인키로 AES key/iv 복호
                                  CreateEncDecType(Enc=XOR, Dec=AES)
                                  XOR 키를 클라 공개키로 RSA 암호화
  ◀──────── PSC_COMP_ENC_CHANGE (XOR 키) ─────────
DoPkCompressAndEncrypt
  RSA 개인키로 XOR 키 복호 → SetXorKey
  _LoginFuncStep2
    SerializeLoginIdPw(id, pw, version)
  ── PS_LOGIN (AES 암호화) ───────────────────────▶
                                          DoPkLogin
                                            (비밀번호 검증 우회 — 문서 01 참조)
  ◀──────── PC_LOGIN_OK (XOR 암호화) ─────────────
DoPkLoginOk
  gServerFrequency = loginOk.ServerFrequency
  gClientTickWeight = gServerFrequency / Stopwatch.Frequency
  InnerUserM 생성 → AddUser
  ── PS_LOGIN_FIN ───────────────────────────────▶
  AppStart(user)
```

**이후 방향별 알고리즘**: 클라→서버 = **AES**, 서버→클라 = **XOR**

### 보안 판정

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **키 교환이 인증되지 않는다.** 양쪽 공개키가 평문으로 오간다 → MITM이 자기 키를 끼워넣으면 **세션 전체를 복호·변조**할 수 있다. 인증서·핀닝·사전 공유 키 어느 것도 없다 | 🔴 치명 |
| 2 | **서버→클라가 XOR.** 패킷 헤더 구조가 고정이므로 **알려진 평문 공격**으로 키가 즉시 복원된다 | 🔴 치명 |
| 3 | `RSAEncryptionPadding.Pkcs1` — Bleichenbacher 패딩 오라클 취약. OAEP를 써야 한다 | 🔴 높음 |
| 4 | 커넥션마다 **양쪽이 RSA 2048 키쌍을 생성**한다 → 연결 폭주 시 CPU 고갈 (DoS) | 🔴 높음 |
| 5 | **클라이언트가 비밀번호를 `public string _pw` 필드에 보관**하고 지우지 않는다. 프로세스 수명 내내 평문으로 메모리에 남는다 | 🔴 높음 |
| 6 | **RSA 개인키를 `public string _privateKeyMadeByClient`에 XML 문자열로 보관** — 공개 필드, 수명 내내 유지 | 🔴 높음 |

### 문서 02 서술 정정

문서 02에서 `PacketM.SerializeLoginIdPw`를 두고 *"비밀번호를 페이로드에 평문 직렬화"*라고 적었다. **정확하지 않다.**

- 정확히는: 비밀번호는 페이로드 바이트로 평문 직렬화되지만, **패킷 전체가 AES로 암호화된 뒤 전송**된다(`_LoginFuncStep2`). 수동적 도청자에게는 평문으로 노출되지 않는다.
- 그러나 **AES 키가 인증되지 않은 RSA 교환으로 합의되므로, MITM은 키를 대체해 비밀번호를 복원할 수 있다.** 결론(자격증명이 실질적으로 노출된다)은 같지만 **메커니즘이 다르고, 그 차이가 대책을 바꾼다** — 필요한 것은 "평문 전송 금지"가 아니라 **인증된 키 교환**이다.

→ Phase 9는 TLS(서버 인증서)를 1순위로 하고, 자체 프로토콜이 필요하면 **인증서 기반 ECDHE + 양방향 AEAD**로 간다.

---

## `ClientM`

`ClientM.cs:24`, `abstract class ClientM : AbNetworkBase`

### 동작

서버 `ServerM`과 **대칭 구조**다. Template Method로 앱이 상속해 구현한다.

**강제 구현(abstract)**: `AddMemPkDispatcher`, `GetScreenResolution()`, `UpdateClient()`
**선택 훅(virtual)**: `AddAllowedPacketMan`, `GetFirstUserPacketState`, `StartAfterLogin`, `FinishUser`

**기본 등록 핸들러**: `DoPkHeartBit`, `DoPkDisconnectRequest`, `DoPkServerTick`, `DoPkVersionCheckResult`, `DoPkRSA`, `DoPkCompressAndEncrypt`, `DoPkLoginOk`

**`ClientConnect(CLIENT_CONNECT_MODE)`** (`:312`)
INI 로딩 → 화이트리스트 생성 → 디스패처 생성·`LoadActions` → `_Connect()` → `VERSION_CHECK` 모드면 `PS_VERSION_CHECK` 전송

**`ServerTickCurrent`** (`:47`) — 🟢 **서버 시각 외삽**
```csharp
long elapsedClientTick = Stopwatch.GetTimestamp() - _clientTickWhenLastUpdateServerTick;
return _lastUpdateServerTick + (long)(elapsedClientTick * ClientTimeM.gClientTickWeight);
```
마지막으로 받은 서버 틱에, 그 이후 경과한 클라 틱을 **주파수 비율로 환산**해 더한다. 서버 틱 패킷 사이의 시각을 부드럽게 메운다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **클라 기본 패킷 상태가 `A_SC_ANY_STATE`** — 모든 패킷을 무조건 허용한다. 문서 02에서 지적한 "전체 허용 상태"의 출처가 여기다. 클라 편의 기능이 **공유 소스를 통해 서버에도 존재**하게 됐다 | `:175~178` | 🔴 높음 |
| 2 | **`if (Tc != null || Tc.Connected)`** — `||`가 아니라 `&&`여야 한다. `Tc`가 null이면 두 번째 항에서 `NullReferenceException` | `:369` | 🔴 버그 |
| 3 | **비Windows에서 동작 불가.** `LoadIniFile`이 Windows에서만 INI를 읽는데, 그 뒤 `IniOptionM.gIpAddress`를 무조건 사용한다 → 비Windows에서 `IPAddress.Parse(null)` 예외 | `:294~304` | 🔴 높음 |
| 4 | `IoPipelineClaM.PipelineForClientAsync(...).ConfigureAwait(false);` — **await하지 않는다.** await 없는 `ConfigureAwait`는 아무 효과가 없다. 예외가 유실된다 (주석으로 의도는 설명돼 있음) | `:374` | 🟠 중간 |
| 5 | `_id`, `_pw`, `_privateKeyMadeByClient`가 **public 필드** | `:439~441` | 🔴 높음 |
| 6 | `VersionCheckResult` 메서드(`:210`)와 `DoPkVersionCheckResult` 클래스(`:630`)가 **동일 로직 중복**. 메서드는 죽은 코드 | | 🟡 낮음 |
| 7 | `GetUser`가 호출마다 `new UserM(user)` 할당 (서버 `SrvGlobal.GetUser`와 같은 문제) | `:68~77` | 🟠 중간 |
| 8 | 서버와 동일하게 **주석 처리된 원본 로직이 대량 잔존** (`:460~472`, `:502~521`, `:536~569`) | | 🟡 낮음 |
| 9 | `DoPkHeartBit`가 하트비트마다 `Debug.WriteLine` — 로그 스팸 | `:597` | 🟡 낮음 |
| 10 | static 유저 딕셔너리(`_dicUser`) — 한 프로세스에서 다중 클라 연결 불가 | `:67` | 🟠 중간 |

### 판정

🟡 **개작**. Template Method → `ClientBuilder` 조립으로. 서버와의 대칭 구조는 유지한다(ChServerM도 `ServerBuilder`/`ClientBuilder` 대칭이 Phase 2 항목).

→ Phase 2 (`ClientBuilder`), Phase 9 (보안)

---

## `ClientTimeM` — 🟢 시각 동기화 유틸

`ClientTimeM.cs:8`, `ClientTimeM : TickTimeM`

| 멤버 | 내용 |
|---|---|
| `gServerFrequency` | `PC_LOGIN_OK`로 받은 **서버의 `Stopwatch.Frequency`** |
| `gClientTickWeight` | `gServerFrequency / Stopwatch.Frequency` — 클라 틱 → 서버 틱 환산 계수 |
| `MsToServerTick(ms)` | `ms * gServerFrequency / 1000` |
| `ServerTickToMs(tick)` | `tick * 1000 / gServerFrequency` |
| `ServerTickToSec(tick)` | `tick / gServerFrequency` |

> **`Stopwatch.Frequency`는 머신마다 다르다.** 서버가 로그인 응답에 자기 주파수를 실어 보내고, 클라가 비율을 계산해 모든 시간 계산을 서버 단위로 정규화한다. **문제 인식과 해법이 정확하다.**

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | `gServerFrequency`가 0이면 `MsToServerTick`이 0을 반환하고 `ServerTickToMs`는 **0으로 나누기**. 호출부(`DoPkLoginOk:724`)에서 `<= 0`이면 1로 보정하지만 **로그인 전에는 0** | 🟠 중간 |
| 2 | `ServerTickToSec`이 음수 검증에 `Debug.Assert` — Release에서 소멸 | 🟠 중간 |
| 3 | static 전역 — 다중 연결 불가 | 🟠 중간 |

### 개선점

- **주파수 정규화 개념은 승계.** 단 ChServerM은 시간 단위를 **처음부터 `Stopwatch.Frequency`가 아닌 고정 단위**(마이크로초 정수 등)로 정의하면 이 환산 자체가 불필요해진다 (Phase 17)
- `IClock` 추상화로 주입 (Phase 1)

### 판정

🟢 **승계** (개념) / 🟡 **개작** (구현) → Phase 1·17

---

## `IoPipelineClaM`

`IoPipelineClaM.cs:80`

**`IoPipelineSrvM`(416줄)과 거의 완전한 중복**(408줄)이다. 동일한 5단 상태 머신(`PK_HEAD → CONTENT_HEAD → CONTENT_DATA`, `ENC_PK_HEAD → ENC_PK_DATA`), 동일한 `FillPipe`/`ReadPipe` 분리, 동일한 `AbDisconnectProcess` 파생(`ClientDisconnectProcess`).

**따라서 문서 01의 `IoPipelineSrvM` 분석이 그대로 적용된다** — `ToArray()` 할당, `ArrayPool` 누수, 프레이밍 desync, 최대 프레임 크기 상한 부재, 체크섬 예외 등.

### 추가 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **약 400줄이 서버판과 중복.** 차이는 `SendMemPk` 대상(`ClientM` vs `ServerM`)과 종료 처리뿐이다. 프레이밍 로직이 두 벌 존재해 한쪽만 고치면 프로토콜이 어긋난다 | 🔴 높음 |
| 2 | `AbMemPkFactory` 추상 클래스가 여기도 주석 처리된 채 남아 있음(`:18~45`) — 서버판과 동일 | 🟡 낮음 |

### 개선점

**프레이밍은 서버·클라가 반드시 같은 코드여야 한다.** ChServerM은 `IFrameDecoder`/`IFrameEncoder`를 **Core에 한 벌만** 두고 양쪽이 그것을 쓴다. 방향별 차이(누구에게 디스패치하는가)는 콜백/제네릭으로 주입한다.

→ Phase 4

### 판정

🔴 **폐기** (중복). 문서 01의 개작 결과를 양쪽이 공유한다.

---

## `TimerM_ClaUser_Delay_Disconnect` / `IniClntOptionM`

**`TimerM_ClaUser_Delay_Disconnect`** (`TimerClaM.cs:10`) — 서버의 `TimerM_SrvUser_Delay_Disconnect`와 **동일 패턴**(지수 백오프 재시도 10회, 주석은 "4번"). 로그인 OK 처리 전에 서버가 FIN을 보낸 경우의 후처리.
→ 문서 01의 분석이 그대로 적용된다. 🟢 **승계**(문제 인식) / 🟡 **개작**(상태 기계로)

**`IniClntOptionM`** (`IniOptionClaM.cs:9`) — `OptionClientM.ini` 파일명만 지정. 서버의 `IniSrvOptionM`과 같이 **실질 내용 없음**.
→ 🔴 **폐기**. Options 패턴으로 대체 (Phase 2)

---

## 이 계층의 종합 판정

| 항목 | 판정 | Phase |
|---|---|---|
| **서버 주파수 전달 → 클라 틱 정규화** | 🟢 승계 | 17 |
| **서버 시각 외삽(`ServerTickCurrent`)** | 🟢 승계 | 17 |
| **서버틱 즉시 응답으로 RTT 측정** | 🟢 승계 | 17 |
| **서버·클라 대칭 Builder 구조** | 🟢 승계 | 2 |
| `ClientM` Template Method | 🟡 개작 | 2 |
| 지연 disconnect 재시도 | 🟡 개작 | 5 |
| **소스 파일 링크 공유 모델** | 🔴 폐기 | 2 |
| `IoPipelineClaM` (중복 프레이밍) | 🔴 폐기 | 4 |
| RSA/AES/XOR 핸드셰이크 | 🔴 폐기 | 9 |
| `IniClntOptionM` | 🔴 폐기 | 2 |

### 새 코드에 절대 옮기면 안 되는 것

1. `ClientM.cs:175~178` — **클라 기본 상태가 `A_SC_ANY_STATE`** (전체 허용). 공유 소스를 통해 서버까지 오염
2. `ClientM.cs:369` — **`if (Tc != null || Tc.Connected)`** (`&&`여야 함)
3. `ClientM.cs:294~304` — **비Windows에서 INI 미로딩 후 무조건 사용** → 예외
4. `ClientM.cs:439~441` — **비밀번호·RSA 개인키를 public 필드에 프로세스 수명 내내 보관**
5. `ClientM.cs:448`, `:486`, `:688` — **커넥션마다 RSA 2048 키쌍 생성 + PKCS#1 v1.5 패딩**
6. **소스 파일 링크로 서버·클라 코드 공유** — 클라이언트 요구(UI 스레드, .NET Framework 4.8)가 서버로 역류하는 경로

### ChServerM 설계 반영

이번 분석으로 **Phase 2 `ClientBuilder` 항목의 요구가 구체화됐다.**

- 서버·클라 공용 계약은 **`ChServerM.Core` 어셈블리 하나**로. 소스 링크 금지
- 클라 전용 관심사(UI 동기화 컨텍스트, 재접속, 시각 외삽)는 **`ChServerM.Client.*`에만**
- **프레이밍 코드는 단 한 벌** — 서버·클라가 같은 `IFrameDecoder` 구현을 쓴다
- 클라 SDK의 타깃 프레임워크가 서버를 제약하지 않도록 **패키지 경계로 분리**
