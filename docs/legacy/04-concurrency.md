# 04 — 동시성 / 실행기 / 스케줄러

**전량 정독 완료** — `PublicLib/ConcurSeqTaskExecM.cs`(870), `Scheduler/TimeEventSchedulerM.cs`(876), `Concurrent/DataStructure/SparseSetM.cs`(696), `Scheduler/ExpireEventConCurSchedulerM.cs`(245), `JobSystemM.cs`(186), `Signal/AsyncManualResetEventM.cs`(148), `Concurrent/ConcurrentQueueExecutorM.cs`(124), `Concurrent/ExecutableTaskDispatcherM.cs`(94), `Scheduler/ConcurrentSchedulerM.cs`(92) — 총 3,331줄

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

## `TimeEventSchedulerM` — 🟢 5단 계층적 타이밍 휠

`BasicLibM/Scheduler/TimeEventSchedulerM.cs:392`

**레거시 전체에서 가장 정교한 자료구조다.** Kafka `TimingWheel` / Netty `HashedWheelTimer`와 같은 계열이다.
`ServerM.gTimeScheduler`(전역), `HashM` 만료, `BaseGameObjM.ExpireJobScheduler`, `MapObjM.ExpireJobScheduler`가 전부 이것을 쓴다.

### 구조

```
TimeEventSchedulerM
├─ _incoming      : ConcurrentQueue<AbTimeEventBaseM>          외부 → 워커 인계
├─ _allJobs       : ConcurrentDictionary<string, AbTimeEventBaseM>
├─ _listPool      : ObjectPoolM<List<AbTimeEventBaseM>>
├─ _deferredExpired : Queue<AbTimeEventBaseM>                  처리량 초과분
└─ 휠 5단 (상위 → 하위 캐스케이딩)
     monthlyWheel   12 슬롯 × 1개월   = 12개월
     veryLongWheel  30 슬롯 × 1일     = 30일
     longWheel     168 슬롯 × 1시간   = 7일
     mediumWheel  1440 슬롯 × 1분     = 24시간
     shortWheel   3000 슬롯 × 100ms   = 5분
```

**`TimingWheelSlotM`** (`:99`) — 슬롯 하나. **Treiber 스택**(락-프리 단일 연결 리스트)으로 구현.
```csharp
do {
    oldHead = Volatile.Read(ref _head);
    node.Next = oldHead;
    Thread.SpinWait(1);
} while (Interlocked.CompareExchange(ref _head, node, oldHead) != oldHead);
```
추출은 `Interlocked.Exchange(ref _head, null)` 한 번으로 전체를 떼어낸다 — **O(1) 원자적 배치 추출**. `Node`는 `ObjectPoolM<Node>`로 재사용한다.

**`TimingWheelM.Advance`** (`:303`) — 현재 시각까지 슬롯을 진행하며 각 작업을 판정한다.
- 만료됨 → `expiredJobs`에 수집
- 아직 남았고 하위 휠 범위 → **하위 휠로 캐스케이딩**
- 아직 남았고 현재 휠 범위 → 같은 휠에 재삽입(`selfAddFlag: true`로 최소 다음 틱)

**`ProcessExpired`** (`:546`) — 워커 루프의 본체
1. `_deferredExpired` 먼저 처리 (처리량 상한까지)
2. `_incoming` 드레인 → 지연 시간에 따라 **적절한 휠에 직접 배치** (5분/1일/7일/30일/그 이상)
3. **상위 휠부터 순서대로** `Advance` 호출 — `monthly → veryLong → long → medium → short`
4. 수집된 만료 작업 실행. 상한 초과분은 `_deferredExpired`로 이월

> **3번의 순서가 핵심이다.** 상위 휠을 먼저 진행시켜야 하위로 내려온 작업이 **같은 패스에서** 처리된다. 반대로 하면 한 틱씩 밀린다. 주석에도 명시돼 있다.

> **왜 타이밍 휠인가**: 우선순위 큐는 삽입·추출이 O(log n)이고 만료 시각으로 정렬해야 한다. 타이밍 휠은 **삽입 O(1), 틱당 진행 O(1)** 이다. 만료 타이머가 수만 개인 게임 서버(버프·쿨다운·세션 타임아웃)에서 차이가 크다.
>
> 파일 끝 `:684~875`에 **`PriorityQueue` 기반 이전 버전이 주석으로 남아 있다.** 즉 **우선순위 큐 → 계층적 타이밍 휠로 이행한 이력**이다. 의도적인 개선이었다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **휠의 틱 원점이 현재 시각으로 초기화되지 않는다.** `_currentTickIndex`는 0에서 시작하는데 `Advance`의 `targetTick = currentTimestamp / tickDuration`은 **절대값**(수십억)이다. 첫 `Advance`에서 `ticksToAdvance`가 천문학적 값이 되어 `min(_, slotCount)`로 잘리고, 5개 휠 합계 **4,650회 빈 슬롯 순회** 후 `_currentTickIndex = targetTick`으로 점프한다. 그 전에 삽입된 작업은 원점 0 기준 슬롯에 있어 재배치된다. 자기 교정되지만 **기동 비용과 설계 취약성**이다 | `:284~290` vs `:316~317` | 🔴 높음 |
| 2 | 🔴 **만료와 취소가 같은 코드 경로다.** 작업 발화도 `job.Cancel()`로 한다(`:616`, `:560`). 핸들러(`OnTerminate`)는 **"시간이 되어 불렸는지" 와 "사용자가 취소했는지"를 구별할 수 없다.** `ScriptDelayEventM`은 취소돼도 리셋 이벤트를 Set한다 | `:616`, `:86~92` | 🔴 높음 |
| 3 | **`Advance`의 조기 반환 경로에 죽은 디버그 코드.** `int k = 3; if (_wheelName == "shortWheel") { k = 5; } return;` — `k`는 미사용이고, **문자열 비교가 남아 있다.** 느린 휠은 대부분의 호출에서 이 경로를 타므로 매 틱 문자열 비교가 5회 발생 | `:319~328` | 🟠 중간 |
| 4 | **`Thread.SpinWait(1)`이 CAS 시도 *전에*, 루프 안에 있다.** 경합이 없는 첫 시도에서도 무조건 스핀한다 | `:138` | 🟠 중간 |
| 5 | **슬롯마다 무제한 `ObjectPoolM<Node>`.** 3,000개 슬롯 × 상한 없는 풀. 한 번 폭주한 슬롯은 노드를 영원히 붙들고 있는다 | `:116` | 🟠 중간 |
| 6 | **`_ticksPerMs = Stopwatch.Frequency / 1000` 정수 나눗셈.** Frequency가 1000의 배수가 아니면 오차가 누적된다(예: 3,579,545 → 0.015% 편차 → 30일 타이머에서 약 6.5분 오차) | `:432`, `:639~640` | 🟠 중간 |
| 7 | **`Stop()`이 `ProcessExpired()`를 직접 호출**하는데 워커가 아직 살아 있을 수 있다 → `_deferredExpired`(비동기화 `Queue<T>`)에 **경쟁** | `:491`, `:426` | 🟠 중간 |
| 8 | `Stop()`의 `catch (Exception ex) { }` — **완전히 비어 있고 `ex` 미사용** | `:494~497` | 🟠 중간 |
| 9 | **`Dispose()`가 있으나 `IDisposable`을 구현하지 않는다** (`ConcurSeqTaskContextExecLongRunM`과 같은 패턴) | `:673`, `:392` | 🟠 중간 |
| 10 | **작업 ID가 `string`.** 추가·제거마다 문자열 해싱. `ScriptDelaysM`이 모든 ID를 `""`로 만드는 버그(위 참조)와 겹쳐 **두 번째 이후 스크립트 지연이 조용히 드롭**된다 | `:395`, `:507` | 🔴 높음 |
| 11 | `IsEmpty`가 `Volatile.Read` 없이 `_head`를 읽는다 (`AddJob`은 사용) — 불일치. 갓 추가된 작업을 놓칠 수 있다(다음 틱에 복구되므로 경미) | `:172` | 🟡 낮음 |
| 12 | 최상위 휠(12개월) 범위를 넘는 작업은 슬롯이 겹친다. `Advance`에서 재삽입되며 자기 교정되지만 churn 발생 | `:290` | 🟡 낮음 |
| 13 | `AbTimeEventBaseM._owner` 필드가 **한 번도 대입되지 않는다** (생성자의 대입이 주석 처리). `Owner`는 abstract이므로 파생이 제공한다 — 죽은 필드 | `:42`, `:56` | 🟡 낮음 |
| 14 | 약 190줄이 주석 처리된 이전 버전 | `:684~875` | 🟡 낮음 |

### 개선점 (ChServerM)

- **계층적 타이밍 휠 설계를 그대로 승계한다.** 만료 타이머가 많은 서버에서 우선순위 큐보다 확실히 낫다
- **휠 원점을 생성 시점의 절대 틱으로 초기화**한다 (#1)
- **만료와 취소를 분리한다.** `OnExpired()` / `OnCanceled()` 두 콜백, 또는 `Terminate(TerminationReason)` (#2)
- **작업 ID를 강타입 `readonly struct JobId`(ulong 기반)로.** 문자열 해싱 제거 (#10, Phase 1 ID 타입)
- 시간 단위를 `Stopwatch.Frequency` 정수 나눗셈이 아니라 **고정 단위(마이크로초)** 로 정의 (#6)
- 노드 풀에 **상한**을 두고 유휴 시 축소 (#5, Phase 3)
- `Volatile.Read` 일관 적용, `SpinWait`는 재시도 시에만
- `IAsyncDisposable` + 워커 종료를 `Stop()`에서 확실히 대기한 뒤 잔여 처리
- 휠 구성(단수·해상도·범위)을 **옵션으로 노출** — 워크로드마다 최적값이 다르다 (Phase 2)

### 판정

🟢 **승계** (설계). **레거시 최고 자산 중 하나.** `SendPacketGroupM`의 샤딩, `AllowedPacketMan`의 화이트리스트와 함께 3대 승계 대상이다.

→ Phase 1 (JobId 강타입), Phase 8 (구현), Phase 17 (틱·타이머)

### 부수 결론 — 스케줄러가 두 개다

`ConcurrentSchedulerM`(`SortedList` 기반, 중복 키 크래시·`First()` LINQ 등 문제 다수)과 `TimeEventSchedulerM`(계층적 타이밍 휠)이 **공존한다.** 후자가 모든 면에서 우월하고 실제로 널리 쓰인다.

→ ChServerM은 **타이밍 휠 하나로 통일**한다. 문서 04의 `ConcurrentSchedulerM` 개선안("`PriorityQueue`로 교체")은 이 발견에 비추어 **"타이밍 휠로 통합"으로 정정**한다. 소규모·단순 예약이라도 스케줄러를 둘 유지할 이유가 없다.

---

## `AsyncManualResetEventM` — 🟢 무할당 비동기 시그널

`BasicLibM/Signal/AsyncManualResetEventM.cs:73`, `sealed class`

### 동작

`TaskCompletionSource` 기반 수동 리셋 이벤트. **구현 품질이 이 계층에서 가장 높다.**

```csharp
private static readonly ValueTask s_completedTask = new ValueTask();
private volatile TaskCompletionSource<byte> _tcs
    = new(TaskCreationOptions.RunContinuationsAsynchronously);

public ValueTask WaitAsync(CancellationToken ct = default) {
    var tcs = _tcs;                       // 다른 스레드의 Reset 대비 로컬 캡처
    if (tcs.Task.IsCompleted) return s_completedTask;   // 무할당 빠른 경로
    if (ct.IsCancellationRequested) return new ValueTask(Task.FromCanceled(ct));
    if (ct.CanBeCanceled) return WaitAsyncCore(tcs, ct);
    return new ValueTask(tcs.Task);
}

public void Reset() {
    var currentTcs = _tcs;
    if (!currentTcs.Task.IsCompleted) return;
    Interlocked.CompareExchange(ref _tcs, new(...), currentTcs);
}
```

승계 가치가 있는 세부:
- **`RunContinuationsAsynchronously`** — `Set()` 호출 스레드에서 대기자의 연속이 인라인 실행되는 것을 막는다. 이게 없으면 시그널을 보낸 스레드가 남의 작업을 떠안는다
- **`WaitAsync`에서 `_tcs`를 먼저 로컬 캡처** — 검사와 사용 사이의 `Reset` 경쟁 회피
- **이미 완료 시 static `ValueTask` 반환** — 할당 0
- `Reset`을 `CompareExchange`로 — 두 스레드가 동시에 Reset해도 TCS가 하나만 교체된다
- 취소 등록을 `using`으로 확실히 해제

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **`SetAndReset()`은 원자적이지 않다.** `TrySetResult` → `CompareExchange` 두 단계 사이에 도착한 대기자는 완료된 TCS를 받아 즉시 통과한다. pulse 의미로는 맞을 수 있으나 주석("원자적 Set-Reset 구현")이 과장 | `:137~144` | 🟠 중간 |
| 2 | `s_completedTask = new ValueTask()` — 동작은 맞지만 `ValueTask.CompletedTask`가 의도를 드러낸다 | `:75` | 🟡 낮음 |

### 판정

🟢 **승계**. **레거시 동시성 코드 중 가장 잘 만들어졌다.** ChServerM에서 시그널이 필요하면 이 구현을 기준으로 삼는다. → Phase 8

---

## `ScriptDelaysM` / `ScriptDelayEventM`

같은 파일 `:11`, `:29`

### 동작

스크립트의 `Sleep(delayMs)`를 시간 이벤트 스케줄러 + 리셋 이벤트로 구현한다.
`Sleep` → `AsyncManualResetEventM` 생성·등록 → `TimeEventSchedulerM.AddJob(ScriptDelayEventM)` → `await resetEvent.WaitAsync()`.
만료되면 `OnTerminate` → `EnqueSetAndResetEvent` → 정적 큐에 넣어 나중에 Set.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`_timeEvents`가 접근할 때마다 새 딕셔너리를 만든다.** `ConcurrentDictionary<...> _timeEvents => new();` — **`=>`(식 본문 프로퍼티)와 `=`(필드 초기자)를 혼동**했다. `TimeEvents`는 **항상 비어 있다** → `IHasTimeEventsM` 계약이 깨져 있다 | `:38~39` | 🔴 치명 버그 |
| 2 | 🔴 **`new StringBuilder(idNum++).ToString()`은 빈 문자열을 반환한다.** `StringBuilder(int)`는 **용량(capacity)** 생성자다. 값이 아니다. 따라서 모든 리셋 이벤트의 ID가 `""`가 되어, `_dicResetEvent.TryAdd("", ...)`가 **첫 번째만 성공**한다 → 두 번째 이후 `Sleep`은 영원히 깨어나지 않는다 | `:51~52` | 🔴 치명 버그 |
| 3 | `idNum++`가 스레드 안전하지 않다 | `:51` | 🟠 중간 |
| 4 | `static ConcurrentQueue<AsyncManualResetEventM> queSetAndResetEvent` — 정적 큐인데 이 파일에 **드레인하는 코드가 없다** | `:36` | 🟠 중간 |

### 판정

🔴 **폐기**. 스크립트 시스템 자체가 폐기 대상(하드 룰)이고, 구현도 두 개의 치명 버그로 동작하지 않는다.
다만 **"비동기 Sleep을 타이머 + 시그널로 구현"하는 발상**은 🔵 참고 가치가 있다 — Phase 17에서 틱 기반 지연이 필요할 때.

---

## `UniqueBufferBlock<T>` (`JobSystemM.cs`)

`BasicLibM/JobSystemM.cs:135`

파일 186줄 중 **132줄이 주석**(맵 업데이트 루프, `BufferBlock` 기반 잡 시스템, `SemaphoreSlim` 사용법 참고 코드)이다. 실제 클래스는 하나뿐.

### 동작

`BufferBlock<T>` + `ConcurrentDictionary<T, bool>`로 **중복 제거 큐**. 같은 항목이 큐에 두 번 들어가지 않는다.
`ReceiveAsync`에서 꺼낸 뒤 딕셔너리에서 제거하므로, 처리 완료 후 재등록이 가능해진다.

> 발상 자체는 유용하다 — "이미 갱신 예약된 오브젝트를 다시 예약하지 않는다"는 더티 큐 패턴이다.

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | 중복 시 `Console.WriteLine` — 정상 흐름인데 콘솔에 찍는다. 부하 시 심각한 병목 | 🔴 높음 |
| 2 | `T`가 딕셔너리 키가 되므로 **올바른 `Equals`/`GetHashCode`가 필수**인데 제약(`where`)이 없다 | 🟠 중간 |
| 3 | 꺼낸 직후 제거하므로 **처리 중에 같은 항목이 다시 큐에 들어갈 수 있다.** 의도인지 불명 | 🟠 중간 |
| 4 | 사용처를 찾지 못했다 | 🟡 낮음 |

### 판정

🔵 **참고** (더티 큐 발상). Phase 18의 델타 전송 대기열에 같은 패턴이 필요하다 — 단 `Console.WriteLine` 없이, 비트 플래그 기반으로.

---

## `ExpireEventConCurSchedulerM<T>` / `ExpireEventSchedulerM<T>` — 세 번째·네 번째 스케줄러

`BasicLibM/Scheduler/ExpireEventConCurSchedulerM.cs:78`, `:188`

### 동작

`SortedList<DateTime, PooledList<T>>` 기반. 같은 시각의 이벤트를 리스트로 묶어 **중복 키 문제를 회피**한다(`ConcurrentSchedulerM`이 놓친 부분).

| 타입 | 동시성 | 워커 |
|---|---|---|
| `ExpireEventConCurSchedulerM<T>` | `ConcurrentQueue` 유입 + 내부 단일 스레드 | 자체 보유 (`StartSchedulerAsync`) |
| `ExpireEventSchedulerM<T>` | 없음 | 외부가 `ProcessSchedules()` 호출 |

`ITimeEventM` : `IExecutableM` — `bCanceled`, `CallBackProcess`, `TriggerTime`
`AbExpireEventM` — `Cancel()`은 `Interlocked.Increment(ref _bCanceled)`
`ExpireJobForDicRemoveM` — 특정 시각에 `PooledDictionary`에서 키를 제거하는 잡. **`HashM`의 만료 구현체**다

> 클래스 주석(`:13`)이 중요한 사실을 알려준다:
> *"Concurrent하지 않으니 주의 할 것 — **HashM자체가 동시성 지원하지 않음**(필요하면 변경해야 함)"*
>
> 즉 `HashM`(오브젝트별 만료 KV, 문서 03)은 **스레드 안전하지 않은데 만료 잡은 스케줄러 스레드에서 실행된다.** 게임 로직 스레드가 `SetHash`를 하는 동안 스케줄러 스레드가 `Remove`를 하면 **딕셔너리가 손상된다.** 주석으로 인지는 하고 있으나 해결되지 않았다.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`async void ProcessTimeEventsAsync()`** — 예외가 관측 불가능하고 프로세스를 죽인다. 게다가 `new Task(Action)`에 넘겨지므로 **첫 `await`에서 Task가 완료된 것으로 간주**된다 → `StopScheduler`의 `await _schedulerTask`가 즉시 반환되어 **실제 종료를 기다리지 않는다** | `:115`, `:101`, `:176` | 🔴 치명 |
| 2 | 🔴 **`PooledList<T>` 누수.** `_eventQueue.Remove(key)`가 리스트를 버리는데 **`Dispose()`를 호출하지 않는다.** `Collections.Pooled` 타입은 Dispose해야 내부 배열이 `ArrayPool`로 돌아간다 → **처리한 시각마다 풀 배열이 유실**된다 | `:151`, `:238` | 🔴 치명 |
| 3 | 🔴 **`ExpireJobForDicRemoveM`이 스레드 안전하지 않은 `PooledDictionary`를 스케줄러 스레드에서 변경한다.** ※ 주석은 "HashM"이라 적었으나 **현재 `HashM`은 `ConcurrentDictionary`를 쓴다** — 이 주석은 **더 오래된 만료 경로**를 가리킨다. 해시 만료 메커니즘이 두 벌 공존한다. 상세: [07-security.md](07-security.md#hashm--expirehasheventm--만료-지원-kv-저장소) | `:13`, `:28` | 🔴 치명 |
| 4 | **워커가 `_cancellationTokenSource.Dispose()`를 호출**하는데 `StopScheduler`도 `Cancel()`을 호출한다 → `ObjectDisposedException` 경쟁 | `:122`, `:173` | 🟠 중간 |
| 5 | `DateTime.UtcNow` 해상도가 Windows 기본 약 15.6ms — 100ms 미만 정밀도를 낼 수 없다 | `:129`, `:211` | 🟠 중간 |
| 6 | `_keysToRemove`(`PooledList`)도 Dispose되지 않는다 | `:87`, `:191` | 🟠 중간 |
| 7 | `bCanceled`가 `bool`이 아니라 `int`로 노출. `Cancel()`을 두 번 부르면 2가 된다 | `:41~47` | 🟡 낮음 |
| 8 | 두 클래스가 `ProcessSchedules` 로직을 **완전 중복** | `:126`, `:209` | 🟠 중간 |

### 판정

🔴 **폐기**. `TimeEventSchedulerM`(계층적 타이밍 휠)이 모든 면에서 우월하다. 다만 **"같은 시각 이벤트를 리스트로 묶어 중복 키를 회피"** 하는 처리는 🔵 참고.

---

## 🔴 스케줄러가 네 개다

정독 결과 **시간 예약 스케줄러가 4종 공존**한다. 책임이 겹치고 품질 편차가 크다.

| 스케줄러 | 자료구조 | 시간 단위 | 상태 |
|---|---|---|---|
| **`TimeEventSchedulerM`** | **5단 계층적 타이밍 휠** | `Stopwatch` 틱 | 🟢 최상. 널리 사용됨 |
| `ConcurrentSchedulerM` / `GroupM` | `SortedList<long, IExecutableM>` | `TickTimeM.GTick` | 🔴 중복 키 크래시, `First()` LINQ |
| `ExpireEventConCurSchedulerM<T>` | `SortedList<DateTime, PooledList<T>>` | `DateTime.UtcNow` | 🔴 `async void`, 풀 누수 |
| `ExpireEventSchedulerM<T>` | 위와 동일 (비동시성) | `DateTime.UtcNow` | 🔴 풀 누수 |

**세 가지 서로 다른 시간 표현**(`Stopwatch` 틱 / `TickTimeM.GTick` / `DateTime.UtcNow`)이 동시에 쓰인다. 정밀도·기준점·직렬화 방식이 모두 달라 상호 변환에서 오차가 생긴다.

→ **ChServerM은 스케줄러 하나, 시간 표현 하나로 통일한다.**
- 자료구조: 계층적 타이밍 휠 (`TimeEventSchedulerM` 설계 승계)
- 시간: `IClock` 추상화 + **고정 단위 정수**(마이크로초). `Stopwatch.Frequency` 나눗셈 제거
- 문서 04의 `ConcurrentSchedulerM` 개선안("`PriorityQueue`로 교체")은 **"타이밍 휠로 통합"으로 정정**한다

→ Phase 1 (`IClock`), Phase 8 (스케줄러 단일화), Phase 17 (틱)

---

## `SparseSetM<T>` 계열 (4종)

`BasicLibM/Concurrent/DataStructure/SparseSetM.cs`

### 동작

| 클래스 | 키 | 값 | 락 | `AsSpan()` |
|---|---|---|---|---|
| `ConcurrentSparseSetM<T>` (`:13`) | `T` 자신 | — | `ReaderWriterLockSlim` | ❌ (`ToArray()`) |
| `SparseSetM<T>` (`:200`) | `T` 자신 | — | 없음 | ✅ |
| `ConcurrentSparseSetGetM<KEY,T>` (`:320`) | `KEY` | `T` | `ReaderWriterLockSlim` | ❌ |
| `SparseSetGetM<KEY,T>` (`:560`) | `KEY` | `T` | 없음 | ✅ |

공통 구조: `PooledDictionary<KEY,int> _sparse`(키 → 밀집 배열 인덱스) + `PooledList<T> _dense`(밀집 저장) + `int _count`

**`TryRemove`가 swap-remove 관용구를 정확히 구현했다.**
```csharp
_count--;
if (index < _count) {
    T lastValue = _dense[_count];
    _dense[index] = lastValue;      // 마지막 원소를 빈 자리로
    _sparse[lastValue] = index;     // 인덱스 갱신
}
_dense.RemoveAt(_count);
_sparse.Remove(value);
```
→ **삭제 O(1)이면서 밀집 배열에 구멍이 생기지 않는다.** `AsSpan()`으로 무할당 순회가 가능하다.

`ObjBasicDataM.referQuadGrids`가 `List<SparseSetM<Entity>>`인 이유가 이것이다 — **공간 그리드 셀의 멤버십 집합**으로 쓰려던 설계다(문서 03 참조. 그리드 자체는 미구현).

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **이름과 달리 진짜 sparse set이 아니다.** 정통 sparse set은 `sparse`를 **정수 인덱스 배열**로 두어 해싱 없이 O(1)을 얻는다. 여기서는 `PooledDictionary`(해시맵)를 쓴다 → **sparse set의 핵심 이점(해싱 없음, 캐시 친화적 정수 인덱싱)이 사라졌다.** `Dictionary<T,int> + List<T>`를 sparse set이라 부르는 셈 | `:15`, `:202` | 🔴 높음 |
| 2 | 🔴 **`using System.Windows.Forms;`** — 자료구조 파일에 WinForms 참조. `using static log4net.Appender.FileAppender;`도 무의미 | `:7~8` | 🔴 높음 |
| 3 | **swap-remove가 인덱스를 무효화한다.** `Get(i)`로 얻은 인덱스를 들고 있던 호출자는 삭제 후 **조용히 다른 원소를 읽는다.** 세대(generation) 검증이 없다 | `:70~95` | 🔴 높음 |
| 4 | **`ReaderWriterLockSlim`이 인스턴스마다.** 스레드 친화성 추적 때문에 무겁다(100바이트 이상 + 진입/이탈 비용). 그리드 셀마다 하나면 비용이 크다 | `:17`, `:325` | 🟠 중간 |
| 5 | **예외 타입이 불일치.** 같은 범위 초과 조건에 `ConcurrentSparseSetM.Get`은 `IndexOutOfRangeException`, `SparseSetM.Get`은 `KeyNotFoundException`을 던진다. 메시지도 `"SparseSet에 없음 found."`로 한·영 혼용 | `:141`, `:278` | 🟠 중간 |
| 6 | 4개 클래스가 거의 동일한 로직을 반복 (락/키 유무 조합) | 전체 | 🟠 중간 |
| 7 | Visual Studio Dispose 템플릿의 `// TODO:` 주석이 4곳 모두 그대로 남아 있다 | 다수 | 🟡 낮음 |
| 8 | `ToArray()`가 매 호출 새 배열 할당 (동시성 버전은 `AsSpan` 미제공이라 이것뿐) | `:144`, `:482` | 🟠 중간 |

### 개선점 (ChServerM)

- **정통 sparse set으로 재구현한다.** 엔티티 ID가 조밀한 정수라면 `int[] sparse` + `T[] dense`로 **해싱 없이** O(1). Phase 1의 강타입 ID(`readonly struct` + `int`/`ulong`)와 맞물린다
- **`AsSpan()` 기반 무할당 순회는 승계.** 모든 변형에 제공
- 인덱스 무효화는 **세대 카운터** 또는 "인덱스를 밖으로 내보내지 않는" API로 차단
- 락은 **실행 모델에 위임**한다 — 유저별/셀별 직렬 실행이 보장되면 락 자체가 불필요해진다 (Phase 8)
- 4종을 **제네릭 + 정책 파라미터 하나**로 통합

### 판정

🟡 **개작** — swap-remove 관용구와 `AsSpan` 순회는 승계, 자료구조와 락 전략은 재작성. → Phase 1·8·18

---

## 이 계층의 종합 (전량 정독 완료)

| 항목 | 판정 | Phase |
|---|---|---|
| **`oid % n` 샤딩으로 순서 보장 + 병렬성** | 🟢 승계 | 1·8 |
| **5단 계층적 타이밍 휠** (`TimeEventSchedulerM`) | 🟢 승계 | 8·17 |
| **Treiber 스택 슬롯 + 원자적 배치 추출** | 🟢 승계 | 8 |
| **상위→하위 휠 캐스케이딩 순서** | 🟢 승계 | 8·17 |
| **무할당 비동기 시그널** (`AsyncManualResetEventM`) | 🟢 승계 | 8 |
| **드레인 루프 + 재진입 복구 (lost wakeup 방지)** | 🟢 승계 | 8 |
| **단일 소유자 선출 (`Interlocked` 카운터)** | 🟢 승계 | 8 |
| **ThreadLocal 재진입 예약 (스택오버플로·교착 차단)** | 🟢 승계 | 8 |
| **틱 후 지연 적용 큐 + 처리량 상한** | 🟢 승계 | 17 |
| **swap-remove + `AsSpan` 무할당 순회** | 🟢 승계 | 1·18 |
| **락 밖에서 콜백 호출** | 🟢 승계 | 8·17 |
| 채널 기반 단일 리더 소비자 | 🟡 개작 | 8 |
| `SparseSetM` 자료구조 (해시맵 기반) | 🟡 개작 (정수 인덱스로) | 1·18 |
| 나머지 스케줄러 3종 | 🔴 폐기 (타이밍 휠로 통합) | 8 |
| UI 스레드 디스패치 경로 | 🔴 폐기 | — |
| 무제한 채널 | 🔴 폐기 | 10 |
| `ScriptDelaysM` (스크립트 Sleep) | 🔴 폐기 | — |

### 새 코드에 절대 옮기면 안 되는 것

1. `ConcurSeqTaskExecM.cs:131,149` — **`FullMode.Wait` + `TryWrite` 조합.** 백프레셔가 설정만 되고 동작하지 않아 **부하 시 패킷이 조용히 유실**
2. `ConcurSeqTaskExecM.cs:222` — **`CreateUnbounded`** (실제 사용 경로). 과부하 시 OOM
3. `ConcurSeqTaskExecM.cs:278` vs `:318` — **`UIWorkItem`을 넘기고 튜플로 캐스팅** → UI 경로 필연적 `InvalidCastException`
4. `ExecutableTaskDispatcherM.cs:54,90` — **`try/finally` 부재.** 작업 예외 하나로 디스패처와 **해당 스레드 전체가 영구 정지**
5. `ConcurrentSchedulerM.cs:18` — **`SortedList.Add` 중복 키 → `ArgumentException`**
6. `ConcurrentSchedulerM.cs:32` — **`SortedList.First()`** (LINQ, O(n) + 할당)
7. `ConcurrentSchedulerM.cs:42` — **스케줄 콜백 예외 미격리**
8. `ConcurrentQueueExecutorM.cs:70` — **스레드 안전하지 않은 쌍둥이 클래스**가 이름·문서로 구분되지 않음
9. `ExpireEventConCurSchedulerM.cs:115` — **`async void` 워커** + `new Task(Action)` 조합 → 종료 대기가 즉시 반환
10. `ExpireEventConCurSchedulerM.cs:151`,`:238` — **`PooledList<T>`를 Dispose 없이 버림** → `ArrayPool` 배열 유실
11. `ExpireEventConCurSchedulerM.cs:13`,`:28` — **스레드 안전하지 않은 `HashM`을 스케줄러 스레드에서 변경**
12. `TimeEventSchedulerM.cs:284~290` vs `:316` — **휠 틱 원점 미초기화** (첫 Advance에서 4,650회 빈 순회 후 점프)
13. `TimeEventSchedulerM.cs:616` — **만료와 취소가 같은 코드 경로** (핸들러가 구별 불가)
14. `TimeEventSchedulerM.cs:319~328` — 조기 반환 경로에 **죽은 디버그 코드 + 매 틱 문자열 비교**
15. `SparseSetM.cs:7~8` — **자료구조 파일의 `System.Windows.Forms` 참조**
16. `SparseSetM.cs:70~95` — **swap-remove가 외부 인덱스를 조용히 무효화** (세대 검증 없음)
17. `ScriptDelaysM`(`AsyncManualResetEventM.cs:38,51`) — **`=>` vs `=` 혼동**, **`StringBuilder(int)`는 용량 생성자**

> **공통 원인이 두 가지로 뚜렷하다.**
>
> **(1) `try/finally` 부재.** 락 없는 카운터·플래그 설계는 **예외 경로에서 상태를 복원하지 않으면 영구 교착**된다(#4). 승계할 때는 상태 복원을 `finally`로 강제하고, 가능하면 `ref struct` 스코프 가드로 타입 차원에서 보장한다.
>
> **(2) 풀링 리소스의 소유권이 코드로 표현되지 않는다.** `ArrayPool` 미반납(문서 01·02), `PooledList` 미Dispose(#10), 무제한 `ObjectPoolM`(#5) — 전부 "누가 언제 돌려주는가"가 주석에만 있고 타입에 없다. **Phase 3에서 소유권을 타입으로 표현하는 것이 이 계열 버그를 구조적으로 없애는 유일한 방법이다.**

### 이 계층에서 승계할 3대 자산

1. **`oid % n` 샤딩** — 락 없는 유저별 순서 보장 + 선형 병렬성
2. **5단 계층적 타이밍 휠** — 삽입 O(1), 틱당 O(1). 만료 타이머 수만 개를 감당
3. **단일 소유자 선출 + ThreadLocal 재진입 예약** — 액터 메일박스의 재귀·교착 문제에 대한 정확한 해법

셋 다 **설계는 승계하고 구현은 재작성**한다. 예외 안전성과 리소스 소유권만 보강하면 상업용 수준에 도달한다.
