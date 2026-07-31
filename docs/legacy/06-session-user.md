# 06 — 세션 / 유저 모델

**전량 정독 완료** — `PublicLib/UserM.cs`(470), `PublicLib/MembersM.cs`(111), `PublicLib/GlobalM.cs`(72) — 총 653줄

서버 전용 파생(`InnerSrvUserM` / `SrvUserM`)은 [문서 01](01-network-transport.md#innersrvuserm--srvuserm)에 있다. 여기서는 **공유 기반**을 다룬다.

---

## 계층 구조

```
PkObjM (추상, 문서 02)          송신 버퍼 · Pid · Oid · Tc · CompEnc
   ▲
InnerUserM : IDisposable, IObservable<InnerUserM>     실제 상태 보유
   ▲                                                 Id, AllowedPkState, ObserverState, dicTimer
InnerSrvUserM (서버 전용)                              DB_ID, netDelay, MetaDataDownloadOk

UserM        ── 얇은 래퍼 (_user가 null이면 기본값 반환, IsExist로 판단)
   ▲
SrvUserM (서버 전용)
```

**Inner/Wrapper 2계층**의 의도는 문서 01에서 설명했다 — 앱 코드가 이미 삭제된 유저 핸들을 만져도 `NullReferenceException`이 나지 않게 하는 보호 장치다.

---

## `InnerUserM`

`UserM.cs:32`, `class InnerUserM : PkObjM, IDisposable, IObservable<InnerUserM>`

### 동작

| 멤버 | 내용 |
|---|---|
| `Id` | 유저 ID |
| `_pw` | 유저 비밀번호 (필드만 존재, 프로퍼티는 주석 처리) |
| `AllowedPkState` | 패킷 화이트리스트 상태 (문서 02) |
| `ObserverState` | `NORMAL` / `DISCONNECTING` |
| `dicTimer` | `TimerM<TIMER_TYPE>` — 유저별 타이머 집합 |
| `_observerList` | `LinkedList<IObserver<InnerUserM>>` |

**종료 절차** (`DisconnectProcess`, `:142`)
```
ObserverState = DISCONNECTING
  → 모든 옵저버에게 OnNext(this)          // 종료를 알림
  → Dispose()                            // 리소스 정리
       → NotifyObserversComplete()        // OnCompleted() + 목록 비우기
       → dicTimer.DisposeAllTimer()
       → _compEnc?.Dispose()
```

> **Observer 패턴으로 "유저가 나갔다"를 전파하는 설계는 타당하다.** 맵·파티·룸 등이 유저를 구독해 두면 종료 시 자동으로 정리 신호를 받는다. `DISCONNECTING` 상태를 먼저 세워 옵저버가 "정리 중"임을 알 수 있게 한 것도 옳다.

`RequestDisconnectForce()` (`:158`) — `Tc.Client.Shutdown(SocketShutdown.Send)`로 FIN 전송. 서버판(`InnerSrvUserM`)은 여기에 강제 종료 타이머를 더한다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`ArrayPool` 반납이 주석 처리돼 있다.** `finally { //GlobalM.arrayPool.Return(dataToSend); }` — 반납 코드가 **작성됐다가 비활성화**됐다. 클라이언트 송신 경로에서 대여 배열이 전량 유실된다 | `:100~103` | 🔴 치명 |
| 2 | **`string _pw` 필드가 유저 객체에 남아 있다.** 프로퍼티는 주석 처리했지만 필드는 살아 있고, 어디서도 지우지 않는다 | `:36` | 🔴 높음 |
| 3 | **옵저버 순회가 O(n²).** `_observerList.ToArray()`로 방어 복사(매번 할당)한 뒤, 루프 안에서 다시 `_observerList.Contains(observer)`로 **O(n) 선형 탐색**을 한다 | `:145~149`, `:205~209` | 🟠 중간 |
| 4 | **파이널라이저 `~InnerUserM()`** — 모든 유저 인스턴스가 종료 큐에 올라 GC 압력 증가. 관리되지 않는 리소스를 직접 들고 있지도 않다 | `:223~226` | 🟠 중간 |
| 5 | `List<byte[]> sendByteBuffer` — 선언만 되고 **한 번도 쓰이지 않는다** | `:39` | 🟡 낮음 |
| 6 | 송신 실패를 `Debug.WriteLine`으로 삼킨다 | `:95~99` | 🟠 중간 |
| 7 | `_observerList`가 `LinkedList`(비동시성)인데 옵저버 등록/해제가 임의 스레드에서 올 수 있다 | `:188` | 🟠 중간 |
| 8 | `Dispose(bool)` 말미의 `// base.Dispose(disposing);` — **기반 클래스 자신에 "파생에서 호출할 것"이라 적혀 있다.** 의미가 뒤집힌 주석 | `:253~254` | 🟡 낮음 |

---

## `UserM` (래퍼)

`UserM.cs:262`

모든 프로퍼티가 `_user != null` 검사 후 값 또는 기본값을 반환한다. `IsExist`로 존재 여부를 노출한다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`Id` setter가 확정적으로 `NullReferenceException`을 던진다.** 래퍼의 존재 이유를 정면으로 위반한다 | `:313~319` | 🔴 치명 버그 |
| 2 | 🔴 **`AllowedPkState` getter의 기본값이 `A_SC_ANY_STATE`** — **존재하지 않는 유저가 "모든 패킷 허용"으로 판정된다.** 기본값은 "전부 거부"여야 한다. 문서 02의 화이트리스트 우회 경로와 직결 | `:340` | 🔴 높음 |
| 3 | `DisconnectProcess()`가 `async ValueTask`인데 **`await`이 없다.** 호출자는 비동기 완료를 기다린다고 착각한다 | `:412~415` | 🟠 중간 |
| 4 | `Subscribe`가 `_user == null`이면 **`null`을 반환**한다 — `IObservable<T>` 계약 위반. 호출자마다 null 검사 강요 | `:435~441` | 🟠 중간 |
| 5 | `Dispose()`가 있으나 `IDisposable`을 구현하지 않는다 | `:455`, `:262` | 🟠 중간 |
| 6 | `class` 래퍼라 조회할 때마다 힙 할당 (문서 01 `SrvGlobal.GetUser` 참조) | `:262` | 🟠 중간 |

버그 #1 원문:
```csharp
public string Id {
    get => (_user != null) ? _user.Id : string.Empty;
    set {
        if (_user != null) _user.Id = value;
        else _user.Id = string.Empty;   // ← _user가 null인 분기에서 _user 역참조
    }
}
```

### 개선점 (Inner/Wrapper 전체)

- **의도는 승계, 구현은 `readonly struct` 핸들로.**
  ```
  readonly struct SessionHandle { SessionId Id; uint Generation; }
  ```
  세대(generation) 카운터를 두면 **삭제된 세션 핸들을 O(1)로 식별**할 수 있고, **할당이 0**이 된다. 현재 구조의 두 문제(할당, null 분기 버그)가 동시에 사라진다
- 존재하지 않는 세션의 기본값은 **가장 안전한 값**으로 — `AllowedPkState`는 "전부 거부"
- 옵저버는 **이벤트 버스**로 대체하거나, 유지하더라도 `HashSet` + 배열 스냅샷으로 O(n²) 제거
- 파이널라이저 제거, `IAsyncDisposable`만

### 판정

🟡 **개작** — Inner/Wrapper 안전 핸들 개념과 Observer 종료 전파는 승계, 구현은 재작성.
→ Phase 1 (ID·핸들), Phase 13 (세션)

---

## `MembersM.cs` — 대부분이 주석이다

`PublicLib/MembersM.cs`

111줄 중 **46줄만 활성 코드**이고 나머지는 주석이다.

### 활성 클래스 3개

**`UnsubscriberM<T>`** (`:9`) — `LinkedList` 기반 구독 해제 토큰
**`ConcurrentUnsubscriberM<T>`** (`:29`) — `ConcurrentDictionary` 기반
**`ConcurrentObservableM<T>`** (`:48`) — oid를 키로 옵저버를 보관

### 주석 처리된 것 — 멤버 그룹 브로드캐스트

```csharp
//public class MembersForPk<T> : ConcurrentSparseSetGetM<T> where T : PkObjM
//{        
//    public void SendPacketToMembers(PACKET_TYPE pkType, byte[] data, long bExceptOid = -1)
//    { ... foreach (PkObjM mem in ToArray()) if (mem.Oid != bExceptOid) mem.SerializeSendPacket(...); }
//}
```

> **이것이 룸/파티 브로드캐스트의 원형이었다.** `ConcurrentSparseSetGetM`(문서 04)을 멤버 저장소로 쓰고, oid 하나를 제외하고 전원에게 보내는 구조다. **구현은 폐기됐고**, 실제 브로드캐스트는 `MapObjM.SendPacketToMapUsers`(추상, 문서 03)와 `SrvGlobal.SendPacketToAllUsers`(문서 01)로 흩어졌다.

**이전 계획 정정**: 이 문서를 "유저 모델, 세션, **멤버 그룹**"으로 예정했으나, **멤버 그룹 기능은 활성 코드로 존재하지 않는다.**

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`ConcurrentObservableM.Subscribe`의 `(observer as T).Oid`** — 옵저버가 `T`가 아니면 `as`가 null을 반환해 **NRE**. 패턴 매칭이나 제약이 필요하다. 또한 "관찰자가 곧 게임 오브젝트여야 한다"는 이상한 결합 | `:54` | 🔴 높음 |
| 2 | `UnsubscriberM.Dispose`가 `Contains` 후 `Remove` — **`LinkedList`에서 O(n) 탐색을 두 번** | `:22~23` | 🟠 중간 |
| 3 | `ConcurrentUnsubscriberM.Dispose`가 `ContainsKey` 후 `TryRemove` — **중복 조회**. `TryRemove` 하나면 충분 | `:42~43` | 🟡 낮음 |
| 4 | `UnsubscriberM`은 스레드 안전하지 않은데 `InnerUserM`이 이것을 쓴다 | `:9`, `UserM.cs:197` | 🟠 중간 |

### 판정

🔵 **참고**. `MembersForPk` 주석 코드가 **Phase 18 룸 브로드캐스트의 요구사항**을 알려준다 — "멤버 집합 + 한 명 제외 브로드캐스트". 단 새 구현은 **같은 페이로드를 N명에게 보낼 때 직렬화 1회**를 계약에 넣는다(레거시는 멤버마다 `SerializeSendPacket`을 호출해 **N번 직렬화**한다).

→ Phase 18

---

## `GlobalM` / `CompressAndEncryptManM`

`PublicLib/GlobalM.cs`

### `GlobalM` (`:11`)

| 멤버 | 내용 |
|---|---|
| `screenWidth/Height/HalfWidth/HalfHeight` | static 화면 크기 |
| `MakeGameOid()` | `Interlocked.Increment(ref gameOid)` — 게임 오브젝트 OID 발급 |

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | 🔴 **OID가 프로세스 전역 단조 증가 카운터다.** 노드 식별자 성분이 없어 **다중 노드 배포 시 OID가 충돌한다.** 스케일아웃의 구조적 차단 요인 | 🔴 높음 |
| 2 | OID가 항상 1부터 시작 — 재시작 시 **이전 세션의 OID와 겹친다.** 영속화된 데이터와 대조하면 오염 | 🔴 높음 |
| 3 | **클라이언트 화면 크기가 서버·클라 공용 전역에 있다** (문서 05의 소스 공유 모델 때문) | 🟠 중간 |

**개선점**: 분산 ID 생성으로. Snowflake 계열(타임스탬프 + 노드 ID + 시퀀스) 또는 노드별 사전 할당 블록. Phase 1의 강타입 `ObjectId`와 묶는다.
→ **Phase 15(클러스터)의 선결 조건이다.** 지금 `long` 단조 증가로 굳히면 나중에 못 바꾼다.

### `CompressAndEncryptManM` (`:36`)

`ConcurrentDictionary<TcpClient, CompressAndEncryptM>` — **로그인 완료 전** 임시 보관소. RSA 핸드셰이크로 만든 암호 객체를 커넥션에 붙여 두었다가, 유저 생성 시 `InnerUserM._compEnc`로 이관하고 제거한다.

정리 지점: `DoPkLogin`의 `TryRemove`(문서 01), `SrvFillPipeAsync`의 `finally`, `ServerDisconnectProcess`.

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **암호 자료가 `TcpClient` 객체를 키로 전역 딕셔너리에 보관**된다. 정리 지점이 3곳으로 흩어져 있어 한 경로라도 새면 **키 자료가 프로세스 수명 내내 남는다** | 🔴 높음 |
| 2 | `IsReadyCompEnc`의 사용처를 찾지 못했다 | 🟡 낮음 |
| 3 | static 전역 — 다중 서버 인스턴스 불가 | 🟠 중간 |

**개선점**: 핸드셰이크 상태를 **커넥션 상태 기계의 일부**로 만든다(Phase 5). 커넥션이 소유하면 커넥션 정리 시 자동으로 함께 사라져 정리 지점이 하나가 된다.

### 판정

🟡 **개작** (`GlobalM` OID) / 🔴 **폐기** (`CompressAndEncryptManM` — 커넥션 상태로 흡수)
→ Phase 1 (ID), Phase 5 (커넥션 상태), Phase 15 (분산 ID)

---

## 이 계층의 종합

| 항목 | 판정 | Phase |
|---|---|---|
| **Inner/Wrapper 안전 핸들 개념** | 🟢 승계 (struct로) | 1·13 |
| **Observer로 유저 종료 전파 + `DISCONNECTING` 상태** | 🟢 승계 | 13·18 |
| **멤버 집합 + 한 명 제외 브로드캐스트** (주석 코드) | 🔵 참고 | 18 |
| `UserM` 래퍼 구현 | 🟡 개작 | 1·13 |
| `GlobalM.MakeGameOid` | 🟡 개작 (분산 ID로) | 1·15 |
| 옵저버 자료구조 (`LinkedList` + O(n²)) | 🟡 개작 | 13 |
| `CompressAndEncryptManM` | 🔴 폐기 (커넥션 상태로) | 5 |
| 파이널라이저 | 🔴 폐기 | — |

### 새 코드에 절대 옮기면 안 되는 것

1. `UserM.cs:313~319` — **`Id` setter가 null 분기에서 null을 역참조** (래퍼의 존재 이유를 위반)
2. `UserM.cs:340` — **존재하지 않는 유저의 `AllowedPkState` 기본값이 `A_SC_ANY_STATE`** (전부 허용)
3. `UserM.cs:100~103` — **`ArrayPool` 반납이 주석 처리됨** (작성됐다가 비활성화)
4. `UserM.cs:36` — **비밀번호를 유저 객체 필드에 보관하고 지우지 않음**
5. `GlobalM.cs:26~29` — **노드 성분 없는 프로세스 전역 OID** (다중 노드에서 충돌, 재시작 시 재사용)
6. `MembersM.cs:54` — **`(observer as T).Oid`** — 타입 불일치 시 NRE
7. `UserM.cs:145~149` — **옵저버 순회 O(n²)** (`ToArray()` 할당 + 루프 내 `Contains`)

### Phase 1 설계에 직접 반영할 것

이 계층이 Phase 1(Core 추상화)에 주는 요구는 명확하다.

- **`SessionHandle`은 세대 카운터를 포함한 `readonly struct`** — 삭제된 세션 접근을 할당 없이 O(1)로 판별
- **존재하지 않는 세션의 모든 기본값은 "가장 제한적인 값"** — 보안 기본값 원칙
- **`ObjectId`는 처음부터 분산 생성 가능한 형태로** — 노드 성분을 포함하지 않으면 Phase 15에서 되돌릴 수 없다
