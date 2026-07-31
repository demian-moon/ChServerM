# 07 — 보안 / 압축 / 만료 KV

**전량 정독 완료** — `PublicLib/CompressAndEncryptM.cs`(273), `BasicLibM/AuthM/AuthM.cs`(36), `BasicLibM/HashM.cs`(130) — 총 439줄

핸드셰이크 **흐름**은 [문서 05](05-client.md#-인증-핸드셰이크-전체-흐름-양쪽-코드로-확정)에 있다. 여기서는 **암호 구현 자체**를 본다.

---

## 🔴 최대 발견 — 압축이 한 번도 실행되지 않는다

`CompressAndEncryptM.Compress:206`

```csharp
int maxLength = LZ4Codec.MaximumOutputSize(originDataLen);

// 만약 예상 최대 크기가 원본 크기보다 크다면 압축하지 않고 원본 반환
if (maxLength >= originDataLen)
{
    isCompress = false;
    compByte = originData;    // 원본 그대로
    return isCompress;
}
// ↓ 아래 LZ4 압축 코드에는 절대 도달하지 않는다
```

`LZ4Codec.MaximumOutputSize(n)`은 **최악의 경우 출력 크기**를 반환한다 — 정의상 **항상 `n`보다 크다**(대략 `n + n/255 + 16`). 따라서 `maxLength >= originDataLen`은 **항상 참**이고, 함수는 **언제나 원본을 그대로 반환하며 `isCompress = false`** 다.

결과:
- **LZ4 압축 경로는 죽은 코드다.** `K4os.Compression.LZ4` 의존은 사용되지 않는다
- `FbsEncryptHeadM.isCompress`는 언제나 0
- `Decompress`는 **한 번도 호출되지 않는다**
- 이전 문서들에서 `.fbs` 주석을 근거로 기술한 *"1024바이트 미만 무압축, LZ4 압축"* 정책은 **코드에 존재하지 않는다.** 크기 임계값 자체가 없다

의도했던 조건은 `maxLength >= originDataLen`이 아니라 **압축 후 실제 크기**와의 비교(`compressedLength >= originDataLen`)였을 것이다.

---

## 🔴 두 번째 발견 — `IHasTimeEventsM` 메커니즘이 전면 무효

`AbTimeEventBaseM.TerminateJob`(문서 04)은 종료 시 `Owner?.TimeEvents.TryRemove(_idJob, out _)`로 소유자의 컬렉션에서 자신을 제거한다. 그런데 **`TimeEvents`에 작업을 추가하는 코드가 코드베이스 어디에도 없다.**

| 구현체 | `TimeEvents` 상태 |
|---|---|
| `HashM` (`HashM.cs:30`) | `ConcurrentDictionary` 필드는 있으나 **아무도 추가하지 않는다** → 항상 빈 상태 |
| `ScriptDelaysM` (문서 04) | `=> new()` 오타로 **접근할 때마다 새 딕셔너리** → 항상 빈 상태 |

`TimeEventSchedulerM.AddJob`은 전역 `_allJobs`에만 추가한다. 소유자 쪽 등록은 **설계만 있고 구현이 없다.**

### 파급 효과

`HashM.Set`과 `HashM.Remove`가 모두 `TimeEvents.ContainsKey(key)`로 분기한다 — **이 조건이 항상 거짓**이므로:

1. **같은 키를 다시 `Set`해도 기존 만료 타이머가 취소되지 않는다.** 이전 타이머가 나중에 발화해 **새로 설정한 값을 지운다**
2. **`Remove`한 키의 타이머도 취소되지 않는다.** 스케줄러에 좀비 작업이 남아 만료 시각까지 유지된다

> **역설적으로 이 결함이 더 심각한 버그를 가리고 있다.** 만약 `TimeEvents`가 정상 동작했다면 `Set`의 흐름은:
> `_hash[key] = value` → `CancelJob(key)` → `job.Cancel()` → `OnTerminate` → **`_hashM.Remove(key)`** → 방금 설정한 값이 삭제됨.
> 즉 **만료와 취소가 같은 코드 경로**(문서 04 발견)라는 설계 결함 때문에, `TimeEvents`를 고치면 "값 갱신이 값을 지우는" 버그가 드러난다. **두 결함을 함께 고쳐야 한다.**

---

## `CompressAndEncryptM`

`PublicLib/CompressAndEncryptM.cs:12`, `class : IDisposable`

### 동작

방향별로 다른 알고리즘을 쓰는 대칭 암호 컨테이너.

| 멤버 | 내용 |
|---|---|
| `encType` / `decType` | `ENCRYPT_TYPE { NONE, XOR, AES }` |
| `aes` | `Aes` 인스턴스 (키·IV 보유) |
| `xorKey` | `byte[32]`, `RandomNumberGenerator`로 생성 |
| `RSAPrivateKeyMadeByClient/Server` | **public string 프로퍼티** (XML 형식 개인키) |
| `RSAPublicKeyMadeByClient/Server` | public string 프로퍼티 |

`CreateEncDecType(enc, dec)` — `enc == AES`면 AES 키·IV 생성, `enc == XOR`면 XOR 키 생성
서버: `CreateEncDecType(XOR, AES)` / 클라: `CreateEncDecType(AES, XOR)`

### 🔴 암호 구현 결함

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **AES-256이 아니라 AES-128이다.** `aes.KeySize = 128` 후 `GenerateKey()`. 이전 문서에서 `.fbs` 주석을 근거로 "AES256"이라 기술한 것은 **오류다** | `:81~82` | 🔴 높음 |
| 2 | **세션 전체에서 IV가 고정된다.** `GenerateIV()`를 키 생성 시 한 번만 호출하고, 이후 모든 `CreateEncryptor()`가 같은 `aes.IV`를 쓴다. **CBC 모드에서 IV 재사용은 동일 평문 접두사가 동일 암호문 접두사를 만든다** — 패킷 헤더가 고정 구조이므로 즉시 관찰 가능 | `:83`, `:178` | 🔴 치명 |
| 3 | **인증 없는 CBC + PKCS7.** MAC이 없어 **패딩 오라클 공격**에 취약하고, 위변조를 탐지할 수 없다(체크섬도 가짜 — 문서 02). AEAD(AES-GCM/ChaCha20-Poly1305)여야 한다 | `:80` | 🔴 치명 |
| 4 | **XOR은 암호화가 아니다.** 32바이트 반복 키. 패킷 헤더가 고정이므로 **알려진 평문 32바이트로 키 전체가 복원**된다. 서버→클라 전 트래픽이 사실상 평문 | `:144~157` | 🔴 치명 |
| 5 | **RSA 개인키가 `public string` 프로퍼티**로 노출된다. XML 문자열이라 메모리에 평문으로 상주하고 GC가 언제 지울지 모른다 | `:27~28` | 🔴 높음 |
| 6 | **`Decompress`가 와이어에서 온 `originalLength`로 배열을 할당한다.** `new byte[originalLength]` — **공격자가 제어하는 할당 크기** → 메모리 고갈 벡터. 상한 검증 없음 | `:240~245` | 🔴 높음 |
| 7 | `Encrypt`/`Decrypt`가 `encType`이 `NONE`이면 **`null`을 반환**한다. 호출자(`PacketM`, `PkObjM`)는 null 검사를 하지 않는다 → NRE | `:98~125` | 🟠 중간 |
| 8 | 매 호출 `CreateEncryptor()`/`CreateDecryptor()` — 변환기 객체를 매번 생성·폐기 | `:178`, `:192` | 🟠 중간 |
| 9 | `Compress`의 풀 소유권이 `out bool isCompress`에 실려 있다 — 호출자가 판단해서 `ReturnPoolAfterCompress`를 불러야 한다. **타입으로 표현되지 않은 소유권** | `:206~237` | 🟠 중간 |
| 10 | 파이널라이저 `~CompressAndEncryptM()` | `:268` | 🟠 중간 |
| 11 | `SetXorKey`/`SetAesKey`가 public — 외부에서 키를 임의 교체 가능 | `:128~140` | 🟡 낮음 |

### 개선점 (ChServerM Phase 9)

- **1순위: TLS(`SslStream`)로 전송 보안을 위임한다.** 자체 암호 프로토콜을 만들 이유가 없다. 서버 인증서로 MITM이 차단되고, 검증된 구현을 쓴다
- 자체 프로토콜이 꼭 필요하면:
  - **인증된 키 교환** — 서버 인증서 + ECDHE. 평문 공개키 교환 금지
  - **양방향 AEAD** — AES-GCM 또는 ChaCha20-Poly1305. 방향별 알고리즘 차등 금지
  - **메시지마다 고유 nonce** — 카운터 기반. IV 재사용 금지
  - 무결성은 **AEAD 태그**로. 별도 체크섬 불필요
- **압축은 실제로 동작하게 만들고 임계값을 둔다.** 그리고 **압축 → 암호화 순서를 고정**한다(역순은 CRIME류 취약점)
- `originalLength`에 **상한 검증** (Phase 4 최대 프레임 크기와 연동)
- 키 자료는 `Span<byte>` + 사용 후 `CryptographicOperations.ZeroMemory`

### 판정

🔴 **폐기**. 승계할 암호 구현이 없다. LZ4 압축 **의도**만 🔵 참고.
→ Phase 9

---

## `AuthM` — 🟢 유일하게 올바른 보안 컴포넌트

`BasicLibM/AuthM/AuthM.cs:10`, `internal static class`

```csharp
static public bool   IsPassed(string rawPw, string hashedPw)
static public string GetHashPassword(string rawPw)
```

`Microsoft.AspNetCore.Identity.PasswordHasher<object>`를 사용한다.

> **이것은 올바른 선택이다.** ASP.NET Core Identity의 기본 해셔는 **PBKDF2-HMAC-SHA256, 비밀번호별 랜덤 솔트, 반복 횟수 내장, 버전 태그 포함** 형식이다. 직접 만든 해시(`MD5`, 솔트 없는 `SHA256` 등)를 쓰지 않았다는 점에서 레거시 보안 코드 중 **유일하게 제대로 된 부분**이다.

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **호출부가 결과를 무시한다.** `DoPkLogin`이 `WRONG_PW`일 때 `return`을 주석 처리했다(문서 01) → **올바른 해싱이 완전히 무의미해진다** | 🔴 치명 |
| 2 | `new PasswordHasher<object>()`를 **호출마다 생성** — 옵션 객체 할당 반복 | 🟠 중간 |
| 3 | `internal` — 어셈블리 외부에서 쓸 수 없다 | 🟡 낮음 |
| 4 | 해시 파라미터(반복 횟수 등)를 설정하지 않아 라이브러리 기본값에 의존. 명시적 정책이 없다 | 🟡 낮음 |

### 판정

🟢 **승계** (선택). ChServerM `IAuthenticator`의 기본 구현으로 **같은 라이브러리를 쓴다**. 단:
- 해셔를 **싱글턴으로 주입**
- 반복 횟수·알고리즘을 **옵션으로 명시**하고 문서화
- **검증 실패는 반드시 연결을 끊는다** (Phase 9)

→ Phase 9

---

## `HashM` / `ExpireHashEventM` — 만료 지원 KV 저장소

`BasicLibM/HashM.cs:27`, `class HashM : IHasTimeEventsM`

### 동작

**해시 함수가 아니라 Redis `HSET` + `EXPIRE`에 대응하는 오브젝트별 상태 저장소다** (문서 03에서 정정한 내용).

```csharp
protected ConcurrentDictionary<string, string> _hash;
ConcurrentDictionary<string, AbTimeEventBaseM> _timeEvents;
TimeEventSchedulerM _expireJobScheduler;

bool Set(string key, string value, int durationSec = -1)   // -1이면 무기한
bool Remove(string key)
bool Has(string key)
bool Get(string key, out string value)
bool GetAndRemove(string key, out string value)            // 원자적이지 않음
```

`BaseGameObjM` / `MapObjM`(문서 03)이 **지연 생성**해 보유한다. 게임 오브젝트마다 자유 형식 상태를 붙일 수 있는 확장 포인트다.

> **발상은 좋다.** 버프·쿨다운·임시 플래그처럼 "일정 시간 후 사라지는 값"은 게임 서버에 흔하다. 컴포넌트를 매번 정의하지 않고 문자열 KV로 처리하는 탈출구를 제공한다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **작업 ID가 전역 네임스페이스와 충돌한다.** `AddJob`에 `key`(예: `"buff_speed"`)를 그대로 작업 ID로 넘기는데, `TimeEventSchedulerM._allJobs`는 **프로세스 전역 딕셔너리**다. 서로 다른 오브젝트가 같은 키를 쓰면 **두 번째부터 `TryAdd` 실패 → 만료가 조용히 동작하지 않는다** (`Debug.WriteLine`만 남는다) | `:77~78` | 🔴 치명 |
| 2 | 🔴 **`TimeEvents`에 아무도 추가하지 않는다** → `Set`/`Remove`의 타이머 취소 분기가 **영원히 실행되지 않는다**. 결과: 같은 키를 다시 Set하면 **이전 타이머가 살아남아 새 값을 지운다** | `:67`, `:90`, 전역 | 🔴 치명 |
| 3 | 🔴 **`TimeEvents`를 고치면 더 큰 버그가 드러난다.** 취소 경로가 `OnTerminate` → `_hashM.Remove(key)`를 타므로, **값을 갱신하려던 `Set`이 값을 삭제한다.** 만료와 취소가 같은 경로라는 설계 결함(문서 04) 때문 | `:64~69`, `:20~23` | 🔴 치명 |
| 4 | `durationSec * 1000`이 `int` 연산 — 약 24일(2,147,483초)을 넘으면 **오버플로** | `:77` | 🟠 중간 |
| 5 | `GetAndRemove`가 `TryGetValue` → `TryRemove` 2단계 — **원자적이지 않다.** 이름이 원자성을 암시하는데 경쟁 시 두 호출자가 같은 값을 받을 수 있다 | `:119~127` | 🟠 중간 |
| 6 | `GetAndRemove`가 만료 타이머를 취소하지 않는다 | `:119` | 🟠 중간 |
| 7 | 값이 `string` 고정 — 숫자·구조체를 넣으려면 문자열 변환 왕복 | `:29` | 🟡 낮음 |
| 8 | `durationSec == 0`은 실패, `-1`은 무기한 — 센티넬이 직관적이지 않다. `TimeSpan?`이면 명확 | `:57~61` | 🟡 낮음 |

### 문서 04 서술 정정

문서 04에서 `ExpireEventConCurSchedulerM.cs:13`의 주석(*"HashM자체가 동시성 지원하지 않음"*)을 근거로 **"`HashM`이 스레드 안전하지 않다"**고 적었다. **정확하지 않다.**

- 현재 `HashM`은 **`ConcurrentDictionary`를 쓰므로 개별 연산은 스레드 안전**하다
- 그 주석이 가리키는 것은 **다른(더 오래된) 만료 경로**인 `ExpireJobForDicRemoveM`로, 이쪽은 `PooledDictionary<string,string>`(비동시성)을 대상으로 한다
- 즉 **해시 만료 메커니즘이 두 벌 공존**하며, 오래된 쪽만 스레드 안전하지 않다

다만 `Set`의 "값 갱신 + 타이머 취소 + 타이머 재등록"은 **복합 연산이라 여전히 원자적이지 않다.**

### 개선점

- **오브젝트별 만료 KV는 승계할 가치가 있다.** `ISessionStore`와 별개로 "엔티티 상태 백" 프리미티브로 (Phase 13)
- **작업 ID를 소유자 범위로 한정한다** — `(OwnerId, Key)` 복합 키 또는 강타입 `JobId`(Phase 1). 전역 문자열 네임스페이스 금지
- **만료와 취소를 분리한다**(Phase 8) — 그래야 `Set` 갱신이 값을 지우지 않는다
- 값 타입을 제네릭으로, 기간은 `TimeSpan?`으로
- `GetAndRemove`는 `ConcurrentDictionary.TryRemove(key, out value)`로 **진짜 원자적으로**

### 판정

🟡 **개작** — 개념 승계, 구현 재작성. → Phase 1 (JobId), Phase 8 (만료/취소 분리), Phase 13 (상태 백)

---

## 이 계층의 종합

| 항목 | 판정 | Phase |
|---|---|---|
| **`PasswordHasher` 채택** (PBKDF2 + 솔트) | 🟢 승계 | 9 |
| **오브젝트별 만료 KV 개념** | 🟢 승계 | 13 |
| LZ4 압축 (의도만) | 🔵 참고 | 9 |
| `HashM` 구현 | 🟡 개작 | 1·8·13 |
| AES 구현 (CBC, 고정 IV, 인증 없음) | 🔴 폐기 | 9 |
| XOR "암호화" | 🔴 폐기 | 9 |
| RSA 키 교환 (인증 없음, PKCS#1 v1.5) | 🔴 폐기 | 9 |
| `Compress` (죽은 코드) | 🔴 폐기 | 9 |

### 새 코드에 절대 옮기면 안 되는 것

1. `CompressAndEncryptM.cs:214` — **`maxLength >= originDataLen`은 항상 참** → 압축이 절대 실행되지 않음
2. `CompressAndEncryptM.cs:83`,`:178` — **세션 전체 IV 고정** (CBC에서 치명적)
3. `CompressAndEncryptM.cs:80` — **인증 없는 CBC+PKCS7** (패딩 오라클)
4. `CompressAndEncryptM.cs:144~157` — **반복 키 XOR을 암호화로 사용**
5. `CompressAndEncryptM.cs:27~28` — **RSA 개인키를 public string 프로퍼티로**
6. `CompressAndEncryptM.cs:240~245` — **와이어 값으로 배열 할당** (메모리 고갈)
7. `HashM.cs:77~78` — **오브젝트 스코프 키를 전역 작업 ID로 사용** → 충돌 시 만료 무동작
8. `HashM.cs:67`,`:90` — **`TimeEvents`가 비어 있어 타이머 취소 분기가 죽어 있음**
9. `AuthM` 자체는 옳으나 **호출부가 결과를 무시** (`ServerM.cs:881`)

### 보안 종합 판정

레거시의 보안 계층은 **`AuthM`의 비밀번호 해싱을 제외하면 전량 재설계 대상**이다. 그리고 그 `AuthM`조차 **호출부가 검증 결과를 버리기 때문에 실효가 없다.**

Phase 9의 위협 모델 문서(`docs/THREAT-MODEL.md`)는 이 목록을 출발점으로 쓴다 — **각 항목이 어떤 위협에 대응하는지, 새 설계가 그것을 어떻게 막는지**를 매핑한다.
