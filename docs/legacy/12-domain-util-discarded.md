# 12 — 도메인 / 유틸 / 폐기 확정

**참조 분석 기반 판정.** 이 문서의 대상은 대부분 **어디서도 호출되지 않는 코드**다. 전량 정독 대신 **판정에 필요한 만큼 읽고 근거를 기록**한다.

---

## 🔴 참조 0인 코드 목록

전수 검색(`grep -rl` 후 자기 파일 제외) 결과다.

| 파일 | 줄수 | 참조 | 비고 |
|---|---:|---:|---|
| `BasicLibM/ExcelLibM/ExcelLibM.cs` | 2166 | 0 | 문서 11 |
| `BasicLibM/ExcelLibM/ExcelODBCM.cs` | 927 | 0 | 문서 11 |
| `RatingSystem/WengLinM.cs` | 626 | **0** | 아래 |
| `Script/ScriptM.cs` | 457 | 간접만 | 아래 |
| `BasicLibM/UI/ProgressBarM.cs` | 424 | 2 | 아래 |
| `BasicLibM/BigIntM.cs` | 307 | **0** | |
| `RatingSystem/GlickoM.cs` | 301 | **0** | 아래 |
| `PublicUtil/ScreenLibM/ScreenLibM.cs` | 205 | **0** | |
| `RoslynCompilerM.cs` | 198 | **0** | 아래 |
| `BasicLibM/HangulM/HangulM.cs` | 226 | **0** | |
| `BasicLibM/CsvParser.cs` | 182 | 0 | 문서 11 |
| `BasicLibM/Pool/MemoryPoolM.cs` | 134 | **0** | 아래 ⚠ |
| `Script/ScriptUtilM.cs` | 85 | **0** | |
| `PublicLib/FileM/FileWatcherSystemM.cs` | 84 | 0 | 문서 11 |
| `BasicLibM/RegM.cs` | 79 | **0** | |
| `BasicLibM/StackMemAllocM.cs` | 49 | **0** | 아래 ⚠ |
| `BasicLibM/JiraLibM/` (14 파일) | ~700 | **0** | |
| `BasicLibM/etc/unity관련/` (7 파일) | ~900 | **0** | |
| `Unused/` (7 파일) | ~1900 | **0** | 아래 |

**합계 약 9,000줄** — `LegacyServer` 26,000줄의 **35%가 어디서도 호출되지 않는다.**

---

## ⚠ 이전 판정 정정 — 버퍼 풀링은 승계할 구현이 없다

`docs/LEGACY-INVENTORY.md`와 ROADMAP **Phase 3(메모리·버퍼)** 에 다음을 "승계 후보"로 기재했다.

> Phase 3 버퍼 | `BasicLibM/Pool/MemoryPoolM.cs`, `ObjectPoolM.cs`, `StackMemAllocM.cs`, `Memory/UnsafeCopyBlock.cs`

**전수 검색 결과 이 중 실제로 쓰이는 것은 `ObjectPoolM<T>` 하나뿐이다.**

| 파일 | 실제 상태 |
|---|---|
| `Pool/ObjectPoolM.cs` (32줄) | ✅ **사용 중** — `TimingWheelSlotM`의 노드 풀, `TimeEventSchedulerM`의 리스트 풀 (문서 04) |
| `Pool/MemoryPoolM.cs` (134줄) | ❌ **참조 0.** `AbConcurrentObjPoolM<T>`, `ConcurrentObjPoolM<T>`, `AbObjPoolM<T>` 정의만 있고 아무도 쓰지 않는다 |
| `StackMemAllocM.cs` (49줄) | ❌ **참조 0.** `unsafe ref struct StackMemAllocM<T> where T : unmanaged` — 스택 할당 래퍼. 정의만 존재 |
| `Memory/UnsafeCopyBlock.cs` (29줄) | ❌ **전체 주석 처리** (문서 초반에 확인) |

> **결론: 레거시의 버퍼·메모리 관리에서 승계할 것은 사실상 없다.**
> 실제 풀링은 전부 **`ArrayPool<byte>.Shared` 직접 호출**로 이루어지고, 그 호출들이 문서 01·02·06에서 확인한 **반납 누수의 근원**이다.
>
> `StackMemAllocM<T>`가 `unsafe ref struct` + `unmanaged` 제약으로 **올바른 방향**을 잡고 있었다는 점만 🔵 참고한다. Phase 3의 소유권 타입 설계에서 이 형태(`ref struct` 스코프)를 쓴다.

### `ObjectPoolM<T>` (유일한 실사용 풀)

`BasicLibM/Pool/ObjectPoolM.cs:13`

```csharp
public sealed class ObjectPoolM<T> where T : class, new()
{
    private readonly ConcurrentQueue<T> _objects = new();
    private readonly Func<T> _generator;
    public T Get() => _objects.TryDequeue(out var item) ? item : _generator();
    public void Return(T item) => _objects.Enqueue(item);
    public void Clear() => _objects.Clear();
}
```

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **상한이 없다.** 무한히 성장한다. `TimingWheelSlotM`이 슬롯 3,000개마다 하나씩 보유하므로(문서 04) 폭주한 슬롯은 노드를 영원히 붙든다 | 🔴 높음 |
| 2 | `Return`에 **null 검사도 중복 반납 검사도 없다.** 같은 객체를 두 번 반납하면 풀에 두 개가 들어가고, 두 소비자가 같은 인스턴스를 받는다 | 🔴 높음 |
| 3 | `Return` 시 상태 초기화를 강제하지 않는다 (호출자 책임) | 🟠 중간 |

**판정** 🟡 **개작** — 상한·중복 반납 검사·초기화 강제를 추가. → Phase 3

---

## 🔴 스크립트 시스템 — 간접 결합만 남아 있다

| 파일 | 줄수 | 상태 |
|---|---:|---|
| `Script/ScriptM.cs` | 457 | `AbScriptManM`, `ScriptForGameObjM`, `AbScriptM`, `ICollisionEventM` 정의 |
| `Script/ScriptUtilM.cs` | 85 | 참조 0 |
| `RoslynCompilerM.cs` | 198 | **참조 0** |

`ScriptForGameObjM` / `AbScriptM` / `ICollisionEventM`은 `HierachyM.cs`(`ObjScriptM`, `MapScriptM` 컴포넌트)와 `BoxColliderM.cs`(충돌 이벤트)가 **타입으로 참조**한다. 그러나:

- **`RoslynCompilerM`(동적 컴파일 엔진)은 어디서도 호출되지 않는다**
- 즉 **스크립트를 실제로 컴파일·로드하는 경로가 없다.** 타입 정의와 컴포넌트 결합만 남았고, 실행 엔진은 연결되지 않았다

> **이는 폐기 판정을 더 쉽게 만든다.** 문서 03에서 *"스크립트가 컴포넌트로 박혀 있어 오브젝트 모델을 다시 설계해야 한다"*고 기술했는데, **실제 동적 컴파일은 배선되지 않았으므로** 제거 비용이 예상보다 낮다. 타입 참조만 걷어내면 된다.

**판정** 🔴 **폐기 확정**. 하드 룰("리플렉션·동적 컴파일 금지", Native AOT 불가) 위반이고, 실행 경로도 없다.
→ 재도입이 필요하면 **AOT 호환 대안**(사전 컴파일 플러그인)을 별도 ADR로 (ROADMAP 백로그에 이미 기재)

---

## 게임 도메인 — 레이팅

| 파일 | 줄수 | 참조 |
|---|---:|---:|
| `RatingSystem/GlickoM.cs` | 301 | **0** |
| `RatingSystem/WengLinM.cs` | 626 | **0** |

**Glicko**(개인 레이팅, 불확실성 RD 포함)와 **Weng-Lin**(팀 기반 베이지안 레이팅, TrueSkill 계열)의 구현. 927줄.

**어디서도 호출되지 않는다.** 매치메이킹 시스템이 존재하지 않으므로 레이팅만 미리 구현해 둔 상태다.

> **문서 03·ADR-0003 관련**: 이 두 파일이 "목표 워크로드 = 게임 서버"의 근거 중 하나였다. 실제로는 **사용되지 않는 준비 코드**였다. ADR-0004(워크로드는 프로필이다)로 이미 정정됐으므로 판정에 영향은 없다.

**판정** 🔵 **참고**. 알고리즘 구현 자체는 가치가 있다. **Phase 19(매치메이킹·레이팅)에서 참조**하되, 프레임워크가 아니라 **`Samples/` 또는 선택 패키지**에 둔다 (ADR-0004: 도메인 로직은 프레임워크가 아니다).

---

## `ProgressBarM` — 문서 03 판정 확정

`BasicLibM/UI/ProgressBarM.cs`(424, 참조 2)

문서 03에서 *"콘솔 UI가 아니라 풀링되는 게임 오브젝트 컴포넌트"*로 정정했다. 참조 2건은 `MapObjM`(문서 03)과 `FsProgressBarFactory`(문서 02)다.

- `ProgressBarM.ProgressBarFactory.GetProgressBar()` / `ReturnToPool()` — 풀링
- `FbsProgressBar` 패킷으로 **서버가 클라의 진행 바를 제어**한다 (`barType`, `visible`, `title`, `barText`, `gage`, `maxGage`, `x`, `y`)

> **서버가 클라이언트 UI 위젯을 원격 제어하는 기능**이다. 로딩·제작·채집 진행률 표시 같은 용도다.

**판정** 🔴 **폐기** (프레임워크 범위 밖). 특정 UI 위젯을 프레임워크가 정의할 이유가 없다. 앱이 자기 패킷으로 처리한다.
단 `FbsProgressBar` 스키마의 **"65535 = 변경 없음" sentinel**은 ADR-0002가 지적한 FlatBuffers 가변 길이 문제의 또 다른 사례로 기록한다(문서 02).

---

## 순수 유틸 (전부 참조 0)

| 파일 | 줄수 | 내용 | 판정 |
|---|---:|---|---|
| `BasicLibM/BigIntM.cs` | 307 | 임의 정밀도 정수 | 🔴 폐기 — `System.Numerics.BigInteger` 사용 |
| `BasicLibM/HangulM/HangulM.cs` | 226 | 한글 자모 분해·초성 검색 | 🔴 폐기 — 앱 관심사 |
| `BasicLibM/RegM.cs` | 79 | 정규식 래퍼 | 🔴 폐기 |
| `PublicUtil/ScreenLibM/ScreenLibM.cs` | 205 | 콘솔 화면 제어 | 🔴 폐기 — 서버 범위 밖 |
| `BasicLibM/StringBuilderM.cs` | 72 | `StringBuilder` 래퍼 (참조 1: `TimeM`) | 🔴 폐기 |
| `BasicLibM/JiraLibM/` (14 파일) | ~700 | Jira REST 클라이언트 | 🔴 폐기 — 서버 프레임워크와 무관 |
| `BasicLibM/etc/unity관련/` (7 파일) | ~900 | `MchUtil`, `CatmullRomSpline`, `TilePicking`, `AbGmaeObjectPool`(오타), `MchMementoPattern`, `PlayMakerVariableUtil`, `MchSingleTone` | 🔴 폐기 — Unity 클라이언트 전용 |

> `JiraLibM/`은 별도 `.sln`과 `.csproj`(`v4.8`)까지 갖추고 있다 — 다른 프로젝트에서 통째로 복사해 온 것으로 보인다.

---

## `Unused/` — 이미 폐기된 코드 (7 파일, 약 1,900줄)

| 파일 | 크기 | 내용 |
|---|---:|---|
| `EcsSystemM.cs` | 20.9K | **ECS 시스템 계층 시도.** `Arch` 기반 시스템 스케줄링을 만들려다 접은 흔적 |
| `CmdActionM.cs` | 11.0K | 명령 패턴 액션 |
| `AbSingleExecuteM.cs` | 7.6K | 단일 실행 보장 추상 |
| `BitIdM.cs` | 7.4K | 비트 필드 ID |
| `CmdMachineM.cs` | 6.4K | 명령 상태 기계 |
| `ParallelExecuteM.cs` | 961B | 병렬 실행 |
| `CryptM.cs` | 938B | 암호 유틸 |

**판정** 🔴 **폐기**. 레거시 자신이 이미 폐기한 코드다.

> 다만 **`EcsSystemM.cs`(20.9K)의 존재가 의미 있다.** ECS 시스템 스케줄링 계층을 만들려다 접었다는 뜻이고, 그래서 문서 03에서 확인한 대로 **ECS가 컴포넌트 정의에만 머물고 시스템 계층이 없는** 상태가 됐다. Part V에서 실시간 프리미티브를 설계할 때 이 시도를 참고할 수 있다 — 다만 지금은 읽지 않는다.

---

## 🔴 최종 통계 — 레거시 26,000줄의 구성

| 분류 | 줄수 | 비율 |
|---|---:|---:|
| **승계·개작 대상** (문서 01~11에서 판정) | 약 11,600 | 45% |
| **미참조 코드** | 약 9,000 | 35% |
| **주석 처리된 코드** (활성 파일 내) | 약 2,900 | 11% |
| **명시적 폐기** (`Unused/`, Jira, Unity) | 약 2,500 | 9% |

> **실제로 동작하며 승계 가치가 있는 코드는 절반 이하다.**
> 그리고 그 절반 안에서도 문서 01~11이 **치명 버그 40건 이상**을 식별했다.
>
> 이것이 "레거시를 개선해서 옮긴다"가 아니라 **"설계를 승계하고 구현은 재작성한다"**가 옳은 결론인 이유다.

---

## 이 문서의 종합 판정

| 항목 | 판정 | Phase |
|---|---|---|
| `ObjectPoolM<T>` (유일 실사용 풀) | 🟡 개작 (상한·중복반납 검사) | 3 |
| `StackMemAllocM<T>` 형태 (`unsafe ref struct`) | 🔵 참고 (소유권 타입 설계) | 3 |
| Glicko / Weng-Lin 레이팅 알고리즘 | 🔵 참고 (Samples로) | 19 |
| `EcsSystemM` (폐기된 시스템 계층 시도) | 🔵 참고 (필요 시) | Part V |
| `MemoryPoolM`, `UnsafeCopyBlock` | 🔴 폐기 (미사용/주석) | 3 |
| 스크립트 시스템 3종 | 🔴 폐기 (하드 룰 + 미배선) | — |
| `ProgressBarM` | 🔴 폐기 (범위 밖) | — |
| 순수 유틸 7종 | 🔴 폐기 | — |
| `Unused/` 7 파일 | 🔴 폐기 | — |

### 이전 판정 정정 (이 문서에서 확정)

1. **버퍼 풀링 승계 후보가 실제로는 미사용이다.** `MemoryPoolM`·`StackMemAllocM`·`UnsafeCopyBlock` 전부 참조 0 또는 전체 주석. **Phase 3은 처음부터 설계해야 한다** — `ObjectPoolM<T>`(32줄)만 실사용
2. **동적 컴파일 엔진(`RoslynCompilerM`)이 배선되어 있지 않다.** 스크립트 제거 비용이 문서 03의 예상보다 낮다
3. **레이팅 시스템은 사용되지 않는 준비 코드다.** ADR-0003의 근거 중 하나였으나 ADR-0004로 이미 정정됨

### ROADMAP·인벤토리 반영 필요

| 문서 | 정정 내용 |
|---|---|
| `docs/LEGACY-INVENTORY.md` 4절 | Phase 3 버퍼 후보에서 `MemoryPoolM`·`StackMemAllocM`·`UnsafeCopyBlock` 제거 |
| ROADMAP Phase 3 | "레거시 승계" 전제 삭제 — 처음부터 설계 |
| ROADMAP Phase 14 | `FileWatcherSystemM` 핫 리로드 승계 기재 삭제 (문서 11) |
| ROADMAP Phase 18 | QuadGrid/AOI 승계 기재 삭제 (문서 03에서 이미 정정) |
| ROADMAP Phase 19 | 레이팅은 `Samples/` 배치로 명시 |
