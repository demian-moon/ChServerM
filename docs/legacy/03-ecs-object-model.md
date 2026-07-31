# 03 — ECS 오브젝트 모델 / 공간·충돌

**대상**: `HierachyM.cs`(1631), `BoxColliderM.cs`(567), `MathM.cs`(173) — **전량 정독 완료**

---

## 🔴 이 계층에서 드러난 아키텍처 사실

### 레거시는 ECS(Arch) 기반이다

`HierachyM.cs:1` — `using Arch.Core;`

네임스페이스 `EcsServerLibM`의 **"Ecs"가 Entity Component System**이었다. 파일명·폴더명만 보고 분류하던 초기 인벤토리가 이것을 놓쳤다.

**단, ECS는 전면 도입이 아니다.** `Arch`를 참조하는 파일은 **3개뿐**이다.

| 파일 | ECS 사용 |
|---|---|
| `HierachyM.cs` | `Entity`, `entity.Get<T>()` — 컴포넌트 정의와 접근 |
| `BoxColliderM.cs` | 충돌 컴포넌트 |
| `DBManager/MongoDBManagerM.cs` | (확인 필요 — 영속화에 Entity가 섞였을 가능성) |

네트워크·패킷·세션 계층(문서 01·02)은 ECS와 무관하게 전통적 OOP다. 즉 **ECS는 게임 오브젝트 계층에만 국소 적용**된 상태다.

### `ServerM.csproj` 전체 의존성

| 패키지 | 용도 | ChServerM 판정 |
|---|---|---|
| `Arch` | ECS | 🔵 선택 축 후보 (Part V) |
| `Google.FlatBuffers` | 직렬화 | 🟡 페이로드 전용으로 격리 (ADR-0002) |
| `K4os.Compression.LZ4` | 압축 | 🟢 `IPayloadCodec` 후보 (Phase 9) |
| `System.IO.Pipelines` | I/O | 🟢 승계 |
| `Microsoft.Extensions.DependencyInjection` | **DI가 이미 있다** | 🟢 승계 (Phase 2) |
| `Microsoft.Extensions.Identity.Core` | 비밀번호 해싱 | 🔵 `IAuthenticator` 참고 (Phase 9) |
| `MongoDB.Driver` | 영속화 | 🔵 어댑터 후보 (Phase 13) |
| `log4net` | 로깅 | 🔴 폐기 → ZLogger (Phase 11) |
| `Microsoft.CodeAnalysis.CSharp` | Roslyn 스크립팅 | 🔴 폐기 (하드 룰 위반) |
| **`System.Windows.Forms`** | ⚠️ **서버 라이브러리가 WinForms를 참조** | 🔴 폐기 |

> **`System.Windows.Forms` 참조가 크로스 플랫폼 불가의 근본 원인이다.** 문서 01·02에서 발견한 UI 스레드 코드(`IUIThreadCheck`, `_dicIsMemPkUiThread`, `TaskScheduler.FromCurrentSynchronizationContext()`)와 `ScreenLibM`, `ProgressBarM`이 모두 여기서 나온다. Linux 배포가 애초에 불가능한 구조였다.
>
> 또한 `Microsoft.Extensions.DependencyInjection`이 **이미 참조돼 있다.** DI 도입은 새로운 시도가 아니라 레거시가 시작했다가 완성하지 못한 방향이다 (실제 코드는 static 전역에 의존).

### 공간 분할(QuadGrid)은 구현되어 있지 않다

**이전 판정을 정정한다.** `docs/LEGACY-INVENTORY.md`와 ROADMAP Phase 18에 "`QuadTreeM.cs` 판정 필요 / AOI 승계 후보"로 적었으나, 전수 검색 결과:

```
BasicLibM/QuadTreeM.cs:3   // QuadGrid 참고 할 것        ← 빈 파일, 메모만
HierachyM.cs:988           /// Bounds를 가지며 LQuadTree 상에 ... 등록되어 있다
HierachyM.cs:1030          /// LQuadTree를 갱신(Update)해야 한다
HierachyM.cs:1180          public List<SparseSetM<Entity>> referQuadGrids;
HierachyM.cs:1189          referQuadGrids = new List<SparseSetM<Entity>>();
```

**`QuadGrid` / `LQuadTree`라는 타입이 코드베이스에 존재하지 않는다.** 주석과 빈 리스트 필드만 있다. `referQuadGrids`는 초기화되지만 **채우는 코드가 없다.**

→ 공간 분할·AOI는 **승계할 구현이 없다.** Phase 18은 처음부터 설계해야 한다. 남은 것은 `SparseSetM<Entity>`를 그리드 셀의 멤버십 저장소로 쓰려던 **의도**뿐이다 (이 발상 자체는 타당하다 — 문서 05에서 `SparseSetM` 정독 후 판정).

---

## 컴포넌트 (ECS value types)

### `PositionM` / `RotationM` / `SizeM`

`:788`, `:910`, `:1044` — 모두 `struct : IEquatable<T>`, `float X, Y, Z`

`PositionM`: `V3` 프로퍼티(`Vector3` 변환), 연산자 `+`/`-`/`==`/`!=`, `SetPos(angle, distance)`(극좌표 이동), `GetHashCode`(17/23 소수 조합)
`RotationM`: `GetAngle() => X` — **X를 각도로 쓴다** (2D이므로 Y·Z는 미사용)
`SizeM`: `SetSize` 오버로드 3개

**문제점**

| # | 문제 | 위치 |
|---|---|---|
| 1 | `SizeM.SetSize(in Vector3 size)` — **본문이 비어 있다.** 주석 `//needPkUpdateFlag = true; // 필요한지 검토 해야 함`만. **조용한 no-op** | `:1086~1089` |
| 2 | `GetHashCode`가 `float.GetHashCode()` 조합 — 부동소수를 딕셔너리 키로 쓰는 것은 위험 (0.0/-0.0, NaN) | `:892`, `:972`, `:1091` |
| 3 | `Vector3`(12B)를 쓰면서 별도 `PositionM`(12B)을 둔 이유가 불명. `Vector3`는 SIMD 가속을 받는다 | 설계 |
| 4 | `PositionM.SetPos(Vector3 rotation, double distance)`가 `rotation.X`만 쓴다 — 시그니처가 오해를 유발 | `:862` |

**개선점**: ECS 컴포넌트는 **blittable**이어야 한다. `System.Numerics.Vector3`를 직접 컴포넌트로 쓰면 SIMD 가속 + 표준 API를 얻는다. 별도 래퍼를 만들 이유가 없다.

**판정** 🟡 **개작** — 개념은 표준 타입으로 대체. → Phase 17·18

---

### `ImgSizeM`

`:990` — 바운딩 박스 컴포넌트. `RectM cachedBounds`, `PositionM cachedPosForImgSize`, `BaseGameObjM _owner`, `float _angle`, `SizeM _obbSize`

**동작**: `Bounds` 프로퍼티가 owner 위치와 캐시된 위치를 비교해 **변했을 때만** `cachedBounds.ChangeCenter()`를 호출한다 — 위치 기반 무효화 캐시.
`RotationObb(angle)`은 회전 후 바운딩 박스 크기를 다시 계산한다(`MathM.GetBoundingSizeAfterRotation`).

**문제점**

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`_angle` 누적이 버려진다.** `_angle += angle;` 직후 `_angle = MathM.NormalizeAngle(angle);` — **`_angle`이 아니라 `angle`을 정규화**한다. 누적값이 매번 파괴됨 | `:1035~1036` | 🔴 버그 |
| 2 | **ECS 컴포넌트 struct가 클래스 참조(`BaseGameObjM _owner`)를 보유** — blittable이 깨지고 ECS 캐시 지역성이 무의미해진다. ECS의 존재 이유를 부정 | `:995` | 🔴 높음 |
| 3 | 프로퍼티 getter(`Bounds`)가 상태를 변경한다 (캐시 갱신) | `:1012~1026` | 🟠 중간 |

**개선점**: 컴포넌트에서 owner 역참조를 제거하고, 위치는 시스템이 `PositionM` 컴포넌트를 함께 조회해 전달한다(ECS 정석). 캐시 무효화는 dirty 플래그 컴포넌트로.

**판정** 🟡 **개작** (캐시 무효화 발상은 승계) → Phase 18

---

### `NeedPkSendM` — 🟢 델타 전송 dirty 플래그

`:1108`

```csharp
public bool CheckNeedPkSend => DirChange || Stopping || ImgRotate || LocatePos || MovingStart;

public bool Stopping   { get; set; } = true;  // 멈춤
public bool MovingStart{ get; set; }          // 멈췄다 다시 움직임
public bool ImgRotate  { get; set; }          // 이미지 회전
public bool DirChange  { get; set; }          // 방향 전환
public bool LocatePos  { get; set; }          // 위치 변경
void Reset()                                  // 전송 후 초기화
```

> **이것이 "스냅샷/델타 압축"(ROADMAP Phase 18)의 실체다.** 매 틱 전체 상태를 브로드캐스트하지 않고, **변경 종류별 플래그**를 세워 필요할 때만 보낸다. 플래그를 5종으로 세분화해 어떤 변화인지까지 구분한다.

**문제점**: 플래그가 `bool` 프로퍼티 5개 = 최소 5바이트. 비트 필드(`byte` 하나 + 상수)로 하면 1바이트. 컴포넌트 크기는 ECS에서 캐시 라인 효율에 직결된다.

**판정** 🟢 **승계** (설계). 비트 플래그로 압축해 재구현. → Phase 18

---

### `LastMoveTickM` — 🟢 이동 보정

`:1206`

`lastMoveTick`, `NeedMoveFlag`, `NeedGridUpdate`(= `NeedMoveFlag || needGridUpdate`), `AlreadyMoved`(= `lastMoveTick != 0`)
`GetElapsedTickAfterMoved(curTick)` → `curTick - lastMoveTick` (0이면 0)

> **의도** (`:1203` 주석): *"fixedUpdate 전에 먼저 움직인것 처리… 그 tick을 저장해 놨다가 업데이트 때 너무 많이 움직이지 않도록 보정"*
>
> 즉 **유저 입력에 의한 즉시 이동과 고정 틱 시뮬레이션의 이중 적용을 방지**하는 장치다. 실시간 서버에서 반드시 필요한 문제 인식이고, 해법도 타당하다.

**문제점**: `NeedGridUpdate` getter가 두 필드를 OR하지만 setter는 하나만 쓴다 — 비대칭이라 `NeedGridUpdate = false`가 실제로 false를 보장하지 않는다 (`NeedMoveFlag`가 true면 여전히 true).

**판정** 🟢 **승계** (문제 인식과 해법). → Phase 17 (틱)

---

### `LastServerTickSendM`

`:20` — `IsSendTime(curTick)` = `lastServerTickSend == 0 || curTick - lastServerTickSend >= serverTickSendInterval`

틱 전송 주기 스로틀. 단순하고 올바르다.
**문제점**: `struct` 기본 생성자로 만들면 `serverTickSendInterval == 0` → 매 틱 전송. 검증 없음.

**판정** 🟢 **승계** → Phase 17

---

### `ObjBasicDataM` / `SrvUserDataM` / `TeamNumberM` / `ObjScriptM` / `MapScriptM`

| 타입 | 내용 | 판정 |
|---|---|---|
| `ObjBasicDataM` (`:1173`) | `int objType`(1=유저,2=몬스터,3=총알,4=아이템), `long Oid`, `string Name`, `int idxCreateId`, `List<SparseSetM<Entity>> referQuadGrids` | 🟡 개작 |
| `SrvUserDataM` (`:1193`) | `SrvUserM srvUser` — 세션을 ECS 컴포넌트로 | 🟡 개작 |
| `TeamNumberM` (`:1145`) | `TEAM_NUMBER { OUR_SIDE, ENEMY }` | 🔴 폐기 |
| `ObjScriptM` (`:1412`) | `ScriptForGameObjM script` | 🔴 폐기 |
| `MapScriptM` (`:1457`) | `AbScriptM script` | 🔴 폐기 |

**문제점**

| # | 문제 | 심각도 |
|---|---|---|
| 1 | `ObjBasicDataM`이 `string`, `List<>`를 보유 — **blittable 아님**. `ImgSizeM`과 같은 문제 | 🔴 높음 |
| 2 | `objType`이 **매직 넘버 int** (주석으로만 의미 설명). enum이어야 한다 | 🟠 중간 |
| 3 | **`TEAM_NUMBER`가 2팀 고정** — 도메인 가정이 프레임워크에 박혔다. 4팀·FFA·PvE는 표현 불가 | 🔴 높음 |
| 4 | **스크립트가 컴포넌트로 박혀 있다** (`ObjScriptM`, `MapScriptM`). Roslyn 동적 컴파일이 오브젝트 모델과 분리 불가능하게 결합 → 하드 룰("리플렉션·동적 컴파일 금지")을 지키려면 **오브젝트 모델을 다시 설계**해야 한다 | 🔴 높음 |

**개선점**: 프레임워크 컴포넌트에서 도메인 가정(팀 수, objType 분류)을 제거한다. 팀·진영은 앱이 정의하는 컴포넌트로 (Part V는 프리미티브만 제공, ADR-0004).

---

## 도형 / 충돌

### `IShapeM`

`:46` — `Contains(IShapeM)`, `Intersects(IShapeM)`, `GetAxes()`, `ProjectOntoAxis(Vector3)`, `Center`, `Points`

**SAT(Separating Axis Theorem)** 기반 충돌 판정 계약. `GetAxes` + `ProjectOntoAxis`가 SAT의 두 축.

**문제점**: 인터페이스가 `Vector3[]`(힙 배열)을 반환한다 — 충돌 검사마다 배열 접근 + 가상 호출. 그리고 `IShapeM`을 struct가 구현하므로 **`otherShape is RectM` 패턴 매칭마다 박싱**이 발생한다.

**판정** 🟡 **개작** — SAT 알고리즘은 승계, 인터페이스 기반 다형성은 제거(제네릭 + `where T : struct, IShape` 또는 태그 유니온).

---

### `RectM` (AABB)

`:404` — `struct : IShapeM`. `X, Y, Width, Height`, `Vector3[] _points`(시계방향 4점), `Vector3 _center`, `readonly Vector3[] _axes`

메서드: `Contains(IShapeM/RectM/Vector3/PositionM)`, `Intersects(IShapeM/RectM)`, `ProjectOntoAxis`, `OffsetWithinSize`, `Offset`, `ChangeCenter`, `ReCalcRectWithinSize`, `ChangeSize`, `SetSize`, `ChangeSizeInMax`, `GetBoundingRectAfterRotation`, `GetAxes`, `Rotate`

**문제점**

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`struct`가 배열 2개를 인스턴스 필드로 보유.** `_points`(4개) + `_axes`(2개). **`RectM` 하나 만들 때마다 힙 할당 2회.** `Offset`/`ReCalcRect`/`GetBoundingRectAfterRotation`이 모두 `new RectM(...)`을 반환하므로 충돌·이동 계산마다 할당이 터진다 | `:411`, `:419`, `:443` | 🔴 치명 |
| 2 | **`readonly Vector3[] _axes = { ... }` 필드 초기자** — struct의 필드 초기자는 **모든 생성자에서 실행**된다. 모든 인스턴스가 동일한 상수 배열을 각자 할당한다. `static readonly`여야 한다 | `:419` | 🔴 높음 |
| 3 | **좌표 오타 버그.** `RectM(in PositionM, w, h)` 생성자의 4번째 점이 `new Vector3(Y + Width, Y, 0)` — **`X + Width`여야 한다** | `:462` | 🔴 버그 |
| 4 | **`ChangeSize` 오타 버그.** `float halfYsize = addSubXsize / 2.0f;` — **`addSubYsize`여야 한다.** Y 방향 크기 변경이 X 값을 쓴다 | `:662` | 🔴 버그 |
| 5 | `Rotate(float)`가 `throw new NotImplementedException()` — public API가 항상 던진다 | `:772~775` | 🟠 중간 |
| 6 | 생성자가 `Math.Max(1, width)` — **폭 0을 허용하지 않는다.** 그런데 `_center`는 `Math.Ceiling(width/2)`로 원본 width를 쓴다 → width=0일 때 center와 실제 사각형이 불일치 | `:437~441` | 🟠 중간 |
| 7 | `Contains(in Vector3)`가 Right·Top을 배타로 처리 — 주석에 명시돼 있지만 `Intersects`는 다른 경계 규칙을 쓴다. **경계 규칙 불일치** | `:554`, `:574` | 🟠 중간 |

**개선점**
- **배열을 제거한다.** 4점은 `Vector2` 4개 필드 또는 `min/max` 2개로 표현. AABB는 `(Vector2 Min, Vector2 Max)`면 충분하고 8~16바이트에 들어간다
- SAT 축은 `static readonly` 또는 컴파일 타임 상수
- 경계 규칙(포함/배타)을 **하나로 통일하고 문서화**. 게임 로직 버그의 고전적 원인

**판정** 🟡 **개작** — AABB·SAT 수식은 승계, 자료구조는 전면 재작성. → Phase 18

---

### `QuadPointBoundM` (회전 가능 사각형)

`:58` — `struct : IShapeM`. `Vector3[] _points`(4점), `bool bAxisAligned`, `readonly Vector3[] _axesAxisAligned`, `Vector3[] _axes`(지연 생성)

**동작**: 축 정렬 상태면 AABB 빠른 경로(`AABBIntersects`), 아니면 SAT 전체 경로.
`Rotate(angle)`은 중심 기준 회전 후 `ResetAxes()`로 캐시 무효화.
`Contains(Vector3)`는 사각형을 **두 삼각형으로 분할**해 무게중심 좌표(barycentric)로 판정.

**문제점**

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **반환값 누락 버그.** 축 정렬 quad vs 축 정렬 quad 분기에서 `AABBIntersects(this, quad);` — **`return`이 없다.** 아래로 흘러 `return false`(`:174`)에 도달한다. **축 정렬된 두 사각형은 절대 충돌하지 않는다고 판정된다** | `:138~141` | 🔴 치명 버그 |
| 2 | **`struct`가 배열 3개** (`_points`, `_axesAxisAligned`, `_axes`). `_axesAxisAligned`는 필드 초기자라 **인스턴스마다 할당** | `:60`,`:66`,`:68` | 🔴 치명 |
| 3 | **`Contains(Vector3)`의 매직 `-1`.** `p1.Y -= 1; p2.X -= 1; p2.Y -= 1; p3.X -= 1;` — 경계를 배제하려고 좌표에서 1을 뺀다. **정수 격자를 가정**한 코드로, float 좌표계에서는 의미가 없다 | `:331~338` | 🔴 높음 |
| 4 | `bAxisAligned`를 설정하는 코드가 없다 — 생성자에서 초기화하지 않으므로 **항상 `false`**. `IsAxisAlignedBoundingBox()` 메서드는 있지만 아무도 호출하지 않는다. **빠른 경로가 죽어 있다** | `:63`, `:71~77` | 🔴 높음 |
| 5 | `GetAxes()`가 `_axes`를 지연 생성해 캐시하지만, `struct`이므로 **복사본에 캐시된다** → 값이 복사될 때마다 재계산. 캐시가 사실상 동작하지 않음 | `:178~196` | 🟠 중간 |
| 6 | `Center`가 `{ get; private set; }` 자동 프로퍼티인데 생성자에서 Z를 0으로 고정 — 3D 확장 불가 | `:74~83` | 🟡 낮음 |
| 7 | `Intersects`의 SAT 루프가 두 도형의 축을 각각 순회하며 `GetAxes()`를 호출 — 매 호출 배열 반환 | `:144~172` | 🟠 중간 |

**개선점**
- **버그 #1·#4를 합치면 회전 사각형 충돌이 사실상 검증되지 않은 상태다.** 새 구현은 **단위 테스트 필수** (축 정렬/회전/포함/접선 케이스)
- `struct` + 배열 조합을 버린다. 4점은 고정 필드 또는 `InlineArray`(C# 12+)
- SAT는 `Span<Vector2>` 기반 무할당 구현으로
- 경계 판정은 **epsilon 기반**으로. 정수 `-1` 트릭 제거

**판정** 🟡 **개작** — SAT 구조는 승계, 구현은 전량 재작성 + 테스트 필수. → Phase 18

---

## 오브젝트 기반 클래스

### `BaseGameObjM`

`:1269` — `abstract class : IHasGameOid`

`string name`, `Entity entity`, `TimeEventSchedulerM ExpireJobScheduler => ServerM.gTimeScheduler`, `HashM _hashM`(지연 생성)

**ECS 접근 패턴** — `ref` 반환으로 컴포넌트 저장소를 직접 가리킨다. 복사 없음.
```csharp
public ref PositionM GetPos() {
    ref var pos = ref entity.Get<PositionM>();
    return ref pos;
}
```

**해시 API**: `SetHash(key, value, durationSec = -1)`, `RemoveHash`, `HasHash`, `GetHash`, `GetHashAndRemove` → 모두 `HashM`에 위임

> **`HashM`은 해시 함수가 아니다.** `durationSec` 만료를 지원하는 **키-값 저장소**이며 `TimeEventSchedulerM`이 만료를 구동한다. Redis의 `HSET`/`EXPIRE`에 대응하는 오브젝트별 상태 백. 이전 인벤토리에서 `HashM.cs`를 "보안/해시"로 분류한 것은 **오류**다 → 문서 08(세션)로 재배치.

**문제점**

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`Oid` getter에 부작용** — `_oid == 0`이면 `MakeOid()`를 호출해 상태를 변경한다. 읽기만 할 것으로 기대되는 프로퍼티가 쓰기를 한다. 멀티스레드에서 **같은 객체가 두 개의 OID를 발급받을 수 있다** (`MakeOid`는 `Interlocked`지만 검사-발급이 원자적이지 않음) | `:1358~1368` | 🔴 높음 |
| 2 | `ExpireJobScheduler`가 `ServerM.gTimeScheduler`(static 전역)에 직결 — 테스트 격리 불가, 스케줄러 교체 불가 | `:1274` | 🟠 중간 |
| 3 | `GetRotation()`이 `private`인데 `ref` 반환 — 외부는 `GetAngle()`만 쓸 수 있어 회전 쓰기 경로가 없다 | `:1392` | 🟡 낮음 |
| 4 | `Clear()`가 `_oid = 0`으로 초기화 → 다음 `Oid` 접근이 **새 OID를 발급**한다. 객체 재사용(풀링) 시 의도된 동작이나 명시되지 않음 | `:1346~1350` | 🟠 중간 |
| 5 | `public string name` — 필드 직접 노출, 캡슐화 없음 | `:1271` | 🟡 낮음 |

**개선점**
- **`ref` 컴포넌트 접근은 승계.** ECS를 쓰든 안 쓰든 "복사 없이 상태 접근"은 옳은 방향
- OID는 **생성 시점에 확정**한다. 지연 발급 + getter 부작용 제거. `readonly` 강타입 ID (Phase 1)
- 스케줄러는 주입받는다 (`IClock`/`ITimerScheduler`, Phase 1)
- **오브젝트별 만료 KV 저장소(`HashM`)는 유용한 프리미티브다** → `ISessionStore`와 별개로 "엔티티 상태 백" 축으로 검토 (Phase 13)

**판정** 🟡 **개작** → Phase 1, 13, 17

---

### `AbScriptableForGameObjM`

`:1427` — `BaseGameObjM` 상속. `Script`, `ref Collider`(`QuadPointColliderM`), `ref ImgSize`, `SetTriggerObject(bool)`

Unity의 `OnTriggerEnter` 스타일 충돌 메시지를 흉내낸 계층.

**판정** 🔴 **폐기** — 스크립트 결합 때문. 충돌 이벤트 개념(`IsTrigger`)만 🔵 참고. → Phase 18

---

### `MapObjM`

`:1486` — `abstract class : IHasGameOid, IDisposable`

`long _oid`, `static long _uinqueOid`, `Entity mapEntity`, `ProgressBarM _progressBar`(지연 생성 + **풀에서 대여**), `HashM _hashM`(지연 생성)

**강제 구현**: `SendPacketToMapUsers(pkType, data, exceptOid)`, `WriteSendBufferToMapUsers(...)`
→ **맵 단위 브로드캐스트**가 오브젝트 모델의 계약으로 들어와 있다. ROADMAP Phase 18의 "룸/존 브로드캐스트"에 대응.

`Dispose(bool)`에서 `ProgressBarM.ProgressBarFactory.ReturnToPool(_progressBar)` — **`ProgressBarM`은 풀링되는 게임 오브젝트 컴포넌트**다. 콘솔 UI가 아니다(→ 이전 판정 정정 필요, 문서 12에서 재검토).

**문제점**

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`_progressBar`가 null이어도 `ReturnToPool`을 호출한다.** 지연 생성이므로 한 번도 접근하지 않은 맵은 null | `:1598` | 🔴 높음 |
| 2 | **OID 센티넬 불일치** — `MapObjM.Clear()`는 `_oid = -1`, `BaseGameObjM.Clear()`는 `_oid = 0` | `:1622` vs `:1349` | 🟠 중간 |
| 3 | `static long _uinqueOid` (오타: uinque) — 프로세스 전역 카운터 | `:1491` | 🟡 낮음 |
| 4 | `Dispose` 후 `Clear()`를 부르면 `_progressBar = null`이지만 이미 풀에 반납된 상태 → **double-return 위험** | 구조적 | 🟠 중간 |
| 5 | `IDisposable`만 구현 (`IAsyncDisposable` 없음) — 브로드캐스트가 비동기인데 정리는 동기 | 설계 | 🟠 중간 |

**개선점**
- **맵/룸 단위 브로드캐스트 계약은 승계** — Phase 18의 룸 추상화 핵심. 단 "같은 페이로드를 N명에게 보낼 때 직렬화 1회"를 계약에 넣는다
- 풀 반납은 소유권 타입으로 강제 (Phase 3)
- `IAsyncDisposable` + 정리 순서 명시

**판정** 🟢 **승계** (맵 브로드캐스트 계약) / 🟡 **개작** (구현) → Phase 18

---

## 콜라이더 (`BoxColliderM.cs`)

Unity의 `Collider` + `OnTriggerEnter/Stay/Exit` / `OnCollisionEnter/Stay/Exit` 모델을 그대로 이식했다.

### 구조

```
IColliderM { AbScriptableForGameObjM Owner; IShapeM Bounds; bool IsTrigger; long OnStayEventDelayTick }
├─ QuadPointColliderM : IColliderM   (회전 사각형, QuadPointBoundM 사용)
└─ BoxColliderM       : IColliderM   (AABB, RectM? 사용)

CollisionM   — 충돌 정보 컨테이너 (class). _collider + ContactPos
ContactPoint — 접촉점(struct). _point, _normal, _thisCollider, _otherCollider
```

**이벤트 생성 알고리즘** (`CollisionEventGenerate`, `:172`/`:414`)
```
_curCollisionObjs = {}                      // 이번 프레임 충돌 집합
foreach (entity in entityList):
    _curCollisionObjs.Add(entity)
    if entity ∉ _lastCollisionObjs:  → Enter 이벤트
    else:                            → Stay 이벤트 (딜레이 검사)
foreach (entity in _lastCollisionObjs):
    if !entity.IsAlive(): continue           // 이미 Destroy된 엔티티 방어
    if entity ∉ _curCollisionObjs:  → Exit 이벤트
_lastCollisionObjs = _curCollisionObjs
_curCollisionObjs = new HashSet<Entity>()
```

> **Enter/Stay/Exit 집합 차분 알고리즘은 정석이다.** 이전 프레임 집합과 현재 프레임 집합의 차집합으로 Exit를, 여집합으로 Enter를 구한다. `IsAlive()`로 파괴된 엔티티를 방어하는 것도 옳다. **이 알고리즘은 승계 가치가 있다.**

`OnStayEventDelayTick`(기본 `Stopwatch.Frequency / 10` = 0.1초)로 Stay 이벤트를 스로틀한다. 그리고 **트리거끼리는 Stay를 발생시키지 않는다**(`collider.IsTrigger == false`일 때만) — 주석: *"유저끼리는 발생 안시킴"*.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`Rotate`가 아무 효과가 없다.** `((QuadPointBoundM)Bounds).Rotate(angleDegree);` — `Bounds`는 `IShapeM`을 반환하는 **프로퍼티**다. struct를 인터페이스에서 캐스팅하면 **언박싱 복사본**이 만들어진다. 복사본을 회전시키고 버린다. `AngleDegree`만 갱신되고 **실제 도형은 회전하지 않는다** | `:82~86` | 🔴 치명 버그 |
| 2 | **`BoxColliderM`의 위치 갱신이 동작하지 않는다.** `cachedBounds`가 `RectM?`(Nullable)이므로 `cachedBounds.Value`는 **복사본**을 반환한다. `cachedBounds.Value.ChangeCenter(ownerPos)`는 복사본의 중심을 바꾸고 버린다. 그런데 `cachedPosForCollider = ownerPos`로 **캐시 키는 갱신**되므로 다시는 갱신을 시도하지 않는다 → **콜라이더가 최초 위치에 영원히 고정된다** | `:333~337` | 🔴 치명 버그 |
| 3 | **`ContactPoint` 계산이 수학적으로 틀렸다.** `_point = Vector3.Abs(centerA - centerB) / 2f` — 두 중심 차의 **절댓값**을 반으로 나눈 것은 접촉점이 아니다(항상 1사분면 벡터가 된다). `_normal = Normalize(_point)`도 따라서 항상 양수 성분만 갖는다. 두 중심이 같으면 `Normalize(0)` → **NaN** | `:546~547` | 🔴 높음 |
| 4 | **`QuadPointColliderM`과 `BoxColliderM`이 약 240줄씩 완전 중복.** `CheckCollisionEventInHashSet`, `GenCollisionEvent`, `CollisionEventGenerate`, 이벤트 메서드 6종이 전부 동일 | 전체 | 🔴 높음 |
| 5 | **`struct`가 `HashSet<Entity>` 2개를 필드 초기자로 보유** → 콜라이더 인스턴스마다 HashSet 2개 할당. 게다가 `CollisionEventGenerate` 끝에서 **매 프레임 `new HashSet<Entity>()`** → 엔티티 N개면 프레임당 N개 할당 | `:45~47`, `:218`, `:294~296`, `:460` | 🔴 높음 |
| 6 | **`_enabled` 프로퍼티를 아무도 검사하지 않는다.** 콜라이더 비활성화 기능이 동작하지 않음 | `:36`, `:286` | 🟠 중간 |
| 7 | `IsCollision`이 `Bounds.Intersects(other.Bounds)` — 인터페이스로 struct를 반환하므로 **충돌 검사마다 박싱 2회** | `:105~108`, `:344~347` | 🟠 중간 |
| 8 | **도달 불가 코드.** `GenCollisionEvent`의 `if/else`가 모두 return하는데 아래에 `return 0;` (CS0162 경고) | `:168`, `:410` | 🟡 낮음 |
| 9 | 반환값이 **매직 넘버 int**(0~6). 주석으로만 의미를 설명. enum이어야 한다 | `:134~141` | 🟠 중간 |
| 10 | `static long STAY_EVENT_DELAY_TICK`이 `readonly`가 아님 — 가변 static | `:41`, `:290` | 🟡 낮음 |
| 11 | struct의 프로퍼티 초기자(`OnStayEventDelayTick = STAY_EVENT_DELAY_TICK`)와 명시적 생성자의 상호작용은 C# 버전에 민감하다. 명시적으로 대입하지 않으면 값이 0이 되어 **Stay 딜레이가 무력화**될 수 있다 | `:42`, `:291` | 🟠 중간 |
| 12 | 모든 충돌 이벤트가 `Script`를 경유하고, `OnTriggerEnter/Stay`는 **스크립트가 직접 패킷을 보낸다**(`SendTriggerEnterPacketToUsers`). 충돌 → 스크립트 → 패킷이 하드와이어 | `:249~261` | 🔴 높음 |
| 13 | `AngleDegree`가 `QuadPointColliderM`에만 있고 `BoxColliderM`에는 없다 — 비대칭 | `:79` | 🟡 낮음 |

### 개선점 (ChServerM)

- **Enter/Stay/Exit 집합 차분 알고리즘은 승계.** 단 `HashSet` 재할당 대신 **두 개의 풀링된 버퍼를 스왑**하고 `Clear()`로 재사용. 프레임당 할당 0
- **struct + 인터페이스 조합을 버린다.** 제네릭 특수화(`where T : struct, IShape`) 또는 태그 유니온으로 박싱·언박싱 제거. #1·#2 버그가 애초에 발생 불가능해진다
- **`Nullable<struct>` 캐시 금지** — `.Value`가 복사본을 반환하는 함정. `bool hasValue` + 필드로 분리
- 접촉점·법선은 **SAT의 최소 침투 축(MTV)** 으로 정식 계산 (Phase 18)
- 충돌 이벤트에서 **패킷 전송을 분리**. 이벤트는 이벤트만 발생시키고, 전송은 별도 시스템이 dirty 플래그(`NeedPkSendM`)를 보고 처리 (Phase 18)
- 반환값을 `enum CollisionEventKind`로

### 판정

🟡 **개작** — 집합 차분 알고리즘과 Stay 스로틀은 승계, 구현은 전량 재작성. **버그 #1·#2 때문에 회전과 위치 갱신이 실제로 동작한 적이 없다** → 재작성 시 단위 테스트 필수.

→ Phase 18

---

## `MathM` / `MortonCodeM` (`MathM.cs`)

### `MathM` (static)

| 멤버 | 내용 |
|---|---|
| `Deg2Rad` | `Math.PI / 180.0` |
| `GetAngleDegreesToTargetPos(src, tgt)` | `Atan2` → **-180~180도** |
| `GetDistanceBetweenPos(...)` | 2D 유클리드 거리 (`Vector3`/`PositionM` 오버로드) |
| `GetAnglePosAtDistance(pos, angle, dist)` | 극좌표 → 직교좌표 |
| `NormalizeAngle(angle)` | `angle % 360`, 음수면 +360 → **0~360도** |
| `IsPointInTriangle(pt, a, b, c)` | 무게중심 좌표 판정 |
| `GetBoundingSizeAfterRotation(angle, w, h)` | 회전 후 AABB 크기 |

### `MortonCodeM` (static) — 🟢 유일하게 살아남은 공간 인덱싱 자산

**Z-order curve(모튼 코드)** 구현.

```csharp
EncodeMorton2(x, y) => (Part1By1(y) << 1) + Part1By1(x)

Part1By1(x):            // 16비트를 32비트에 1칸씩 벌려 배치
    x &= 0x0000ffff;
    x = (x ^ (x << 8)) & 0x00ff00ff;
    x = (x ^ (x << 4)) & 0x0f0f0f0f;
    x = (x ^ (x << 2)) & 0x33333333;
    x = (x ^ (x << 1)) & 0x55555555;

MortonIndex2(point, minX, minY, width, height):
    정규화 → pX = UInt16.MaxValue * (x-minX) / width  → EncodeMorton2(pX, pY)
```

> **폐기된 QuadGrid 작업에서 유일하게 남은 실제 구현이다.** 모튼 코드는 2D 좌표를 **공간 지역성이 보존되는 1차원 키**로 바꾼다. 가까운 좌표는 가까운 키가 되므로 정렬된 배열/B-트리로 범위 질의를 할 수 있고, 쿼드트리의 셀 인덱스로도 쓴다. 비트 트릭 구현도 정석(매직 상수 주석까지 정확)이다.
>
> **Phase 18(관심 영역)의 출발점으로 승계한다.**

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`using System.Drawing;` + `PointF`** — `System.Drawing.Common`은 .NET 6+에서 **Windows 전용**이다. Linux에서 `PlatformNotSupportedException`. `System.Windows.Forms`와 함께 **크로스 플랫폼을 막는 두 번째 의존** | `:2`, `:158` | 🔴 높음 |
| 2 | **각도 규약이 일관되지 않는다.** `GetAngleDegreesToTargetPos`는 **-180~180**, `NormalizeAngle`은 **0~360**을 반환한다. 두 값을 섞어 쓰면 조용히 틀린다 | `:19`, `:57` | 🔴 높음 |
| 3 | **`MortonIndex2`에 경계 검증이 없다.** `width`/`height`가 0이면 **0으로 나누기** → `Infinity` → `(UInt32)` 캐스팅 결과 미정의. 좌표가 범위를 벗어나면 `pX`가 16비트를 넘고 `Part1By1`의 `& 0x0000ffff`가 **조용히 잘라내** 엉뚱한 셀로 매핑 | `:158~170` | 🔴 높음 |
| 4 | `IsPointInTriangle`이 `QuadPointBoundM.PointInTriangle`(`HierachyM.cs:344`)과 **완전 중복** | `:71` | 🟡 낮음 |
| 5 | `GetDistanceBetweenPos`가 `double` 연산 — `Vector2.Distance`(SIMD)로 대체 가능 | `:29~38` | 🟡 낮음 |
| 6 | `GetBoundingSizeAfterRotation`이 `RectM.GetBoundingRectAfterRotation`(`HierachyM.cs:749`)과 로직 중복 | `:95` | 🟡 낮음 |

### 개선점

- **`System.Drawing` 제거.** `PointF` → `Vector2`
- **각도 규약을 하나로 고정하고 타입으로 강제.** `readonly struct Degrees` / `Radians`로 단위를 타입에 새기면 혼용이 컴파일 에러가 된다 (Phase 1 ID/값 타입 규약과 같은 발상)
- `MortonIndex2`에 **범위 클램프 + 0 나누기 방어**. 그리드 크기를 2의 거듭제곱으로 강제하면 나눗셈이 시프트가 된다
- 64비트 모튼(`EncodeMorton2` → `ulong`)으로 확장해 좌표 해상도를 32비트씩 확보 검토
- `Bmi2.ParallelBitDeposit`(x86 PDEP) 하드웨어 가속 검토 — `System.Runtime.Intrinsics.X86`. 소프트웨어 폴백 유지 (Phase 12에서 벤치마크)

### 판정

🟢 **승계** (`MortonCodeM`) / 🟡 **개작** (`MathM`)
→ Phase 1 (각도 타입), Phase 18 (공간 인덱싱)

---

## 이 계층의 종합 판정

| 항목 | 판정 | Phase |
|---|---|---|
| **`NeedPkSendM` 델타 전송 dirty 플래그** | 🟢 승계 | 18 |
| **`LastMoveTickM` 이동 이중적용 방지** | 🟢 승계 | 17 |
| **`LastServerTickSendM` 틱 전송 스로틀** | 🟢 승계 | 17 |
| **맵 단위 브로드캐스트 계약(`MapObjM`)** | 🟢 승계 | 18 |
| **`ref` 컴포넌트 접근 (복사 없는 상태 접근)** | 🟢 승계 | 17·18 |
| **만료 지원 오브젝트별 KV(`HashM` 연동)** | 🟢 승계 | 13 |
| **`MortonCodeM` Z-order 공간 인덱싱** | 🟢 승계 | 18 |
| **Enter/Stay/Exit 집합 차분 알고리즘** | 🟢 승계 | 18 |
| **Stay 이벤트 스로틀 (0.1초 딜레이)** | 🟢 승계 | 18 |
| SAT 충돌 알고리즘 | 🟡 개작 | 18 |
| 콜라이더 (`QuadPointColliderM`/`BoxColliderM`) | 🟡 개작 | 18 |
| `MathM` 기하 유틸 | 🟡 개작 | 1·18 |
| `System.Drawing` (`PointF`) 의존 | 🔴 폐기 | — |
| AABB `RectM` | 🟡 개작 | 18 |
| ECS 컴포넌트 타입 (`PositionM` 등) | 🟡 개작 | 17·18 |
| `BaseGameObjM` / `MapObjM` | 🟡 개작 | 1·13·18 |
| `TEAM_NUMBER` 2팀 고정 | 🔴 폐기 | — |
| 스크립트 컴포넌트 (`ObjScriptM`/`MapScriptM`) | 🔴 폐기 | — |
| `System.Windows.Forms` 의존 | 🔴 폐기 | — |
| QuadGrid / AOI | **구현 없음** | 18 (신규 설계) |

### 새 코드에 절대 옮기면 안 되는 것 + 미수정 버그

1. `HierachyM.cs:138~141` — **축 정렬 quad vs quad 충돌이 항상 false** (`return` 누락)
2. `HierachyM.cs:63` — **`bAxisAligned`가 설정되는 곳이 없어 빠른 경로가 죽어 있음**
3. `HierachyM.cs:462` — **`new Vector3(Y + Width, Y, 0)`** (`X + Width`여야 함)
4. `HierachyM.cs:662` — **`halfYsize = addSubXsize / 2.0f`** (`addSubYsize`여야 함)
5. `HierachyM.cs:1035~1036` — **`_angle` 누적이 매번 파괴됨**
6. `HierachyM.cs:1086~1089` — **`SizeM.SetSize(Vector3)`가 조용한 no-op**
7. `HierachyM.cs:1598` — **null `_progressBar`를 풀에 반납**
8. `HierachyM.cs:419`, `:66` — **struct 필드 초기자로 상수 배열을 인스턴스마다 할당**
9. `HierachyM.cs:1358~1368` — **`Oid` getter의 부작용 + 경쟁 조건**
10. `HierachyM.cs:331~338` — **float 좌표계에 정수 `-1` 경계 트릭**
11. `BoxColliderM.cs:82~86` — **`Rotate`가 언박싱 복사본을 회전시켜 효과 없음**
12. `BoxColliderM.cs:333~337` — **`Nullable<RectM>.Value`가 복사본이라 위치 갱신 실패 + 캐시 키만 갱신되어 영구 고정**
13. `BoxColliderM.cs:546~547` — **접촉점·법선 계산이 수학적으로 틀림 (`Abs` 오용, `Normalize(0)` → NaN)**
14. `BoxColliderM.cs:218`, `:460` — **매 프레임 `new HashSet<Entity>()`**
15. `BoxColliderM.cs:36`, `:286` — **`_enabled`를 아무도 검사하지 않음** (콜라이더 비활성화 무동작)
16. `MathM.cs:2`, `:158` — **`System.Drawing.PointF`** (.NET 6+ Windows 전용)
17. `MathM.cs:158~170` — **`MortonIndex2`에 0 나누기·범위 초과 방어 없음** (조용한 셀 오매핑)
18. `MathM.cs:19` vs `:57` — **각도 규약 불일치** (-180~180 vs 0~360)

> **충돌 계층 전체에 미수정 버그가 8개 있다** (#1·#2·#3·#4·#10·#11·#12·#13).
> 종합하면 — **회전은 적용되지 않고**(#11), **`BoxColliderM`은 최초 위치에 고정되며**(#12), **축 정렬 빠른 경로는 죽어 있고**(#2), **축 정렬 quad끼리는 절대 충돌하지 않으며**(#1), **접촉점은 무의미한 값**(#13)이다.
>
> 즉 **이 충돌 시스템은 실제로 검증된 적이 없다고 보아야 한다.** Phase 18 재작성 시 **단위 테스트를 먼저 쓴다.** 승계하는 것은 알고리즘 구조(SAT, 집합 차분, Stay 스로틀, 모튼 코드)이지 코드가 아니다.

### 크로스 플랫폼 차단 요인 (종합)

레거시가 Linux에서 돌 수 없는 이유는 3개다. 전부 이 계층 또는 그 의존에서 나온다.

| 의존 | 출처 | 영향 |
|---|---|---|
| `System.Windows.Forms` | `ServerM.csproj` | UI 스레드 디스패치(`IUIThreadCheck`, `FromCurrentSynchronizationContext`), `ScreenLibM`, `ProgressBarM` |
| `System.Drawing` (`PointF`) | `MathM.cs` | .NET 6+ Windows 전용 |
| `IOControlCode.KeepAliveValues` | `NetWorkM.cs` (문서 01) | Windows 전용 소켓 옵션 |

ChServerM의 CI가 **ubuntu + windows 매트릭스**인 것은 이 문제를 구조적으로 재발시키지 않기 위한 장치다.

### 정량 근거 (ADR용)

| 항목 | 레거시 | ChServerM 목표 |
|---|---|---|
| `RectM` 1개 생성 | **힙 할당 2회** (`_points` 4 + `_axes` 2) | **0회** (`Vector2 Min/Max` 16B) |
| `QuadPointBoundM` 1개 생성 | **힙 할당 2~3회** | **0회** (`InlineArray` 또는 고정 필드) |
| 충돌 검사 1회 | 인터페이스 가상 호출 + 박싱 + 배열 접근 | 제네릭 특수화, 무할당 |
| ECS 컴포넌트 blittable | ❌ (`string`/`List`/클래스 참조 혼입) | ✅ 필수 |
