# 08 — 영속화 (MongoDB)

**전량 정독 완료** — `DBManager/MongoDBManagerM.cs`(714), `DBManager/DBManagerM.cs`(38), `DBManager/SrvUserAuthM.cs`(24) — 총 776줄

---

## 문서 03 정정 — MongoDB는 ECS를 쓰지 않는다

문서 03에서 `Arch`(ECS) 참조 파일을 3개로 집계하며 `MongoDBManagerM.cs`를 포함시켰다. **정확하지 않다.**

`MongoDBManagerM.cs:1`의 `using Arch.Core;`는 **사용되지 않는 불필요한 using**이다. 파일 어디에도 `Entity`나 ECS API가 없다. 같은 파일에 이런 잉여 using이 넷이나 있다.

```csharp
using Arch.Core;                              // 미사용
using Microsoft.CodeAnalysis.CSharp.Syntax;   // 미사용 (Roslyn)
using System.Windows.Forms;                   // 미사용
using ZstdSharp.Unsafe;                       // 미사용
```

→ **ECS를 실제로 쓰는 파일은 `HierachyM.cs`와 `BoxColliderM.cs` 2개뿐이다.**
→ `System.Windows.Forms`가 DB 파일에까지 퍼져 있다 — 크로스 플랫폼 오염의 범위가 문서 03에서 파악한 것보다 넓다.

---

## 구조

```
DBManagerM  (싱글턴, LazyInitializer)
   └─ MongoDBManagerM  (IDisposable)          연결·풀 설정, 컬렉션 캐시
        └─ MongoDBCollectionM<T>              타입별 CRUD + 재시도
             where T : IDB_DataM              { ObjectId DB_OBJECT_ID }
```

`SrvUserAuthM : IDB_DataM` — 유일한 실제 엔티티. `{ ObjectId DB_OBJECT_ID; string id; string hashedPw; }`

---

## `MongoDBManagerM`

`DBManager/MongoDBManagerM.cs:30`

### 동작

**연결 풀 설정이 실전 튜닝되어 있다** (`:38~56`)

| 설정 | 값 | 의도 |
|---|---|---|
| `MaxConnectionPoolSize` | 500 | 높은 동시성 |
| `MinConnectionPoolSize` | 50 | 충분한 초기 연결 (콜드스타트 회피) |
| `MaxConnectionIdleTime` | 10분 | |
| `WaitQueueTimeout` | 10초 | 풀 고갈 시 대기 상한 |
| `ConnectTimeout` | 5초 | |
| `SocketTimeout` | 60초 | |
| `ServerSelectionTimeout` | 3초 | 빠른 실패 |

> **기본값을 그대로 두지 않고 워크로드에 맞춰 조정했다.** 특히 `MinConnectionPoolSize = 50`으로 초기 연결을 확보하고 `ServerSelectionTimeout = 3초`로 빠른 실패를 택한 것은 게임 서버에 맞는 판단이다. **이 설정 목록 자체가 Phase 13의 참고 자료다.**

**API 표면** — 모두 제네릭 + `where T : IDB_DataM`
`InsertAsync`, `GetAsync`(ObjectId / 필터+프로젝션), `GetOrCreateAsync`(ObjectId / 필터+프로젝션), `UpsertAsync`, `UpdateAsync`, `HasAasync`(오타: Aasync), `GetOrCreateCollection<T>`

`IsMongoDBConnectedAsync(connectionString)` (`:64`) — `admin` DB에 `ping` 명령. 헬스체크용

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **컬렉션 캐시 키가 `typeof(T).Name`** — 짧은 이름이라 **네임스페이스가 다른 동명 타입이 충돌**한다. 충돌하면 `(MongoDBCollectionM<T>)` 캐스팅에서 `InvalidCastException` | `:100~101` | 🔴 높음 |
| 2 | **`Dispose`에 `Thread.Sleep(1000)`** — 진행 중 작업이 취소 신호를 받도록 1초 블로킹 대기. 서버 종료가 그만큼 늦어지고, 실제로 완료를 보장하지도 않는다 | `:225` | 🟠 중간 |
| 3 | **`_cts`를 모든 연산이 공유**한다. 개별 요청 취소가 불가능하고, 하나를 취소하면 전부 취소된다 | `:35`, `:114` 등 | 🟠 중간 |
| 4 | 잉여 using 4개 (`Arch.Core`, `Roslyn`, `WinForms`, `ZstdSharp`) | `:1~15` | 🟠 중간 |
| 5 | `HasAasync` 오타 (`Has` + `Aasync`) — public API 이름 | `:189`, `:202` | 🟡 낮음 |
| 6 | `IsMongoDBConnectedAsync`가 호출마다 새 `MongoClient`를 만든다. `MongoClient`는 무겁고 재사용 대상이다 | `:75` | 🟠 중간 |
| 7 | 인덱스 생성·관리 API가 없다. 쿼리가 컬렉션 스캔을 하는지 알 수 없다 | 전체 | 🟠 중간 |
| 8 | 트랜잭션(세션) 지원이 없다. 다중 문서 원자성 불가 | 전체 | 🟠 중간 |

---

## `MongoDBCollectionM<T>`

`DBManager/MongoDBManagerM.cs:268`

### 동작

`InsertAsync`(단건/리스트), `HasAsync`, `DeleteAsync`, `GetAllAsync`, `GetOrCreateAsync`, `UpsertAsync`, `GetAsync`, `UpdateAsync`

**원자적 get-or-create** — MongoDB 관용구를 정확히 사용한다.
```csharp
var options = new FindOneAndUpdateOptions<T> {
    IsUpsert = true,                        // 없으면 생성
    ReturnDocument = ReturnDocument.After   // 갱신 후 문서 반환
};
var findData = await _collection.FindOneAndUpdateAsync(filter, updateDef, options, ct);
```
> **조회-없으면-삽입을 두 번의 왕복으로 나누지 않고 서버 측 원자 연산 하나로 처리한다.** 경쟁 조건이 없다. 올바른 선택이다.

**재시도 루프** — `maxRetries = 3`, 예외 필터 + 지수 백오프

### 🔴 재시도가 실제로 동작하지 않는다

```csharp
catch (MongoException ex) when (i < maxRetries)
{
    if (i < maxRetries - 1)
    {
        Debug.WriteLine(...);
        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프
    }
    throw;      // ← if 블록 밖. 백오프 후에도 무조건 던진다
}
```

`OperationCanceledException` 핸들러는 `else throw`로 올바르게 작성됐는데, **`MongoException` 핸들러는 `throw`가 `if` 밖에 있어 항상 실행된다.**

→ **MongoDB 예외(연결 실패·타임아웃·일시적 오류)에 대한 재시도가 한 번도 일어나지 않는다.** 지연만 넣고 실패한다. 재시도 코드를 작성했으나 **의도대로 동작하지 않는다.**

### 그 외 재시도 설계 문제

| # | 문제 | 심각도 |
|---|---|---|
| 1 | 🔴 **`MongoException` 재시도가 `throw` 위치 오류로 무효** (위) | 🔴 치명 |
| 2 | 🔴 **`OperationCanceledException`을 재시도한다.** 취소는 일시적 장애가 아니다 — 호출자가 명시적으로 취소한 것이므로 **즉시 전파해야** 한다. 백오프하며 재시도하면 취소가 무시된다 | 🔴 높음 |
| 3 | **`Task.Delay`에 `ct`를 넘기지 않는다** — 백오프 중에는 취소가 먹지 않는다 | 🟠 중간 |
| 4 | **쓰기 연산 재시도의 멱등성을 고려하지 않았다.** `updateDef`가 `$inc`류면 재시도 시 **중복 적용**된다 | 🔴 높음 |
| 5 | `when (i < maxRetries)` 필터는 루프 안에서 **항상 참**이라 무의미. 실제 게이트는 안쪽 `if (i < maxRetries - 1)` | 🟡 낮음 |
| 6 | 백오프가 1초 → 2초. 게임 서버 요청 하나가 **3초 이상 블로킹**될 수 있다 | 🟠 중간 |
| 7 | 오류를 `Debug.WriteLine`으로만 남긴다 — Release에서 소멸 | 🟠 중간 |
| 8 | `catch (Exception ex) { Debug.WriteLine(...); throw; }` — 로그만 찍고 던지는 래핑이 반복 | 🟡 낮음 |

---

## `DBManagerM` (싱글턴)

`DBManager/DBManagerM.cs:11`

`LazyInitializer.EnsureInitialized(ref _instance, ref _initialized, ref _syncLock, () => new DBManagerM())` — 스레드 안전한 지연 싱글턴. 패턴 자체는 올바르다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **하드코딩된 DB 이름 `"TangDB"`** + `SrvGlobal.gDbConnectionString`(하드코딩 자격증명, 문서 01) | `:22` | 🔴 높음 |
| 2 | **초기화 로그가 `"###...짜장###"`** — 프로덕션 로그에 의미 없는 문자열 | `:23` | 🟠 중간 |
| 3 | 싱글턴이라 **테스트에서 교체 불가**. DI가 이미 참조돼 있는데(문서 03) 쓰지 않았다 | `:26~36` | 🟠 중간 |
| 4 | `IDisposable`을 구현하지 않아 `_mongoDbMgr`가 정리되지 않는다 | 전체 | 🟠 중간 |
| 5 | `using ZstdSharp.Unsafe;` 잉여 | `:7` | 🟡 낮음 |

---

## `SrvUserAuthM`

`DBManager/SrvUserAuthM.cs:11`

```csharp
public class SrvUserAuthM : IDB_DataM
{
    public ObjectId DB_OBJECT_ID { get; set; }
    public string id;
    public string hashedPw;
}
```

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **`[BsonElement]` 등 매핑 속성이 없다.** 필드명 변경이 곧 **스키마 변경**이 되어 기존 문서를 읽지 못한다. 마이그레이션 경로 없음 | 🔴 높음 |
| 2 | `id`, `hashedPw`가 **public 필드**(프로퍼티 아님). Bson 직렬화는 되지만 캡슐화 없음 | 🟠 중간 |
| 3 | `id`에 **유니크 인덱스가 없다** — 코드 어디에도 인덱스 생성이 없다. 로그인 조회가 컬렉션 스캔이고, 동시 가입 시 중복 계정이 생길 수 있다 | 🔴 높음 |
| 4 | 생성 시각·최종 로그인 등 감사 필드가 없다 | 🟡 낮음 |
| 5 | `using FbsClassM;` 잉여 | 🟡 낮음 |

> 문서 01에서 확인한 `LoadingUserAuthDbAsync`의 `GetOrCreate` 동작(없는 계정 자동 생성)과 **유니크 인덱스 부재**가 겹치면, 동시에 같은 ID로 접속한 두 요청이 **두 개의 계정 문서**를 만들 수 있다.

---

## 이 계층의 종합

| 항목 | 판정 | Phase |
|---|---|---|
| **연결 풀 튜닝 설정 목록** | 🟢 승계 (참고값) | 13 |
| **`FindOneAndUpdate` + `IsUpsert`로 원자적 get-or-create** | 🟢 승계 | 13 |
| **제네릭 컬렉션 파사드 + 타입별 캐시** | 🟢 승계 (개념) | 13 |
| **`IDB_DataM` 마커 인터페이스** | 🔵 참고 | 13 |
| 재시도 로직 | 🟡 개작 (버그 수정 + 멱등성) | 10·13 |
| `MongoDBManagerM` 구현 | 🟡 개작 | 13 |
| `DBManagerM` 싱글턴 | 🔴 폐기 (DI로) | 2·13 |
| 하드코딩된 연결 문자열·DB명 | 🔴 폐기 | 9 |

### 새 코드에 절대 옮기면 안 되는 것

1. `MongoDBManagerM.cs:439~448` — **`MongoException` 재시도의 `throw`가 `if` 밖에 있어 재시도가 무효**
2. `MongoDBManagerM.cs:430~437` — **`OperationCanceledException`을 재시도** (취소는 일시적 장애가 아니다)
3. 재시도 시 **쓰기 멱등성 미고려** (`$inc`류가 중복 적용될 수 있음)
4. `MongoDBManagerM.cs:100` — **`typeof(T).Name`을 캐시 키로** (네임스페이스 충돌 → `InvalidCastException`)
5. `MongoDBManagerM.cs:225` — **`Dispose`의 `Thread.Sleep(1000)`**
6. `DBManagerM.cs:22` — **하드코딩된 DB명 + 자격증명**
7. `SrvUserAuthM` — **Bson 매핑 속성 없음, 유니크 인덱스 없음**
8. `MongoDBManagerM.cs:1~15` — **잉여 using 4개** (특히 `System.Windows.Forms`가 DB 계층까지)

### Phase 13 설계에 반영할 것

- **인덱스를 코드로 관리한다.** 시작 시 `CreateIndexes` 실행 + 존재 검증. 인덱스 없는 쿼리를 개발 빌드에서 경고
- **스키마 매핑을 명시**한다(`[BsonElement]`). 필드명 리팩터링이 데이터를 깨지 않게
- **재시도는 정책 객체로 분리**하고 **멱등 연산에만** 적용한다. 취소는 재시도하지 않는다 (Phase 10 서킷 브레이커와 통합)
- 연결 문자열·DB명은 **옵션 + 시크릿 저장소** (Phase 2·9)
- `ISessionStore` / 영속화 어댑터는 **DI로 주입**해 테스트에서 인메모리 구현으로 교체 가능하게 (Phase 2·13)
