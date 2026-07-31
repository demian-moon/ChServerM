# 10 — 시간 / 틱 / 타이머

**정독 완료** — `PublicUtil/TickTimeM.cs`(299), `PublicUtil/TimerM.cs`(163)
**구조 파악** — `BasicLibM/TimeM.cs`(183), `BasicLibM/DateTimeStartEndM.cs`(610) — 프레임워크 범위 밖으로 판정, 아래 사유 기재

---

## 🔴 시간 표현이 세 가지다

문서 04에서 스케줄러 4종을 발견하며 지적한 문제의 뿌리다.

| 표현 | 사용처 | 단위 | 기준점 |
|---|---|---|---|
| `Stopwatch.GetTimestamp()` | `TickTimeM.GTick`, `TimeEventSchedulerM`, `NetWorkDelayM`, `ClientTimeM` | 머신마다 다른 주파수 | 부팅 시각 (임의) |
| `TickTimeM.GTick` | `ConcurrentSchedulerM`, `PkObjM.LastPkRecvTick` | 위와 동일 (래퍼) | 동일 |
| `DateTime.UtcNow` | `ExpireEventConCurSchedulerM`, `DateTimeStartEndM` | 100ns, 실제 해상도 ~15.6ms | 절대 시각 |

**세 표현이 상호 변환될 때마다 오차와 의미 혼동이 발생한다.**
- `Stopwatch` 틱은 **절대 시각이 아니다** — 프로세스를 재시작하면 기준점이 바뀌고, 머신 간 비교가 불가능하며, 영속화하면 의미를 잃는다
- `DateTime.UtcNow`는 절대 시각이지만 **해상도가 나쁘고**(Windows 기본 15.6ms) NTP 보정으로 **뒤로 갈 수 있다**
- 두 표현을 섞으면 "만료 시각"이 무엇을 기준으로 하는지 불분명해진다

> **ChServerM은 이것을 Phase 1의 `IClock` 추상화로 해결한다.**
> - **단조 시각**(monotonic, 경과 측정용)과 **벽시계 시각**(wall clock, 절대 시각·영속화용)을 **타입으로 분리**한다
> - 내부 단위는 **고정 정수**(마이크로초 등). `Stopwatch.Frequency` 나눗셈을 제거해 머신 의존성과 정수 나눗셈 오차를 없앤다
> - 테스트에서 시간을 주입할 수 있게 한다 — 레거시는 `Stopwatch`를 직접 호출해 **타이밍 테스트가 불가능**하다

---

## `TickTimeM`

`PublicUtil/TickTimeM.cs:7`, `static` 유틸 (인스턴스 없음)

### 동작

| 멤버 | 내용 |
|---|---|
| `GTick` | `Stopwatch.GetTimestamp()` |
| `GTickMs` | `GetTimestamp() * 1000.0 / Frequency` |
| `GTickPerSec` / `GTickPerMs` | `Frequency` / `Frequency / 1000` |
| `MsToTick(ms)` / `SecToTick(sec)` | 변환 |
| `GTickToMs(tick)` / `GTickToSec(tick)` | 역변환 |
| `GetElapsedMs/Sec/Tick(preTick)` | 경과 측정 |
| `GetTickAfterMs(ms)` | 미래 틱 |

`ClientTimeM`(문서 05)이 이것을 상속해 서버 주파수 환산을 추가한다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`GTickMs`가 장기 실행에서 정밀도를 잃는다.** `GetTimestamp() * 1000.0`을 `double`로 계산하는데, 10MHz 머신이 1년 가동하면 약 3.15×10¹⁷ 로 **`double` 정수 정밀도 한계(2⁵³ ≈ 9×10¹⁵)를 넘는다.** 밀리초 단위가 뭉개진다 | `:19` | 🔴 높음 |
| 2 | **`GTickPerMs = Frequency / 1000` 정수 나눗셈.** `Frequency`가 1000의 배수가 아니면 오차가 누적된다 (문서 04 `TimeEventSchedulerM._ticksPerMs`와 동일 문제) | `:61` | 🟠 중간 |
| 3 | `GetTickAfterMs`가 **`double`을 반환**한다. 틱은 정수여야 하고, 호출자가 `long`으로 캐스팅하며 정밀도를 잃는다 | `:23~26` | 🟠 중간 |
| 4 | `GTickToSec`의 음수 검증이 `Debug.Assert` — Release에서 소멸 | `:91` | 🟠 중간 |
| 5 | 경과 시간 함수들이 음수를 **0으로 뭉갠다**(`GetElapsedSec`, `GetElapsedTick`). 시계 역행이나 버그를 감춘다 | `:40~43`, `:52~55` | 🟠 중간 |
| 6 | 전부 static — 테스트에서 시간 주입 불가 | 전체 | 🔴 높음 |

---

## `ElapsedTimeManM` — 🟢 인터벌 게이트

`PublicUtil/TickTimeM.cs:101`

```csharp
bool IsElapsed()              // 마지막 갱신 후 intervalMs 지났는가
void RefreshLastUpdateTime()  // 지금으로 갱신
int  GetLeftUpdateTimeMs()    // 남은 시간
long GetPastUpdateTick()      // 지난 틱
```

- `_lastUpdateTimeTick`이 0으로 시작 → **첫 호출은 항상 통과**(주석에 의도 명시)
- `_intervalMs <= 0`이면 항상 통과 (무제한 실행)
- 생성자에서 `_intervalTick = MsToTick(intervalMs)`를 **미리 계산**해 매 호출 변환을 피한다

> **"주기적으로만 실행" 패턴의 최소 구현이다.** 틱 루프에서 하위 작업의 실행 빈도를 개별 조절할 때 쓴다. 인터벌을 틱으로 미리 변환해 두는 것도 옳다.

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **스레드 안전하지 않다.** `_lastUpdateTimeTick`이 비휘발성 `long`. 두 스레드가 `IsElapsed`/`Refresh`를 동시에 하면 중복 실행 | 🟠 중간 |
| 2 | `IsElapsed()`와 `RefreshLastUpdateTime()`이 **분리되어 있어** 호출자가 갱신을 잊으면 매번 통과한다 | 🟠 중간 |
| 3 | `GetLeftUpdateTimeMs()`가 **음수를 반환**할 수 있다 (이미 지난 경우) | 🟡 낮음 |
| 4 | `_intervalMs`를 생성 후 변경할 수 없다 | 🟡 낮음 |

### 판정

🟢 **승계**. `TryConsume()` 형태(검사+갱신 원자적)로 다듬고 `Interlocked`로 스레드 안전하게. → Phase 17

---

## `ElapsedExecuteM<T>` / `ElapsedExecuteFuncAsyncM<T>`

`PublicUtil/TickTimeM.cs:183`, `:250`

`ElapsedTimeManM` + 실행 대상을 묶은 래퍼. 인터벌이 지났고 `CanExeCute()`가 참일 때만 실행한다.

- `ElapsedExecuteM<T>` — `abstract class`, `ImpExecute(T)` 상속 구현
- `ElapsedExecuteFuncAsyncM<T>` — `struct` + 델리게이트

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | 두 클래스가 **거의 동일**한데 하나는 상속, 하나는 델리게이트 — 통합 가능 | 🟠 중간 |
| 2 | `ElapsedExecuteFuncAsyncM`이 `struct`인데 **참조 필드 2개**(`ElapsedTimeManM`, 델리게이트)를 보유. struct로 만든 이점이 없다 | 🟠 중간 |
| 3 | 오타 `CanExeCute` (Execute) — public API | 🟡 낮음 |
| 4 | 실행 실패 시 `RefreshLastUpdateTime`을 호출하지 않아 **다음 틱에 즉시 재시도**한다. 실패가 계속되면 매 틱 재시도 (백오프 없음) | 🟠 중간 |

### 판정

🟡 **개작** — 개념 승계, 하나로 통합. → Phase 17

---

## `TimerM<T>` / `ITimerActionM`

`PublicUtil/TimerM.cs:28`

### 동작

`ConcurrentDictionary<T, System.Threading.Timer>` 래퍼. 키(`TIMER_TYPE`)별로 타이머를 관리한다.

```csharp
void AddOrUpdateTimer(T key, ITimerActionM action, TimeSpan due, TimeSpan period)
void ChangeTimer(T key, TimeSpan due, TimeSpan period)
void RemoveTimer(T key)      // Dispose 포함
void DisposeAllTimer()
```

`TIMER_TYPE` enum은 **40000부터 시작** — `PACKET_TYPE`과 같은 규약(앱이 그 아래를 쓴다).
`DISCONNECT_USER_FORCE`, `HEART_BIT_SEND`, `HEART_BIT_ALIVE_CHECK`, `SERVER_TICK_SEND`, `MAP_TICK_SCRIPT`, `MAP_UPDATE`, `MON_TICK_SCRIPT`, `TIME_SCHEDULER`

`InnerUserM`(문서 06)이 `dicTimer`로 보유하고, `AbNetworkBase.gDisconnectTimer`(문서 01)가 전역으로 하나 더 있다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`ConcurrentDictionary.AddOrUpdate`의 팩토리가 여러 번 호출될 수 있다.** 경합 시 `new Timer(...)`가 **여러 개 생성되고 하나만 저장**된다. 나머지는 **Dispose되지 않은 채 계속 발화**한다 — 고전적인 `ConcurrentDictionary` + `IDisposable` 함정 | `:43~50`, `:60~64` | 🔴 치명 |
| 2 | 🔴 **`ChangeTimer`의 add-factory가 `null`을 반환한다** → 딕셔너리에 **null이 저장**된다. 이후 `RemoveTimer`/`DisposeAllTimer`가 `tmTimer.Dispose()`에서 **`NullReferenceException`** | `:76~81`, `:100`, `:114` | 🔴 치명 |
| 3 | 🔴 **타이머 콜백이 `Task`를 버린다.** `((ITimerActionM)obj).DoAction();` — 반환된 `Task`를 await하지도 관측하지도 않는다. **`DoAction` 내부 예외가 조용히 사라진다** | `:48`, `:62` | 🔴 높음 |
| 4 | `RemoveTimer`가 실패할 때마다 `Debug.WriteLine("지우려는 타이머가 없음요...")` — 정상 흐름인데 로그 | `:104` | 🟡 낮음 |
| 5 | `ITimerActionM.DoAction()`이 `Task`를 반환하지만 `TimerM`은 동기 콜백에서 호출한다 — 계약 불일치 | `:129` | 🟠 중간 |
| 6 | `System.Threading.Timer`는 **타이머마다 스레드풀 작업**을 만든다. 유저 수만큼 타이머를 두면 비용이 크다 — 이미 우월한 `TimeEventSchedulerM`(타이밍 휠)이 있는데 병행 사용된다 | 전체 | 🟠 중간 |

### `TimerM_User_Disconnect_Force` (`:136`)

강제 종료 타이머의 최종 단계. `_user.Tc.Close()`.
`async Task DoAction()`에 `await`이 없다(경고).

### 판정

🔴 **폐기**. **`TimeEventSchedulerM`(계층적 타이밍 휠, 문서 04)로 통합한다.**
- 타이밍 휠은 삽입 O(1)·틱당 O(1)이고 스레드풀 타이머를 커넥션 수만큼 만들지 않는다
- 키별 타이머 관리(`TIMER_TYPE`) 개념은 🟢 **승계** — 강타입 `JobId`(Phase 1)와 결합

→ Phase 8 (스케줄러 통합), Phase 17

---

## `TimeM` / `TestSec` — 성능 측정 유틸

`BasicLibM/TimeM.cs`

`StartTimeCheck` / `EndTimeCheck` / `LapTimeCheck` / `TimeCheckStatic` / `GetTimeResult` — `List<DateTime>`에 시점을 쌓아 경과를 출력하는 **수동 프로파일링 도구**.
`TestSec`은 `userNum = 5000` 같은 상수를 둔 **부하 테스트 계측** 보조.

### 판정

🔴 **폐기**. ChServerM은 **BenchmarkDotNet**(마이크로) + **NBomber**(부하) + **OpenTelemetry 메트릭**(런타임)으로 대체한다(Phase 11·12). 수동 계측 코드를 제품에 남기지 않는다.

> 다만 이 파일의 존재가 **"성능을 측정하려는 시도는 있었으나 도구가 없어 직접 만들었다"**는 사실을 보여준다. Phase 0에서 `Bench/` 골격을 먼저 세우기로 한 결정이 옳았다는 방증이다.

---

## `DateTimeStartEndM` / `DateTimeStartEndGroupM`

`BasicLibM/DateTimeStartEndM.cs` (610줄)

### 동작

**시간 구간(interval) 연산 라이브러리**다. 서버 네트워크와 무관하다.

- `DateTimeStartEndM(start, end)` — 시간 구간 하나
- `GetOverlapTypeTo(other)` → `TIME_OVERLAP_TYPE { NONE, HEAD_OVERLAP, TAIL_OVERLAP, INCLUE, COVER, ERROR }` — **구간 겹침 분류**
- `Split(iCnt, intervalMin)` / `Split(percentList, intervalMin)` — 구간 분할
- `Add(second)` / `Sub(timeGroup)` — **구간 합집합·차집합**
- `DateTimeStartEndGroupM` — 구간 집합. `Sort`, `DistinctDateTimeStartEnd`(중복 제거·병합)
- `GetRandomPercent(iCnt, useRandomRate)` — 무작위 비율 분할

> 예약 시스템·근무 일정·이벤트 기간 관리에 쓰는 도메인 유틸이다. 겹침 6종 분류와 구간 대수(합/차)를 갖춘 것으로 보아 **특정 비즈니스 요구에서 나온 코드**다.

### 판정

🔴 **폐기** (프레임워크 범위 밖).

**전량 정독하지 않았다.** 판정에 필요한 만큼만 읽었고, 근거는 다음과 같다.
- `EcsServerLibM` 네임스페이스에 있으나 **네트워크·세션·틱 어디에서도 참조되지 않는다**
- `DateTime` 기반이라 서버의 `Stopwatch` 틱 체계와 접점이 없다
- 서버 프레임워크가 제공할 기능이 아니다 — 필요하면 **앱이 자체 보유**하거나 NuGet 패키지를 쓴다

향후 이 코드가 필요해지면 그때 정독한다. 지금은 **판정 근거만 기록**한다.

---

## 이 계층의 종합

| 항목 | 판정 | Phase |
|---|---|---|
| **인터벌 게이트 (`ElapsedTimeManM`)** | 🟢 승계 | 17 |
| **키별 타이머 관리 개념 (`TIMER_TYPE`)** | 🟢 승계 | 1·8 |
| **인터벌을 틱으로 미리 변환** | 🟢 승계 | 17 |
| `TickTimeM` 변환 유틸 | 🟡 개작 (`IClock`으로) | 1 |
| `ElapsedExecuteM` 계열 | 🟡 개작 (통합) | 17 |
| `TimerM<T>` (스레드풀 타이머) | 🔴 폐기 (타이밍 휠로) | 8 |
| `TimeM` / `TestSec` 수동 계측 | 🔴 폐기 (BenchmarkDotNet) | 12 |
| `DateTimeStartEndM` 구간 대수 | 🔴 폐기 (범위 밖) | — |

### 새 코드에 절대 옮기면 안 되는 것

1. `TimerM.cs:43~50` — **`AddOrUpdate` 팩토리에서 `IDisposable` 생성** → 경합 시 미Dispose 타이머가 계속 발화
2. `TimerM.cs:76~81` — **add-factory가 `null` 반환** → 딕셔너리에 null 저장 → Dispose 시 NRE
3. `TimerM.cs:48` — **타이머 콜백이 `Task`를 버린다** (예외 유실)
4. `TickTimeM.cs:19` — **`GTickMs`가 장기 실행에서 `double` 정밀도 초과**
5. `TickTimeM.cs:61` — **`Frequency / 1000` 정수 나눗셈**
6. `TickTimeM.cs:40~55` — **경과 시간 음수를 0으로 뭉갬** (시계 역행·버그 은폐)
7. 전 계층 — **`Stopwatch` 직접 호출** → 타이밍 테스트 불가

### Phase 1 `IClock` 설계에 반영할 것

이 계층이 Phase 1에 주는 요구는 구체적이다.

```
IClock
├─ 단조 시각 (Monotonic)  — 경과 측정. 절대 시각이 아니며 영속화 금지
└─ 벽시계 시각 (WallClock) — 절대 시각. 영속화·로그·만료 표시용
```

- **두 시각을 타입으로 분리한다.** 섞어 쓰면 컴파일 에러가 되게
- 내부 단위는 **고정 정수(마이크로초)**. `Stopwatch.Frequency` 나눗셈을 API 표면에서 제거
- **경과 시간이 음수면 예외 또는 명시적 결과**로. 조용히 0으로 만들지 않는다
- `TimeProvider`(.NET 8+) 채택을 우선 검토 — 표준 추상화가 이미 있다
- 스케줄러·타이머·틱 루프·지연 측정이 **모두 이 하나의 추상화 위에** 올라간다
