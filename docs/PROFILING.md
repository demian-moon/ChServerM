# 프로파일링 워크플로

CPU·할당·GC 프로파일을 **어떻게 뜨고 어떻게 읽는가**. 이 문서의 모든 절차와 수치는
ENV-B 에서 실제로 실행해 얻은 것이다 — 추측으로 쓴 단계는 없다.

> **이 문서의 한 줄 요약**: 프로파일에서 가장 큰 항목이 문제라는 보장은 없다.
> 프로파일은 **어디를 볼지** 알려줄 뿐이고, **고칠지 말지는 A/B 측정이 정한다.**
> 아래 워크드 예제가 정확히 그 사례다(CPU 25% 항목이 알고 보니 이득이었다).

---

## 0. 프로파일링 전에 — 게이트가 먼저다

프로파일러를 켜기 전에 이미 있는 것부터 본다. 프로파일링은 비싸고(수집·변환·해석)
결과가 모호한 반면, 게이트는 결정적이다.

| 질문 | 먼저 볼 것 | 프로파일러가 필요한 때 |
|---|---|---|
| 할당이 늘었나? | `DispatchAllocationGateTests` (결정적, 상시) | 게이트가 잡은 뒤 **어디서** 나는지 |
| 설계 주장이 깨졌나? | `eng/bench-gate.ps1` (CI 상시) | 비율이 왜 나빠졌는지 |
| 확장성이 무너졌나? | `eng/scaling-gate.ps1` | 어느 공유 자원에서 막히는지 |
| 처리량이 왜 이 수준인가? | `docs/BENCHMARKS.md` 기준선 | **여기서부터가 프로파일러의 몫이다** |

**게이트가 전부 통과하는데 "느린 것 같다" 면 그것이 프로파일링을 시작할 시점이다.**

---

## 1. 도구 설치

```bash
dotnet tool install --global dotnet-trace      # CPU·이벤트 추적
dotnet tool install --global dotnet-counters   # 라이브 카운터 (가장 싸다)
dotnet tool install --global dotnet-gcdump     # 힙 스냅샷 (누수 추적)
```

되돌릴 때는 `dotnet tool uninstall --global <이름>`.

---

## 2. CPU 프로파일

### 2.1 절차

측정 대상은 **정상 상태의 서버**다. 기동·JIT 승격 구간을 프로파일하면 워밍업을 프로파일한
것이지 서버를 프로파일한 것이 아니다.

```bash
# ① 서버를 띄운다
Bench/ChServerM.Bench.LoadRunner/bin/Release/net10.0/ChServerM.Bench.LoadRunner.exe \
    server --port 15301 --partitions 32 --seconds 60 &

# ② PID 를 찾는다 (셸의 백그라운드 PID 와 다를 수 있다)
dotnet-trace ps | grep ChServerM.Bench.LoadRunner

# ③ 부하를 건다
... client --port 15301 --connections 512 --active 512 --payload 128 --seconds 35 --rampup 4 &

# ④ 부하가 정상 상태에 든 뒤(램프업 + 여유) 수집한다
sleep 10
dotnet-trace collect --process-id <PID> --duration 00:00:00:20 \
    --format Speedscope --output server.nettrace \
    --profile dotnet-sampled-thread-time
```

산출물은 `server.nettrace`(원본)와 `server.speedscope.json`(뷰어용)이다.
후자는 <https://speedscope.app> 에 드래그하면 바로 열린다(로컬에서 렌더링되며 업로드되지 않는다).

### 2.2 ⚠ 실전 함정

| 함정 | 증상 | 해결 |
|---|---|---|
| **`--profile cpu-sampling` 은 Linux 전용** | `[ERROR] The specified profile 'cpu-sampling' does not apply to 'dotnet-trace collect'` | Windows 는 **`dotnet-sampled-thread-time`**. 목록은 `dotnet-trace list-profiles` (프로파일명 옆의 `(collect-linux)` 표시가 플랫폼 제한이다) |
| 셸 백그라운드 PID ≠ 대상 PID | 붙을 수 없다는 오류 | `dotnet-trace ps` 로 이름으로 찾는다 |
| 기동 직후 수집 | JIT·클래스 로딩이 상위를 점령 | 부하를 걸고 **10초 이상 지난 뒤** 수집 |
| speedscope 파일이 거대 | 20초 수집에 nettrace 9MB → speedscope **95MB** | 정상이다. 뷰어는 견딘다. 스크립트로 집계할 거면 아래 2.3 |

### 2.3 읽는 법 — ⚠ 리프를 그대로 세면 안 된다

`dotnet-sampled-thread-time` 의 스택 **리프는 의사 프레임**이다:

- `CPU_TIME` — 이 스택이 CPU 를 쓰고 있었다
- `UNMANAGED_CODE_TIME` — 커널/런타임 네이티브 코드에 있었다(대개 **대기**다)
- `BLOCKED_TIME` — 블로킹 대기

리프 기준으로 self time 을 세면 상위가 통째로 `CPU_TIME` / `UNMANAGED_CODE_TIME` 이 되어
**아무 정보도 없다**(실제로 그렇게 재면 68% / 32% 두 줄만 나온다). 의사 프레임을 만나면
**스택을 거슬러 올라가 가장 가까운 실제 프레임에 귀속**시켜야 한다.

또 하나: **`UNMANAGED_CODE_TIME` 이 크다고 놀라지 않는다.** 서버는 스레드 대부분이 소켓·
세마포어에서 대기 중이고 그것이 전부 여기로 잡힌다. 90개 스레드 중 일하는 것은 소수다.
**CPU_TIME 만 따로 보는 것이 요점이다.**

집계 스크립트 골자(evented 형식 기준):

```python
PSEUDO = {'CPU_TIME', 'UNMANAGED_CODE_TIME', 'BLOCKED_TIME'}
# 이벤트를 훑으며 현재 스택 top 이 의사 프레임이면
# 스택을 거슬러 첫 실제 프레임에 그 구간 시간을 더한다
owner = next(f for f in reversed(stack) if name(f) not in PSEUDO)
```

### 2.4 ★ 워크드 예제 — 25% 짜리 항목이 문제가 아니었다

**실측 (ENV-B, 512 커넥션 전체 활성, 파티션 32, 20초 수집)** — 관리 코드 CPU 귀속 상위:

| 비중 | 프레임 |
|---:|---|
| **25.32%** | `ExecutionPartition.WaitForWork()` |
| 21.76% | `SocketAsyncEventArgs.DoOperationSendSingleBuffer` |
| 14.67% | `PortableThreadPool+WorkerThread.WorkerThreadStart` |
| 12.46% | `LowLevelLifoSemaphore.Wait` |
| 7.91% | `Thread.PollGCWorker` |
| 7.80% | `PortableThreadPool+IOCompletionPoller.Poll` |
| 2.38% | `Monitor.Enter_Slowpath` |

1위가 **"일을 기다리는 코드"** 다. 원인은 명확했다 — `WaitForWork` 가 쓰는
`ManualResetEventSlim` 이 기본 스핀 횟수를 갖고 있어서, 32개 파티션 스레드가 큐가 빌 때마다
스핀한다. 169k RPS 에서 파티션당 초당 ~5천 프레임이므로 큐는 **끊임없이 비고**, 그때마다
스핀 비용이 붙는다.

"낭비를 없애자" 는 결론이 자연스럽다. **그래서 측정했다** — `spinCount: 0` 으로 바꾸고
같은 조건으로 A/B:

| | RPS (2회) | p50 | p99 |
|---|---:|---:|---:|
| 스핀 기본값 | 155,964 / 156,115 | 2.89ms | 14.90 / 14.10ms |
| **스핀 0** | 148,068 / 149,204 | 3.00ms | 15.90 / 16.00ms |

**스핀을 없애면 처리량 −4.7%, p99 +8% 로 나빠진다.** 그 25% 는 낭비가 아니라
**커널 전이를 피해 깨우기 지연을 줄이는 대가**였다. 변경을 되돌렸다.

> **교훈 — 프로파일은 가설을 만들 뿐 결론을 만들지 않는다.**
> "CPU 를 많이 쓴다" 와 "없애면 빨라진다" 는 다른 명제다. 이 프로젝트의
> "측정 없는 최적화 금지"(CLAUDE.md 2절)는 **프로파일 결과에도 적용된다.**
> 프로파일에서 찾은 것은 반드시 A/B 로 확인하고 나서 고친다.

---

## 3. 할당과 GC

### 3.1 라이브 카운터가 가장 싸다

```bash
dotnet-counters monitor --process-id <PID> --counters System.Runtime   # 대화형
dotnet-counters collect --process-id <PID> --refresh-interval 1 \
    --format csv --output counters.csv --counters System.Runtime       # 파일로
```

**실측 (정상 상태, 512 커넥션 · ~156k RPS)**

| 카운터 | 값 |
|---|---:|
| `dotnet.gc.heap.total_allocated` | **37.3 MB/s** |
| `dotnet.gc.collections` gen0 | 5 /s |
| `dotnet.gc.collections` gen1 | **0** /s |
| `dotnet.gc.collections` gen2 | **0** /s |
| `dotnet.gc.pause.time` | 0 s/s (해상도 아래) |
| `dotnet.process.memory.working_set` | 68.8 MB |
| `dotnet.thread_pool.queue.length` | ~0 |

### 3.2 ⚠ 읽는 법 — 양이 아니라 **승격**을 본다

37.3 MB/s ÷ 156k RPS ≈ **약 240 B/요청**. "프레임당 할당 0" 이라는 주장과 모순처럼 보이지만
아니다:

- 그 주장은 **프레임워크가 얹는 몫**에 대한 것이다(`DispatchAllocationGateTests` 문서).
  여기 벤치 서버의 에코 핸들러는 `async` 이고 `FrameWriter.WriteFrameAsync` 를 await 하므로
  **프레임마다 async 상태 머신을 할당한다** — 설계상 정상이다
- 그리고 **gen1·gen2 수집이 0** 이다. 즉 그 240B 는 전부 gen0 에서 죽는 단명 객체이고
  승격되지 않는다. GC 일시정지도 관측 해상도 아래다

**누수 신호는 할당량이 아니라 `gen2 수집`과 `working_set` 의 추세다.** 할당이 많아도 승격이
없으면 GC 가 설계대로 일하고 있는 것이다. 반대로 할당이 적어도 gen2 가 계속 늘면 문제다.

### 3.3 할당 회귀는 프로파일러로 잡지 않는다

`dotnet-trace --profile gc-verbose` 로 할당 스택을 뜰 수 있지만, **회귀 방어 용도로는
게이트가 낫다.**

| | 프로파일러 | 할당 게이트 |
|---|---|---|
| 결정성 | 샘플링·부하 의존 | **결정적**(`GC.GetAllocatedBytesForCurrentThread`) |
| 비용 | 수집·변환·해석 | 테스트 한 번 |
| 언제 안다 | 사람이 뜰 때 | **매 커밋** |

프로파일러는 **게이트가 회귀를 잡은 뒤 "어디서" 를 찾을 때** 쓴다. 순서를 바꾸지 않는다.

### 3.4 누수 추적 — 힙 스냅샷

```bash
dotnet-gcdump collect --process-id <PID> --output before.gcdump
# ... 부하를 오래 돌린다 ...
dotnet-gcdump collect --process-id <PID> --output after.gcdump
```

두 스냅샷의 타입별 개수를 비교한다. 커넥션 처치(connect→메시지→disconnect)를 반복한 뒤
커넥션 관련 타입이 계속 늘면 **슬롯 누수**다 — `SoakTests` 가 잡는 부류이며, 그 하네스가
먼저 신호를 준 뒤에 gcdump 로 원인을 좁힌다.

---

## 4. 측정 위생 (모든 프로파일에 공통)

1. **Release 빌드**. Debug 프로파일은 인라이닝이 없어 상위 프레임이 통째로 다르다
2. **GC 모드를 확인한다** — 선언이 아니라 `*.runtimeconfig.json` 의 `System.GC.Server` 를
   본다. 오타 하나로 3개월간 Workstation GC 로 돌았던 전례가 있다(ADR-0031)
3. **정상 상태에서 뜬다** — 램프업 + 10초 이상 뒤
4. **생성기 CPU 를 함께 본다**. 루프백 측정에서 생성기가 포화면 프로파일에 잡히는 것은
   서버의 병목이 아니라 생성기의 상한이다(ADR-0009)
5. **같은 머신·같은 조건으로 A/B**. 프로파일 결과의 절대 비중은 머신마다 다르다

---

## 5. 결과를 어디에 남기는가

| 결과 | 남기는 곳 |
|---|---|
| 수치 (before/after) | `docs/BENCHMARKS.md` — 환경 ID·커밋·조건과 함께 |
| 고치기로 한 결정 | `perf(...)` 커밋 본문에 before/after 필수 |
| 고치지 **않기로** 한 결정 | 이 문서 2.4 처럼 **근거와 함께** 남긴다. 안 남기면 다음 사람이 같은 항목을 다시 파고 같은 결론에 도달한다 |
| 설계가 바뀌는 결정 | `docs/DECISIONS.md` ADR |

**"프로파일해 봤더니 문제가 아니었다" 도 결과다.** 기록하지 않으면 그 시간이 반복된다.
