# 04 — 동시성 / 실행기 / 스케줄러

**정독 완료**: `PublicLib/ConcurSeqTaskExecM.cs`(870), `BasicLibM/Concurrent/ConcurrentQueueExecutorM.cs`(124), `BasicLibM/Concurrent/ExecutableTaskDispatcherM.cs`(94), `BasicLibM/Scheduler/ConcurrentSchedulerM.cs`(92)

**대기**: `Scheduler/TimeEventSchedulerM.cs`(876), `Concurrent/DataStructure/SparseSetM.cs`(696), `Scheduler/ExpireEventConCurSchedulerM.cs`(245), `JobSystemM.cs`(186), `Signal/AsyncManualResetEventM.cs`(148)

---

## 이 계층의 핵심 — 샤딩이 아키텍처의 중심 아이디어다

`oid % 샤드수`로 큐를 고르는 패턴이 **최소 3곳에서 독립적으로 반복**된다.

| 위치 | 샤딩 대상 | 샤드 수 |
|---|---|---|
| `SendPacketGroupM` (문서 01) | 송신 패킷 / 수신 memPk | `ProcessorCount × 설정 팩터` |
| `ConcurrentSchedulerGroupM` (`ConcurrentSchedulerM.cs:51`) | 시간 예약 작업 | 생성자 인자 |
| `SrvGlobal.cnt*PkActBlock` (문서 01) | 위 샤드 수 산정 | — |

> **같은 유저(oid)의 작업은 언제나 같은 샤드에 들어간다 → 락 없이 순서 보장.**
> 샤드끼리는 완전 독립이므로 병렬성은 샤드 수만큼 확장된다.
> 이것이 레거시 아키텍처의 중심 발상이고, ChServerM `IExecutionModel`이 계약으로 표현해야 할 대상이다.

**중요**: 순서 보장은 **샤드 배정**에서 나오지 실행기 내부에서 나오지 않는다. 실행기는 단일 리더 FIFO 소비자일 뿐이다. 이 분리를 이해하면 실행기 구현을 자유롭게 교체할 수 있다.

---

## `ConcurSeqTaskExecM<T>`

`ConcurSeqTaskExecM.cs:19`

### 동작

가장 단순한 형태. `ConcurrentQueue` + 실행 중 플래그 + `Task.Run`.

```csharp
public void Enqueue(T item) {
    _queue.Enqueue(item);
    if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
        _ = Task.Run(ProcessQueueAsync);
}

private async Task ProcessQueueAsync() {
    while (_queue.TryDequeue(out var item)) {
        try { await _processor(item).ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[Error] {ex}"); }
    }
    Interlocked.Exchange(ref _isRunning, 0);
    // 놓친 요청 복구 (lost wakeup 방지)
    if (!_queue.IsEmpty && Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0)
        _ = Task.Run(ProcessQueueAsync);
}
```

> **드레인 루프 + 재진입 복구 패턴의 정석이다.** 플래그를 0으로 되돌린 직후 큐를 다시 확인해, "플래그 해제와 새 Enqueue 사이"의 경쟁으로 작업이 영원히 잠드는 것(lost wakeup)을 막는다. 항목별 `try/catch`로 나쁜 항목 하나가 루프를 죽이지 않게 한 것도 옳다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`Console.WriteLine`으로 오류 처리** — 로깅 추상화 없음. 프로덕션에서 오류가 사라진다 | `:51` | 🟠 중간 |
| 2 | 큐가 무제한 — 백프레셔 없음 | `:21` | 🟠 중간 |
| 3 | 종료(`Complete`/`Dispose`) 경로 없음 | 전체 | 🟠 중간 |

### 판정

🟢 **승계** (패턴). 드레인 + 재진입 복구는 그대로. 로깅·백프레셔·종료를 추가. → Phase 8

---

## `ConcurSeqTaskContextExecM<T>`

`ConcurSeqTaskExecM.cs:125`, `where T : IUIThreadCheck`

### 동작

`Channel.CreateBounded<T>(1000)` 기반. UI 스레드 분기 포함.

```csharp
Channel.CreateBounded<T>(new BoundedChannelOptions(1000) {
    FullMode = BoundedChannelFullMode.Wait,   // "백프레셔 적용"
    SingleReader = true, SingleWriter = false,
    AllowSynchronousContinuations = false
});
```

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **백프레셔가 실제로 동작하지 않는다.** `FullMode = Wait`는 `WriteAsync`에만 적용된다. `Post`는 **`TryWrite`** 를 쓰는데, 채널이 가득 차면 `TryWrite`는 **`false`를 반환하고 항목을 버린다.** 반환값도 무시한다 → **부하 시 패킷이 조용히 유실된다.** 주석은 "백프레셔 적용"이라고 되어 있다 | `:131`, `:149` | 🔴 치명 |
| 2 | UI 경로가 항목마다 `TaskCompletionSource` 할당 + 튜플 `(item, tcs)` **박싱** | `:167~168` | 🟠 중간 |
| 3 | `_uiContext`가 static 초기화 시점의 `SynchronizationContext.Current` — 서버에서는 **null** | `:137`, `:100~101` | 🔴 높음 |

### 판정

🟡 **개작**. 채널 기반 방향은 옳지만 `TryWrite`/`FullMode` 불일치는 재현하면 안 된다. → Phase 8·10

---

## `ConcurSeqTaskContextExecLongRunM<T>` — **실제 사용되는 실행기**

`ConcurSeqTaskExecM.cs:213`

`SendPacketGroupM`과 `SendPacketM`이 쓰는 것이 이 클래스다.

### 동작

- **`Channel.CreateUnbounded<T>`** — 무제한 채널
- 생성자에서 **전용 장기 실행 스레드** 기동:
  ```csharp
  Task.Factory.StartNew(async () => await ProcessQueueAsync(),
      CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
  ```
- `Post`는 `TryWrite`만 (무제한이므로 항상 성공)
- `WaitToReadAsync` → `TryRead` 드레인 이중 루프
- `finally`에서 채널이 살아 있으면 워커 재기동
- **`UIWorkItem` 풀**(`ConcurrentBag`)로 UI 경로의 튜플 박싱을 제거하려는 시도

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **UI 경로가 반드시 `InvalidCastException`을 던진다.** `_uiContext.Post(_uiPostCallback, work)`로 **`UIWorkItem` 인스턴스**를 넘기는데, `UiPostCallback`은 `((T, TaskCompletionSource<bool>))state`로 **튜플 캐스팅**을 한다. 풀을 도입하면서 콜백을 갱신하지 않았다 | `:278` vs `:318` | 🔴 치명 버그 |
| 2 | 🔴 **무제한 채널 = 백프레셔 전무.** 소비자가 느리면 메모리가 무한히 증가한다. 과부하 시 OOM으로 죽는다 — "거부가 붕괴보다 낫다"의 정반대 | `:222` | 🔴 치명 |
| 3 | **샤드마다 전용 스레드.** 송신·수신 각각 `ProcessorCount × 팩터`개 → 16코어·팩터 1.0이면 **32개 전용 스레드**, 팩터 2.0이면 64개. 스레드 수가 설정값에 곱셈으로 연동된다 | `:241~249` | 🟠 중간 |
| 4 | 서버에서 `_uiContext`가 **null**. `item.IsUIThread()`가 true를 반환하는 순간 **`NullReferenceException`**. 현재는 `FinalPkDataM.IsUIThread() => false`라 잠복 상태지만, 핸들러를 `bMemPkUIThread: true`로 등록하면 즉시 터진다 | `:231` | 🔴 높음 |
| 5 | **`Dispose()`가 있는데 클래스가 `IDisposable`을 구현하지 않는다.** `using`이 안 되고 실제로 아무도 호출하지 않는다 | `:364`, `:213` | 🟠 중간 |
| 6 | `_disposed`를 설정하지만 `Post`에서 확인하지 않는다 — dispose 후 Post가 조용히 유실 | `:252~257` | 🟠 중간 |
| 7 | `Console.WriteLine` 오류 처리 (동일) | `:293` | 🟠 중간 |
| 8 | **약 430줄이 주석 처리된 폐기 버전** — 시퀀스 번호 기반 순서 보장을 시도했다가 접은 흔적 | `:383~870` | 🟡 낮음 |

### 개선점 (ChServerM)

- **채널 기반 단일 리더 소비자 구조는 승계.** 단:
  - **유계 채널 + `WriteAsync` 백프레셔** 또는 **명시적 거부(admission control)**. 무제한은 선택지가 아니다 (Phase 10)
  - 큐 포화를 **메트릭으로 노출**하고 임계 초과 시 커넥션 거부 (Phase 10·11)
  - UI 경로 전량 제거
  - 전용 스레드 수를 **옵션 + 상한**으로. 스레드-퍼-코어 모델과 통합 (Phase 8)
  - `IAsyncDisposable` + graceful drain
- `ConcurSeqTaskContextExecLongRunM` vs `System.Threading.Channels` 직접 사용 vs TPL Dataflow **3자 벤치마크** (Phase 8·12)

### 판정

🟡 **개작** — 구조 승계, 백프레셔·UI·종료는 재작성. → Phase 8·10

---

## `ConcurrentQueueExecutorM<T>` / `QueueExecutorM<T>`

`ConcurrentQueueExecutorM.cs:15`, `:70`

### 동작

**업데이트 이후 지연 처리 큐.** 두 종류의 작업을 받는다.
- `IExecutableAsyncM` (비동기 처리기) → `_afterUpdateProcessor`
- `Action<T>` (타겟에 대한 동기 액션) → `_afterUpdateAction`

`Execute()`가 둘을 순서대로 드레인하되 **`maxProcessOneTime`으로 한 번에 처리할 개수를 제한**한다.

> **틱 루프 중 자료구조 변경을 미루는 장치다.** 순회 중 컬렉션을 수정하면 안 되므로 변경을 큐에 쌓고 업데이트 후 일괄 적용한다. 게임 서버의 정석 패턴이고, 처리량 상한으로 **한 틱이 무한정 길어지는 것도 막는다.**

두 클래스의 차이는 큐 구현뿐이다 — `ConcurrentQueue`(스레드 안전) vs `PooledQueue`(Collections.Pooled, **스레드 안전 아님**).

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **약 50줄이 완전 중복** (두 클래스가 큐 타입만 다르다) | 전체 | 🟠 중간 |
| 2 | **이름과 문서에 스레드 안전성 차이가 드러나지 않는다.** `QueueExecutorM`을 멀티스레드에서 쓰면 조용히 손상된다 | `:70` | 🔴 높음 |
| 3 | `curProcessTimes`가 인스턴스 필드인데 동기화 없이 증감 — `Execute()` 동시 호출 시 경쟁 | `:19`, `:52` | 🟠 중간 |
| 4 | 처리기 예외 처리 없음 — 하나가 던지면 나머지가 드레인되지 않고 큐에 남는다 | `:47~66` | 🟠 중간 |
| 5 | 상한 도달로 남은 작업이 **다음 `Execute()`까지 지연**되는데, 이를 알리는 신호가 없다 (적체 관측 불가) | 설계 | 🟠 중간 |

### 개선점

- 큐 구현을 제네릭 파라미터나 전략으로 분리해 중복 제거
- **스레드 안전 여부를 타입 이름에 명시**하거나 하나로 통일
- 처리량 상한 초과분을 **메트릭으로 노출** (Phase 11)
- `IExecutionModel`의 "틱 후 지연 적용" 프리미티브로 승격 (Phase 17)

### 판정

🟢 **승계** (지연 적용 + 처리량 상한 패턴) / 🟡 **개작** (구현) → Phase 17

---

## `ExecutableTaskDispatcherM` — 🟢 락 없는 단일 소유자 디스패처

`ExecutableTaskDispatcherM.cs:34`

### 동작

**이 파일이 동시성 계층에서 가장 정교하다.**

```csharp
public void DoTask(IExecutableM task) {
    if (Interlocked.Increment(ref iCntRemainTask) != 1) {
        taskQue.Enqueue(task);              // 이미 소유자가 있다 → 넣고 끝
    } else {
        taskQue.Enqueue(task);
        if (tls_CurEtdOccupyingThread.Value != null)
            tls_EtdQue.Value.Enqueue(this); // 이 스레드가 다른 디스패처를 처리 중 → 예약
        else {
            tls_CurEtdOccupyingThread.Value = this;
            FlushTask();
            while (tls_EtdQue.Value.Count != 0)     // 예약된 것들 처리
                tls_EtdQue.Value.Dequeue().FlushTask();
            tls_CurEtdOccupyingThread.Value = null;
        }
    }
}

void FlushTask() {
    int iTaskCount;
    do {
        iTaskCount = taskQue.Count;
        for (int i = 0; i < iTaskCount; ++i) {
            taskQue.TryDequeue(out IExecutableM task);
            task.Execute();
        }
    } while (Interlocked.Add(ref iCntRemainTask, -iTaskCount) != 0);
}
```

두 가지 기법이 결합되어 있다.

**1. 단일 소유자 선출** — `Interlocked.Increment` 결과가 정확히 1이면 "내가 첫 번째"이므로 드레인 책임을 진다. 나머지 스레드는 큐에 넣기만 하고 즉시 반환한다. 락이 없고, 처리 스레드가 정확히 하나임이 보장된다.

**2. ThreadLocal 재진입 방지** — 처리 중인 작업이 **다른 디스패처**에 작업을 넣으면, 그 자리에서 재귀 호출하지 않고 `tls_EtdQue`에 예약한다. 현재 드레인이 끝난 뒤 순차 처리한다.
→ **스택 오버플로와 디스패처 간 교착을 구조적으로 차단한다.** 액터 모델의 메일박스 재진입 문제에 대한 정확한 해법이다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **예외 처리가 전혀 없다.** `task.Execute()`가 던지면 `Interlocked.Add(ref iCntRemainTask, -iTaskCount)`에 도달하지 못한다 → **카운터가 0으로 돌아오지 않아 디스패처가 영구히 잠긴다.** 이후 모든 `DoTask`가 큐에만 쌓이고 아무도 처리하지 않는다 | `:54` | 🔴 치명 |
| 2 | 🔴 **`tls_CurEtdOccupyingThread.Value = null`도 실행되지 않는다** (예외 시). 그 스레드는 이후 영원히 "점유 중"으로 판단되어 **모든 디스패처가 예약만 되고 처리되지 않는다** | `:90` | 🔴 치명 |
| 3 | `TryDequeue` 반환값 무시 — 실패 시 `task`가 null → `NullReferenceException` | `:53~54` | 🟠 중간 |
| 4 | `taskQue.Count`는 `ConcurrentQueue`에서 **O(n)에 가깝고 스냅샷**이다. 루프 조건에 쓰기에 부적절 | `:50` | 🟠 중간 |
| 5 | `static ThreadLocal<>` 2개를 `Dispose`하지 않음 | `:37~38` | 🟡 낮음 |
| 6 | `EtdTaskM<A>`가 작업마다 힙 할당 (`class`) | `:13` | 🟠 중간 |
| 7 | 이 클래스를 **실제로 쓰는 곳을 찾지 못했다** — 사용처 없이 남아 있을 가능성 | — | 🟡 낮음 |

### 개선점

- **두 기법 모두 승계.** 단일 소유자 선출 + ThreadLocal 재진입 예약은 `IExecutionModel`의 유저별 직렬 실행에 그대로 쓸 수 있다
- **`try/finally`로 카운터·ThreadLocal 복원을 보장한다.** #1·#2가 이 계층 최악의 버그이고, 원인은 단 하나 — `finally` 부재
- 작업을 `struct` + 제네릭으로 받아 할당 제거 검토

### 판정

🟢 **승계** (설계). **레거시에서 세 번째로 값어치 있는 코드.** 단 예외 안전성을 반드시 추가. → Phase 8

---

## `ConcurrentSchedulerM` / `ConcurrentSchedulerGroupM`

`ConcurrentSchedulerM.cs:7`, `:51`

### 동작

**`ConcurrentSchedulerM`** — `SortedList<long executeTick, IExecutableM>` + `lock`.
`ExecuteSchedule()`이 `TickTimeM.GTick` 이하인 항목을 앞에서부터 꺼내 실행한다. 콜백은 **락 밖에서** 호출한다(올바름).

**`ConcurrentSchedulerGroupM`** — `oid % 샤드수`로 스케줄러를 고르고, `Parallel.ForEach`로 전 샤드를 병렬 실행한다.
→ **`SendPacketGroupM`과 동일한 샤딩 패턴이 스케줄러에도 적용되어 있다.**

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **같은 틱에 두 개를 예약하면 크래시.** `SortedList.Add`는 **중복 키에 `ArgumentException`** 을 던진다. 틱은 `long` 타임스탬프라 부하 시 충돌이 충분히 발생한다 | `:18` | 🔴 치명 |
| 2 | **`_sortedList.First()`는 LINQ 확장** — `SortedList`를 **열거**하므로 O(1)이 아니고 열거자 할당이 발생한다. `_sortedList.Keys[0]`이 올바르다 | `:32` | 🔴 높음 |
| 3 | `SortedList.Remove(key)`는 **O(n) 배열 시프트**. 매 틱 다수 제거 시 비용이 크다 | `:35` | 🟠 중간 |
| 4 | `_sortedList.Count`를 **락 밖에서** 읽는다 (`:24`, `:44`) — 경쟁 | | 🟠 중간 |
| 5 | `Execute()` 예외 처리 없음 — 하나가 던지면 `ExecuteSchedule` 전체가 중단되고 나머지 만기 작업이 밀린다 | `:42` | 🔴 높음 |
| 6 | `ParallelExecuteSchedule`이 매 틱 `Parallel.ForEach` — 태스크·클로저 할당 | `:79` | 🟠 중간 |
| 7 | 취소·제거 API 없음 — 한 번 예약하면 취소 불가 | 전체 | 🟠 중간 |

### 개선점

- 자료구조를 **4-ary 힙(우선순위 큐)** 으로. .NET 6+ `PriorityQueue<TElement, TPriority>`는 중복 우선순위를 허용하고 삽입·추출이 O(log n)이다 → #1·#2·#3 동시 해결
- 예약 취소는 **핸들 + tombstone** 방식 (제거 대신 무효 표시 후 pop 시 건너뛰기)
- 샤드별 워커를 고정하고 `Parallel.ForEach` 제거 (Phase 8)
- 콜백 예외를 격리해 한 작업이 스케줄러를 멈추지 않게 (Phase 10)

### 판정

🟡 **개작** — 샤딩 + 락 밖 콜백 호출은 승계, 자료구조는 `PriorityQueue`로 교체. → Phase 8·17

---

## 이 계층의 잠정 종합 (미독 5개 파일 제외)

| 항목 | 판정 | Phase |
|---|---|---|
| **`oid % n` 샤딩으로 순서 보장 + 병렬성** | 🟢 승계 | 1·8 |
| **드레인 루프 + 재진입 복구 (lost wakeup 방지)** | 🟢 승계 | 8 |
| **단일 소유자 선출 (`Interlocked` 카운터)** | 🟢 승계 | 8 |
| **ThreadLocal 재진입 예약 (스택오버플로·교착 차단)** | 🟢 승계 | 8 |
| **틱 후 지연 적용 큐 + 처리량 상한** | 🟢 승계 | 17 |
| **락 밖에서 콜백 호출** | 🟢 승계 | 8·17 |
| 채널 기반 단일 리더 소비자 | 🟡 개작 | 8 |
| 시간 스케줄러 자료구조 | 🟡 개작 (`PriorityQueue`) | 8·17 |
| UI 스레드 디스패치 경로 | 🔴 폐기 | — |
| 무제한 채널 | 🔴 폐기 | 10 |

### 새 코드에 절대 옮기면 안 되는 것

1. `ConcurSeqTaskExecM.cs:131,149` — **`FullMode.Wait` + `TryWrite` 조합.** 백프레셔가 설정만 되고 동작하지 않아 **부하 시 패킷이 조용히 유실**
2. `ConcurSeqTaskExecM.cs:222` — **`CreateUnbounded`** (실제 사용 경로). 과부하 시 OOM
3. `ConcurSeqTaskExecM.cs:278` vs `:318` — **`UIWorkItem`을 넘기고 튜플로 캐스팅** → UI 경로 필연적 `InvalidCastException`
4. `ExecutableTaskDispatcherM.cs:54,90` — **`try/finally` 부재.** 작업 예외 하나로 디스패처와 **해당 스레드 전체가 영구 정지**
5. `ConcurrentSchedulerM.cs:18` — **`SortedList.Add` 중복 키 → `ArgumentException`**
6. `ConcurrentSchedulerM.cs:32` — **`SortedList.First()`** (LINQ, O(n) + 할당)
7. `ConcurrentSchedulerM.cs:42` — **스케줄 콜백 예외 미격리**
8. `ConcurrentQueueExecutorM.cs:70` — **스레드 안전하지 않은 쌍둥이 클래스**가 이름·문서로 구분되지 않음

> **공통 원인이 뚜렷하다 — `try/finally` 부재와 예외 격리 부재.**
> 락 없는 카운터·플래그 기반 설계는 **예외 경로에서 상태를 복원하지 않으면 영구 교착**된다.
> ChServerM에서 이 패턴을 승계할 때는 **상태 복원을 `finally`로 강제**하고, 가능하면 `ref struct` 스코프 가드로 타입 차원에서 보장한다.
