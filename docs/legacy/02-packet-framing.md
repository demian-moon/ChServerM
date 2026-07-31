# 02 — 패킷 / 프레이밍 / 직렬화

**대상**: `PublicLib/PacketM.cs`(823), `PublicLib/AllowedPacketM.cs`(426), `PublicLib/PkObjM.cs`(333), `PublicLib/MemPacketM.cs`(295), `PublicLib/SendPacketM.cs`(66), `BasicLibM/SerializeM.cs`(76), `FbsClassM/`(9 파일)

**전량 정독 완료** (`FbsClassM/`은 `flatc` 생성 코드로 `PacketM.fbs` 스키마와 1:1 대응이므로 개별 분석 불필요).

---

## 🔴 이 계층의 3대 발견

### 1. 체크섬 검증이 존재하지 않는다

```csharp
// PacketM.cs:147
static public bool IsValidCheckSum(FbsPkHeadM fbsPkHeadM)
{
    return true;
}
```

`IoPipelineSrvM.cs:303`이 이 함수를 호출해 `false`면 `throw`하도록 되어 있지만 **절대 false를 반환하지 않는다.** 송신 측은 체크섬 값을 항상 상수 `1`로 넣는다(`PacketM.cs:234`, `:334`, `PkObjM.cs:83`, `:177`).

즉 **`.fbs` 스키마의 `byteCheckSum` 필드는 1바이트를 낭비하며 아무 검증도 하지 않는다.** 이전 인벤토리에서 "체크섬 검증"을 승계 자산으로 적었던 것은 틀렸다.

### 2. FlatBuffers 헤더 오버헤드가 정량 확인됐다

```csharp
// PacketM.cs:102~104
public static ushort gPkHeadLen  = 28;   // 실제 데이터 7바이트
public static ushort gConHeadLen = 24;   // 실제 데이터 6바이트
public static ushort gEncHeadLen = 32;   // 실제 데이터 9바이트
```

| 헤더 | 실제 필드 | FlatBuffers 크기 | 오버헤드 |
|---|---|---|---|
| `FbsPkHeadM` | `pid`(4) + `conHeadLen`(2) + `checkSum`(1) = **7B** | **28B** | 4.0× |
| `FbsContentHeadM` | `packetType`(2) + `conDataLen`(4) = **6B** | **24B** | 4.0× |
| `FbsEncryptHeadM` | `isCompress`(1) + `encDataLen`(4) + `originDataLen`(4) = **9B** | **32B** | 3.6× |

평문 패킷의 헤더는 **52바이트**다. 고정 `struct`로 하면 **13바이트**(정렬 포함해도 16B)로 끝난다.
**패킷당 36~39바이트가 순수 낭비.** 초당 10만 패킷이면 **약 3.7 MB/s**를 헤더 오버헤드로 태운다.

이 수치가 **ADR-0002(프레임 헤더에 직렬화 포맷을 쓰지 않는다)의 정량적 근거**다.

또한 `gPkHeadLen` 등이 `const`도 `readonly`도 아닌 **`public static` 가변 필드**다. 누구든 런타임에 바꿀 수 있고, 바꾸면 프레이밍이 즉시 깨진다.

### 3. 고정 struct 헤더 코드가 이미 작성돼 있었다 — 그리고 폐기됐다

`BasicLibM/SerializeM.cs`는 **전체가 주석 처리된 빈 클래스**다. 내용은 `Marshal.PtrToStructure` / `Marshal.StructureToPtr` 기반 고정 struct 직렬화 참고 코드다.

```csharp
// SerializeM.cs:9~18 (주석)
//public static PkHeadMOld? Deserialize(byte[] pkHeadByte) {
//    IntPtr pHeader = Marshal.AllocHGlobal(pkHeadLen);
//    Marshal.Copy(pkHeadByte, 0, pHeader, pkHeadLen);
//    PkHeadMOld? head = (PkHeadMOld?)Marshal.PtrToStructure(pHeader, typeof(PkHeadMOld));
```

타입 이름이 **`PkHeadMOld`** — 즉 **원래 고정 struct 헤더였고, FlatBuffers로 갈아탔다.** 그 결과가 위 2번의 4배 오버헤드와 sentinel 값 오염(`-1`, `65535`)이다.

ChServerM은 **이 결정을 되돌린다.** 단 `Marshal`/`AllocHGlobal`이 아니라 `MemoryMarshal`/`BinaryPrimitives`로 — 할당 없이, 안전하게.

---

## `PACKET_TYPE` (enum)

`PacketM.cs:34`, `: ushort`

### 동작

| 범위 | 용도 |
|---|---|
| `0` | `NOT_USED` — **금지**. 주석: *"Flatbuffer 때문에 사용시 헤더 사이즈 달라짐"* |
| `1 ~ 40000` | **앱(상속받은 서버/클라)이 자유롭게 사용** |
| `40001 ~` | 프레임워크 예약 |

프레임워크 패킷: `PSC_RQ_DISCONNECT`(40001), `PSC_COMP_ENC_CHANGE`, `PSC_RSA`, `PC_VERSION_CHECK_RESULT`, `PC_LOGIN_OK`, `PC_HEART_BIT`, `PC_PROGRESS_BAR`, `PC_SERVER_TICK`, `PS_RSP_SERVER_TICK`, `PS_VERSION_CHECK`, `PS_LOGIN`, `PS_LOGIN_FIN`, `PS_LOGOUT`, `PS_HEART_BIT_ALIVE`

접두어 규약: `PSC_` = 양방향, `PC_` = 서버→클라, `PS_` = 클라→서버

### 판정

🟢 **승계** (규약). **앱/프레임워크 ID 공간 분리**와 **방향 접두어 규약**은 그대로 가져간다. `0` 금지 제약은 ADR-0002로 사라진다 (고정 struct 헤더는 0을 정상 저장한다).

→ Phase 4 (헤더), Phase 7 (디스패치 ID 검증)

---

## `PacketM` (struct)

`PacketM.cs:99`, `[Serializable] [StructLayout(LayoutKind.Sequential, Pack = 1)] struct`

### 동작

패킷 조립·전송의 중심. 실제로는 struct 인스턴스보다 **static 메서드 모음**으로 쓰인다.

**`TryMakeSendPacketData(...)`** — 와이어 바이트 생성. 오버로드 2개(`:216` in-struct, `:322` 개별 인자)
1. `new FlatBufferBuilder(64)` → `FbsPkHeadM.CreateFbsPkHeadM(fbb, pid, gConHeadLen, 1)` → `fbb.SizedByteArray()`
2. `new FlatBufferBuilder(64)` → `FbsContentHeadM.CreateFbsContentHeadM(fbb, type, len)` → `SizedByteArray()`
3. `ArrayPool.Rent(헤더+콘헤더+데이터)` → `BlockCopy` 3회
4. `compEnc != null`이면: `Compress` → `Encrypt` → `Return(combinePk)` → `new FlatBufferBuilder(64)`로 암호 헤더 → `ArrayPool.Rent` → `BlockCopy` 2회

**`SendPacket(FinalPkDataM)`** (`:171`) — `NetworkStream.WriteAsync(finalData, 0, len)` 후 `finally`에서 `ArrayPool.Return(finalData)`

**`FinalPkDataM`** (`:68`) — `struct { TcpClient _tc; byte[] _pkData; int _sendPkDataLength; }`. `IUIThreadCheck` 구현, `IsUIThread() => false`

**`FbsClassFactory<T>`** (`:465`) — FlatBuffers 직렬화 Template Method 추상 기반. 파생: `FsEncryptKeyFactory`, `FsLoginOkFactory`, `FsLoginFinFactory`, `FsServerTickFactory`, `FsStrArrayFactory`, `FsMetaDataFactory`, `FsProgressBarFactory`

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **체크섬 검증이 `return true`** — 위 3대 발견 #1 | `:147~150` | 🔴 치명 |
| 2 | **패킷당 할당 폭발.** 평문 패킷: `FlatBufferBuilder` ×2 + `SizedByteArray()` ×2 + `ArrayPool.Rent` ×1 = **최소 5개 객체**. 암호 패킷: `FlatBufferBuilder` ×3 + `SizedByteArray()` ×3 + `Rent` ×2 + `Compress`/`Encrypt` 반환 배열 = **8개 이상** | `:231~302` | 🔴 치명 |
| 3 | **헤더 길이 상수가 `public static` 가변 필드** — `const`/`readonly`가 아니다 | `:102~104` | 🔴 높음 |
| 4 | **`ArrayPool` 반납 누수 (경쟁).** `SendPacket`의 `:177`에서 `tc.Connected == false`면 Return 후 종료. `:183`에서 다시 `if (tc.Connected)`. **두 검사 사이에 연결이 끊기면** `else`(`:200`)로 가서 **Return 없이 종료** → 누수 | `:177~204` | 🔴 높음 |
| 5 | **`TryMakeSendPacketData`가 true를 반환한 뒤 큐 Post가 실패/드롭되면 대여 배열이 영구 누수.** 소유권이 호출자에게 암묵적으로 넘어가는데 계약이 코드로 표현되지 않음 | 구조적 | 🔴 높음 |
| 6 | **90줄 코드가 4곳에 복제됨** — `PacketM.cs:216`, `:322`, `PkObjM.cs:74`, `:168`. 한 곳을 고치면 나머지 3곳을 놓친다 | 다수 | 🟠 중간 |
| 7 | **`FbsClassFactory.Serialize()`의 호출 순서가 뒤집혔다.** `GetOffset()`(실제 작업) → `StartFbsFuncCall()`(테이블 시작) → `Finish()`. FlatBuffers는 Start → Add → End 순서를 요구한다. `Create*` 정적 메서드가 내부에서 Start/End를 하므로 **우연히 동작**하지만, `StartFbsFuncCall()`은 **끝나지 않는 테이블을 열어둔 채** 방치한다. 모든 파생 클래스가 무의미한 메서드를 구현해야 한다 | `:521~529` | 🟠 중간 |
| 8 | `PacketType` setter가 0을 감지해 로그를 남기고 **그대로 대입한다** | `:125` | 🟠 중간 |
| 9 | `[StructLayout(Pack = 1)]` + `[Serializable]`이 참조 필드(`TcpClient`, `byte[]`)를 가진 struct에 붙어 있다 — 의미 없고 오해를 유발 | `:97~98` | 🟡 낮음 |
| 10 | `SendPacket`이 예외를 `Debug.WriteLine`으로 삼킨다 | `:190~194` | 🟠 중간 |
| 11 | `SerializeLoginIdPw(id, pw, version)` — **비밀번호를 페이로드에 평문 직렬화**. 핸드셰이크가 MITM 가능하므로(01 문서 참조) 자격증명이 실질적으로 노출 | `:415~429` | 🔴 치명 |
| 12 | `FsMetaDataFactory` 생성자에 `int k = 0;` 죽은 코드 | `:725` | 🟡 낮음 |

### 개선점 (ChServerM)

- **헤더를 고정 `readonly struct`로.** `[StructLayout(LayoutKind.Sequential)]` + `MemoryMarshal.Read/Write` 또는 `BinaryPrimitives`. 크기는 `Unsafe.SizeOf<T>()`로 컴파일 타임 확정. **버전 필드 필수** (Phase 4)
- **무결성은 AEAD 태그로.** 별도 체크섬 필드를 두지 않는다. 평문 모드가 필요하면 `XxHash3`를 쓰되 **실제로 검증한다** (Phase 4·9)
- **조립 경로를 `IBufferWriter<byte>` 단일 경로로 통합.** 4곳의 복제를 하나로. 헤더는 `writer.GetSpan(HeaderSize)`에 직접 쓰고 `Advance`. 중간 배열 0개 (Phase 3·4)
- **대여 소유권을 타입으로.** `PooledBuffer` ref struct 또는 `IMemoryOwner<byte>`로 "누가 반납하는가"를 컴파일러가 강제 (Phase 3)
- `FbsClassFactory`는 폐기. 직렬화 어댑터(`IMessageSerializer`)가 이 역할을 흡수하고, 소스 제너레이터가 보일러플레이트를 생성 (Phase 6·7)
- 비밀번호는 **페이로드에 담지 않는다.** TLS 위에서 토큰 기반 인증 (Phase 9)

### 판정

🟡 **개작**. `PACKET_TYPE` 규약과 "헤더+콘텐츠헤더+데이터" 3단 구조는 승계. 나머지 구현은 전량 재작성.

→ Phase 3 (버퍼), Phase 4 (프레이밍), Phase 6 (직렬화), Phase 9 (보안)

---

## `MemPacketM` / `EncMemPacketM` / `MemPkDispatcher` / `AbMemPkAction`

`MemPacketM.cs`

### 동작

**`MemPacketM`** (`:52`, `struct : IUIThreadCheck`) — 수신 완료된 한 프레임.
필드: `UserM _u`, `TcpClient _tc`, `FbsPkHeadM _pkHead`, `FbsContentHeadM _conHead`, `byte[] _conData`, `byte[] pooledPkHead`, `byte[] pooledConHead`, `int ConDataLen`

**`EncMemPacketM.MakeMemPacket(CompressAndEncryptM)`** (`:146`) — 복호 파이프라인
`IsCompress == 1`이면 `Decompress` → `Decrypt` → `ReadOnlySpan` 슬라이스로 `gPkHeadLen` / `gConHeadLen` / `conDataLen` 순차 파싱 → `new MemPacketM(...)`

**`MemPkDispatcher`** (`:178`)
- `static Dictionary<PACKET_TYPE, AbMemPkAction> _dicMemPkAction`
- `static Dictionary<PACKET_TYPE, bool> _dicIsMemPkUiThread`
- `static async Task MemPkAction(MemPacketM)` — **화이트리스트 검증 → 딕셔너리 조회 → 핸들러 await**
- `Add(AbMemPkAction)` / `LoadActions()` — 리스트 → static 딕셔너리 이관

**`AbMemPkAction`** (`:271`) — `abstract Task MemPkAction(MemPacketM)`. 생성자에 `PACKET_TYPE` + `bool bMemPkUIThread`

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`pooledPkHead` / `pooledConHead` / `ConDataLen`이 생성자에서 설정되지 않는다.** 항상 `null` / `0`. XML 주석은 *"ArrayPool 해제를 위한 byte[]"*, *"반드시 아래 것을 사용한다"*고 명시하지만 **구현이 없다.** 풀링 설계를 시작했다가 `ToArray()`로 대체하면서 **필드와 주석만 남았다** | `:67~68`, `:84`, `:109~115` | 🔴 높음 |
| 2 | **주석의 계약이 지켜지지 않는다.** *"MemPkDispatcher.MemPkAction() 이후 바로 어레이 풀에 리턴함"*(`:62`, `:78`) — 그런데 `MemPkAction`의 `finally`가 **빈 블록**(`:221~224`). 반납 코드가 어디에도 없다 | `:221~224` | 🔴 높음 |
| 3 | **화이트리스트 위반 시 `throw new Exception`**, 그리고 catch에서 **새 Exception으로 재포장**(`:219`) → **스택 트레이스 유실**. 게다가 이 예외가 샤드 실행기로 전파되어 삼켜지면 **프로토콜 위반 후에도 커넥션이 살아 있다** | `:203`, `:219` | 🔴 높음 |
| 4 | **`user != null`일 때만 화이트리스트 검증** (`:197`). 로그인 전(`user == null`)에는 이 경로가 검증을 **건너뛴다**. 별도로 `ServerM.SendMemPk`가 `IsAllowedPacketNotLogined`를 검사하지만 **검증 지점이 두 곳으로 갈려 있어** 한쪽만 고치면 구멍이 남는다 | `:197` | 🔴 높음 |
| 5 | **`_dicMemPkAction`이 `static`** — 프로세스당 하나. 같은 프로세스에 서버+클라이언트를 띄우면 핸들러 테이블이 충돌 | `:182~183` | 🟠 중간 |
| 6 | **UI 스레드 개념이 서버 라이브러리에 침투.** `IUIThreadCheck`, `_dicIsMemPkUiThread`, `bMemPkUiThread` — 서버에는 UI 스레드가 없다 | 전역 | 🟠 중간 |
| 7 | `EncMemPacketM.MakeMemPacket`에서 `viewBuffer.ToArray()` **3회** | `:159`,`:163`,`:169` | 🟠 중간 |
| 8 | `MakeMemPacket`이 `readonly` 아닌 struct 메서드에서 `_encDataLen`을 변경(`:152`) — 복사본에 쓰므로 값이 유실됨. 무해하지만 코드 스멜 | `:152` | 🟡 낮음 |
| 9 | `IsMemPkUiThread`가 `ContainsKey` 후 `true` 반환 — 딕셔너리의 `bool` 값을 읽지 않는다 | `:229~235` | 🟡 낮음 |
| 10 | 핸들러 미등록 시 조용히 통과(`else` 블록이 주석만) — 등록 누락이 런타임에 무증상 | `:210~214` | 🟠 중간 |

### 개선점 (ChServerM)

- **`MemPacketM`을 `ref struct` 프레임 뷰로.** `ReadOnlySpan<byte>` 페이로드 + 고정 struct 헤더. **배열 소유 자체를 없앤다** → 반납 문제가 소멸 (Phase 4)
- **화이트리스트 검증을 단일 미들웨어로 통합.** 로그인 전/후 경로를 하나로. 위반 = `TryXxx` 실패 → **커넥션 종료**. 예외 사용 금지 (Phase 9)
- **핸들러 미등록을 컴파일 타임에 검출** — 소스 제너레이터가 메시지 ID ↔ 핸들러 매핑을 검증하고 누락을 빌드 실패로 (Phase 7)
- 딕셔너리 조회 → **소스 생성 스위치 테이블** (Phase 7)
- static 테이블 제거, DI 스코프
- **UI 스레드 개념 전량 제거.** 클라이언트 SDK가 필요하면 별도 패키지에서 동기화 컨텍스트를 다룬다

### 판정

🟡 **개작**. `AbMemPkAction` = `IMessageHandler<T>`, 화이트리스트 검증 지점, 핸들러 등록 흐름은 승계. 나머지 재작성.

→ Phase 4 (프레임 뷰), Phase 7 (디스패치), Phase 9 (검증)

---

## `AllowedPacketMan` (+ `AllowedPacketItem` / `AllowedPacketGroup` / Builder)

`AllowedPacketM.cs`

### 동작

**Composite + Builder 패턴의 정석 구현.** 레거시에서 가장 깔끔하게 짜인 파일이다.

```
IAllowedPacket { bool IsAllowedPacket(PACKET_TYPE) }
├─ AllowedPacketItem   : 패킷 타입 1개 보유 (leaf)
└─ AllowedPacketGroup  : List<IAllowedPacket> 보유 (composite, 중첩 가능)

AllowedPacketMan
├─ Dictionary<ALLOWED_PACKET_STATE, AllowedPacketGroup> _dicPacketMan
└─ List<PACKET_TYPE> _pkAllAlloweded        ← 모든 상태에서 허용 (예: 하트비트)
```

**`IsAllowed(state, pkType)`** (`:157`)
1. `state == A_SC_ANY_STATE` → 무조건 `true`
2. `_pkAllAlloweded.IndexOf(pkType) >= 0` → `true`
3. `_dicPacketMan[state].IsAllowedPacket(pkType)`

**Builder** (`:194`) — `StartAllowedPkGroup(state)` → `AddPacketType(type)`* / `AddAlreadResteredPkGroup(state)` → `EndAllowedPkGroup()` → `Build()`
생성자가 `private`이라 **Builder를 통해서만 생성 가능**. 중복 그룹 등록·미완료 그룹·빈 매니저를 예외로 차단.

`ALLOWED_PACKET_STATE`: `A_SC_NOT_LOGINED`(30000), `A_SC_ANY_STATE`, `A_SC_START`. 앱이 확장하도록 설계.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`A_SC_ANY_STATE`가 무조건 `true`** — 이 상태로 잘못 설정된 유저는 **모든 검증을 우회**한다. 주석에 *"보통 클라에서 편하게 세팅"* — 편의 기능이 서버에도 존재 | `:160~161` | 🔴 높음 |
| 2 | **`_pkAllAlloweded`가 `List` + `IndexOf` → 패킷당 O(n) 선형 탐색** | `:141`, `:163` | 🟠 중간 |
| 3 | **`AllowedPacketGroup.IsAllowedPacket`이 `foreach` + 인터페이스 가상 호출.** 그룹에 타입 20개면 **패킷당 가상 호출 20회** | `:117~126` | 🟠 중간 |
| 4 | Composite 구조상 **패킷 타입 N개당 힙 객체 N개**(`AllowedPacketItem`) | 구조적 | 🟡 낮음 |
| 5 | Builder가 `Exception` / `NotImplementedException`을 검증 실패에 사용 — 의미가 맞지 않는 예외 타입 | `:212`,`:217`,`:287`,`:292` | 🟡 낮음 |
| 6 | 메서드명 오타 — `AddAlreadResteredPkGroup` (Already Registered) | `:237` | 🟡 낮음 |
| 7 | 120줄 분량의 이전 구현이 주석으로 보존 | `:304~424` | 🟡 낮음 |

### 개선점 (ChServerM)

- **자료구조를 비트맵으로 교체.** `PACKET_TYPE`이 `ushort`이므로 상태당 `ulong[1024]`(8KB) 비트맵이면 **O(1) 조회, 할당 0, 가상 호출 0**. 상태 수가 많아도 8KB × 상태 수로 감당 가능. 최소한 `FrozenSet<PACKET_TYPE>`(.NET 8+)
- **Builder API는 그대로 승계** — 사용성이 좋고 잘못된 사용을 시작 시점에 막는다. 단 예외를 `InvalidOperationException` / `ArgumentException`으로 정정하고, 프레임워크는 **옵션 검증(`IValidateOptions<T>`)** 으로 통합 (Phase 2)
- **`A_SC_ANY_STATE` 같은 전체 허용 상태를 서버에서 제거.** 필요하면 명시적 `AllowAll` 옵션으로 노출하고 **경고 로그**를 남긴다 (Phase 9)
- 컴파일 타임 생성 검토: 상태×패킷 매트릭스를 소스 제너레이터가 비트맵 상수로 굽는다 (Phase 7)

### 판정

🟢 **승계** (설계). **레거시에서 두 번째로 값어치 있는 파일.** 상태 기반 패킷 화이트리스트는 게임 서버 보안의 기본이고, Composite + Builder 조합도 적절하다. 자료구조만 교체한다.

→ Phase 2 (Builder·검증), Phase 9 (보안 미들웨어)

---

## `PkObjM`

`PkObjM.cs:19`, `abstract class PkObjM : IHasGameOid`

### 동작

패킷을 주고받는 객체(= 유저)의 기반. `InnerUserM`이 이걸 상속한다.

**상태**: `uint Pid`, `TcpClient Tc`, `CancellationTokenSource Cts`, `CompressAndEncryptM _compEnc`, `long LastPkRecvTick`, `long Oid`
**송신 버퍼**: `byte[] _sendBuffer`(생성자에서 `new byte[65536]`), `int _sendBufferLength`, `int MaxBufferSize = 65536`, `const int BatchSize = 16384`

**`WriteSendBuffer(PACKET_TYPE, byte[])`** (`:74`) / **`WriteSendBuffer(PACKET_TYPE, ReadOnlySequence<byte>)`** (`:168`)
`TryMakeSendPacketData`와 동일한 조립 로직(헤더 2개 + 압축·암호화) 후, 결과를 `_sendBuffer`에 누적한다.
```csharp
int lengthCheck = finalDataLen + _sendBufferLength;
if (lengthCheck > BatchSize) {           // 16384 초과 시 flush
    FlushSendBuffer();
    if (finalDataLen > MaxBufferSize) {  // 단일 패킷이 버퍼보다 크면
        MaxBufferSize = finalDataLen;
        _sendBuffer = new byte[MaxBufferSize];
    }
}
Buffer.BlockCopy(finalData, 0, _sendBuffer, _sendBufferLength, finalDataLen);
_sendBufferLength += finalDataLen;
```

> **송신 배칭 아이디어는 옳다.** 작은 패킷 다수를 모아 `WriteAsync` 호출(= syscall) 수를 줄이는 것은 고성능 서버의 기본이다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **커넥션당 `new byte[65536]` 고정 할당.** 풀에서 빌리지 않는다. 동시 커넥션 1만 개 = **640MB**. 이것이 커넥션당 메모리의 주범 | `:44` | 🔴 치명 |
| 2 | **버퍼가 커지면 절대 줄지 않는다.** 큰 패킷 하나가 `MaxBufferSize`를 영구히 올린다. 주석은 *"버퍼 사이즈가 너무 크면 초기화"*라는데 코드는 **키운다** — 주석·코드 정반대 | `:155~159` | 🔴 높음 |
| 3 | **스레드 안전성이 없다.** `_sendBuffer` / `_sendBufferLength`를 락 없이 조작. 주석 처리된 이전 버전(`:301`)에는 `lock(_lock)`이 **있었고 제거됐다**. 앱 코드가 임의 스레드에서 `WriteSendBuffer`를 부르면 버퍼 손상 | `:162~163` | 🔴 높음 |
| 4 | **`TryMakeSendPacketData` 로직의 3·4번째 복제본** (90줄 × 2) | `:74`, `:168` | 🟠 중간 |
| 5 | `BatchSize`(16384, `const`)와 `MaxBufferSize`(65536, 가변 `int`)의 관계가 문서화되지 않음. flush 임계값과 버퍼 크기가 4배 차이나는 이유가 불명 | `:38~39` | 🟡 낮음 |
| 6 | `FlushSendBuffer()` 실패 시 처리 없음 — 파생 클래스(`InnerSrvUserM`)가 예외를 삼킨다 | 구조적 | 🟠 중간 |
| 7 | `Cts`가 `public set` — 외부에서 교체 가능 | `:29` | 🟡 낮음 |
| 8 | `ObservablePkObjM`(Observer 패턴) 전체가 주석 처리 | `:320~331` | 🟡 낮음 |

### 개선점 (ChServerM)

- **송신 버퍼를 풀에서 대여.** 커넥션당 고정 할당 대신 `IBufferWriter<byte>` + 슬랩 할당기. **유휴 커넥션은 버퍼를 갖지 않는다** (Phase 3)
- **배칭 정책을 옵션화**: flush 임계값, 최대 배치 크기, 시간 기반 flush(nagle 유사). 근거는 벤치마크로 (Phase 5)
- **스레드 안전성을 실행 모델로 보장.** `IExecutionModel`의 유저별 직렬 실행 안에서만 송신 버퍼를 만지게 하면 **락 없이 안전**해진다. 레거시가 락을 제거한 이유가 이것이었을 가능성이 높지만, 계약으로 표현되지 않아 앱 코드가 깰 수 있었다 → **계약을 타입/API로 강제** (Phase 1·8)
- `ReadOnlySequence<byte>` 오버로드 방향(`sendData.CopyTo(span)`)이 옳다. 이쪽으로 통일 (Phase 4)

### 판정

🟡 **개작**. 송신 배칭과 `ReadOnlySequence` 경로는 승계. 버퍼 관리·동시성은 전면 재작성.

→ Phase 1 (실행 모델 계약), Phase 3 (버퍼 풀), Phase 5 (배칭)

---

## `SendPacketM`

`SendPacketM.cs:12`, `static class`

### 동작

`SendPacketGroupM`의 **비샤딩 단일 큐 버전**. 클라이언트(`UserM`)용.

| 필드 | 타입 |
|---|---|
| `_sendPkActBlock` | `ConcurSeqTaskContextExecLongRunM<FinalPkDataM>` → `PacketM.SendPacket` |
| `_memPkActBlock` | `ConcurSeqTaskContextExecLongRunM<MemPacketM>` → `MemPkDispatcher.MemPkAction` |
| `_memPkActBlockUI` | `ActionBlock<MemPacketM>` + `TaskScheduler.FromCurrentSynchronizationContext()` |

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`TaskScheduler.FromCurrentSynchronizationContext()`를 static 필드 초기자에서 호출.** `SynchronizationContext.Current == null`이면 **`InvalidOperationException`**. 콘솔/서버 프로세스에는 동기화 컨텍스트가 없으므로, 이 클래스를 처음 만지는 순간 **`TypeInitializationException`으로 폭발**한다. 서버가 `SendPacketGroupM`을 쓰기 때문에 지금은 안 터지지만 `UserM` 경로가 이걸 건드리면 터진다 | `:23~24` | 🔴 높음 |
| 2 | 여기에도 `ActionBlock` → `ConcurSeqTaskContextExecLongRunM` 교체 흔적이 주석으로 남음 | `:15`,`:19` | 🟡 낮음 |
| 3 | UI 전용 블록이 서버·클라 공용 코드에 존재 | 전역 | 🟠 중간 |

### 판정

🔴 **폐기**. 샤딩 버전(`SendPacketGroupM`)이 상위 호환이고, UI 스레드 개념은 제거 대상이다. `#1`은 **새 코드에서 재현하지 말 것**의 사례로 기록한다.

→ Phase 8 (실행 모델 단일화)

---

## `SerializeM`

`BasicLibM/SerializeM.cs:3`

**전체가 주석 처리된 빈 클래스.** 내용은 위 "3대 발견 #3" 참조.

### 판정

⚪ **빈 파일**이지만 🔵 **참고 가치가 크다.** `PkHeadMOld`라는 타입명이 **고정 struct 헤더 → FlatBuffers 헤더로 후퇴한 이력**을 증명한다. ADR-0002의 역사적 근거로 보존한다.

---

## `FbsClassM/` (9 파일, 약 600줄)

`flatc --csharp`가 `FlatbufferM/PacketM.fbs`에서 생성한 코드. **손으로 수정하지 않는다.**

| 파일 | 대응 `table` |
|---|---|
| `FbsPkHeadM.cs` | `pid`, `conHeadLen`, `byteCheckSum` |
| `FbsContentHeadM.cs` | `packetType`, `conDataLen` |
| `FbsEncryptHeadM.cs` | `isCompress`, `encDataLen`, `originDataLen` |
| `FbsEncryptKey.cs` | `key[]`, `iv[]` |
| `FbsLogInIdPw.cs` | `id`, `pw`, `version` |
| `FbsLoginOk.cs` | `id`, `oid`, `serverFrequency` |
| `FbsLoginFin.cs` | `pid` |
| `FbsServerTick.cs` | `serverTick` |
| `FbsStrArray.cs` / `FbsMetaData.cs` / `FbsProgressBar.cs` | 메타데이터·UI 전송 |

빌드 전 이벤트로 생성하도록 `.fbs` 주석에 기록돼 있다:
```
$(ProjectDir)FlatbufferM\flatc.exe --csharp -o $(SolutionDir) $(SolutionDir)AppPacketM.fbs
```

### 판정

🔴 **폐기** (헤더 3종) / 🔵 **참고** (페이로드 4종).
헤더 `table` 3개는 ADR-0002로 고정 struct로 대체된다. 페이로드 스키마(`LogInIdPw`, `LoginOk`, `ServerTick`)는 **메시지 정의의 참고**로만 쓴다 — `pw` 필드는 승계하지 않는다(Phase 9).
`flatc.exe`는 저장소에 넣지 않는다. 필요하면 NuGet 도구 패키지로.

→ Phase 4 (헤더 대체), Phase 6 (직렬화 어댑터)

---

## 이 계층의 종합 판정

| 항목 | 판정 | 대응 Phase |
|---|---|---|
| **상태 기반 패킷 화이트리스트 (Composite+Builder)** | 🟢 승계 | 2, 9 |
| **`PACKET_TYPE` ID 공간 분리 + 방향 접두어 규약** | 🟢 승계 | 4, 7 |
| **송신 배칭 (syscall 수 절감)** | 🟢 승계 | 5 |
| **3단 구조 (패킷헤더 → 콘텐츠헤더 → 데이터)** | 🟢 승계 | 4 |
| **`ReadOnlySequence` 기반 쓰기 경로** | 🟢 승계 | 4 |
| `AbMemPkAction` 핸들러 모델 | 🟡 개작 | 7 |
| 패킷 조립 (`TryMakeSendPacketData`) | 🟡 개작 | 3, 4 |
| 송신 버퍼 관리 | 🟡 개작 | 3, 5 |
| FlatBuffers 헤더 | 🔴 폐기 | 4 |
| 체크섬 필드 | 🔴 폐기 | 4, 9 |
| `FbsClassFactory<T>` | 🔴 폐기 | 6 |
| UI 스레드 디스패치 | 🔴 폐기 | — |
| `SendPacketM` (비샤딩) | 🔴 폐기 | 8 |

### 새 코드에 절대 옮기면 안 되는 것

1. `PacketM.cs:147` — **`IsValidCheckSum`이 무조건 `true`**
2. `PacketM.cs:102~104` — **헤더 길이가 `public static` 가변 필드**
3. `PacketM.cs:415` — **비밀번호를 페이로드에 직렬화**
4. `PacketM.cs:177~204` — **`ArrayPool` 반납 누수 (경쟁 조건)**
5. `MemPacketM.cs:203,219` — **화이트리스트 위반을 예외로 처리 + 스택 트레이스 유실 + 커넥션 유지**
6. `MemPacketM.cs:197` — **로그인 전 경로에서 화이트리스트 검증 건너뜀**
7. `PkObjM.cs:44` — **커넥션당 64KB 고정 할당**
8. `PkObjM.cs:155~159` — **송신 버퍼가 단조 증가 (축소 없음)**
9. `PkObjM.cs:162` — **송신 버퍼 조작에 동기화 없음** (이전 버전의 락을 제거)
10. `SendPacketM.cs:23` — **static 초기자의 `FromCurrentSynchronizationContext()`** → 서버에서 `TypeInitializationException`

### 정량 근거 요약 (ADR용)

| 항목 | 레거시 | ChServerM 목표 |
|---|---|---|
| 평문 패킷 헤더 크기 | **52 B** (FlatBuffers) | **13~16 B** (고정 struct) |
| 패킷당 힙 할당 (평문) | **최소 5개** | **0개** |
| 패킷당 힙 할당 (암호) | **8개 이상** | **0개** |
| 커넥션당 송신 버퍼 | **64 KB 고정** | 풀 대여, 유휴 시 0 |
| 화이트리스트 조회 | **O(n) + 가상 호출 n회** | **O(1) 비트맵** |
| 디스패치 조회 | 딕셔너리 해시 | 생성 스위치 테이블 |
