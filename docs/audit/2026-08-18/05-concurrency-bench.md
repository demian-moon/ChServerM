# 감사 05 — 동시성 실행 모델 + 벤치마크 방법론 (`ChServerM.Concurrency` · `Bench/`)

> 전수 감사 2026-08-18. 대상: `Server/ChServerM.Concurrency/` · `Bench/ChServerM.Bench/` ·
> `Bench/ChServerM.Bench.LoadRunner/` + Core `IExecutionModel` 계약 + TimerWheel(RealTime) 정독.
> 우선순위: P0=정확성/1.0 필수 · P1=중요 · P2=권장 · P3=선택. 인덱스: [00-summary.md](00-summary.md)

## 요약

동시성 실행 모델(파티션 샤딩)과 벤치마크 방법론은 **전반적으로 매우 높은 수준**이다. 9절 규약의
핵심 3종(파티셔닝 우선, `finally` 상태 복원, 유계+거부)이 코드에 실제로 구현돼 있고,
증가-후-검사 방식의 엄격한 유계, 무할당 대기 경로(`WaitForWork`), `IThreadPoolWorkItem` 기반
무할당 완료 신호, 타이밍 휠의 세대+상태 단일 워드 CAS와 드라이버 전용 슬롯 설계는 레거시
결함(TimingWheelSlotM 비일관 Volatile, ExecutableTaskDispatcherM 영구 정지)의 재발을 구조적으로
막고 있다. 벤치 방법론도 이례적으로 정직하다 — 스핀 제거 A/B 기각 기록, SMT 형제 마스크 발견,
워밍업 오염 발견, 바닥선을 상대에게 유리하게 기울인 프레임워크 세금 측정, 생성기 CPU 기록까지.

다만 **타이밍 휠 노드 풀의 Treiber 스택 pop에 고전적 ABA 결함**이 하나 있고(발생 확률은 극히
낮으나 정확성 버그), Bench 프로젝트 2곳에 ADR-0031이 경고한 바로 그 GC 오타 속성이 잔존하며,
기준선 표의 환경 ID 오기 등 문서·설정 정합성 문제가 몇 건 있다.

## 발견 사항

### [P0] X-1. TimerWheel 노드 풀 Treiber 스택 pop의 ABA — 재사용 노드가 활성 상태로 풀에 재진입할 수 있다

- **위치**: `Server/ChServerM.RealTime/Timers/TimerWheel.cs:523-543` (`RentNode`)
- **현재 구현**: `_poolHead`에서 `Volatile.Read(head)` →
  `Interlocked.CompareExchange(ref _poolHead, head.StackNext, head)`로 pop한다.
  push(`ReturnNode`)는 드라이버 단일 스레드지만 **pop(`TrySchedule` 경유)은 임의의 다중 스레드**다.
- **문제**: 고전적 ABA. T1이 `head=A, A.StackNext=B`를 읽은 직후 선점되고, 그 사이 T2가 A를
  rent→예약, T3가 B를 rent→예약(활성 타이머), 드라이버가 A를 발화·반납(`A.StackNext=C`로
  재push)하면, T1의 CAS는 `head==A`로 **성공**하며 낡은 B를 헤드로 설치한다. B는 살아 있는
  Pending 타이머인데 풀에 들어가고, 다음 rent가 B를 이중 사용한다. B는 `ReturnNode`를 거치지
  않아 **세대가 증가하지 않았으므로 세대 가드가 이 경우를 잡지 못한다** — `StackNext`가 유입
  스택과 풀 스택에 공유되므로 슬롯/스택 리스트가 조용히 오염된다(타이머 유실·오발화). 창은 수
  ns지만 OS 선점이 그 지점에서 일어나면 ms 단위로 벌어진다 — 상업용 1.0 프레임워크에서 감수할
  수 없는 종류의 비결정 버그다.
- **대안**: 풀만 `ConcurrentQueue<TimerNode>`로 교체(같은 코드베이스의 `WorkBoxPool`이 이미 쓰는
  ABA-안전 패턴, `ExecutionPartition.cs:615-644`). 풀 op는 타이머당 1회(프레임 핫패스 아님)라
  비용 영향은 미미하다. 참고: `_incomingHead`는 push+`Interlocked.Exchange` 전량 드레인이라
  ABA-안전하고, `PushStack`도 push 전용이라 안전 — 결함은 pop 하나뿐이다.
- **1.0 전 필수**: **예**. / **난이도**: 낮음

### [P2] X-2. Bench 프로젝트 2곳에 ADR-0031의 오타 GC 속성(`...Collector`)이 잔존

- **위치**: `Bench/ChServerM.Bench/ChServerM.Bench.csproj:25-26`,
  `Bench/ChServerM.Bench.LoadRunner/ChServerM.Bench.LoadRunner.csproj:19-20`
- **현재 구현**: `<ServerGarbageCollector>true</ServerGarbageCollector>` /
  `<ConcurrentGarbageCollector>true</ConcurrentGarbageCollector>` — MSBuild가 조용히 무시하는
  바로 그 오타(ADR-0031). 루트 `Directory.Build.props:63-64`가 올바른 `...Collection`으로 고쳐져
  있어 **현재 산출물은 ServerGC가 맞음을 확인**(`*.runtimeconfig.json`의 `System.GC.Server: true` 실측).
- **문제**: 지금은 무해하지만 "선언은 있는데 아무것도 하지 않는" 줄이 3개월 Workstation GC
  사고의 원인과 동일 형태로 남아 있다. 루트 props가 바뀌거나 이 블록이 복사되는 순간 재발한다.
- **대안**: 두 csproj에서 해당 줄 삭제(루트가 이미 설정) 또는 `...Collection`으로 정정.
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P2] X-3. `RoutingBenchmarks`에 `[Config(typeof(BenchConfig))]` 누락 — Program.cs가 경고한 바로 그 함정

- **위치**: `Bench/ChServerM.Bench/Cluster/RoutingBenchmarks.cs:28-29`
- **문제**: 기본 job으로 돌아 ServerGC job 고정·`Core10_0` 지정·`StopOnFirstError`·게이트 모드
  JSON 내보내기가 전부 빠진다. bench-gate.json 대상은 아니라 CI 게이트는 무사하지만, 이 클래스의
  수치가 BENCHMARKS.md에 실리면 다른 측정과 조건이 다른 채로 비교된다.
- **대안**: 속성 추가 + 재발 방지 장치(벤치 어셈블리 리플렉션 테스트 1개 — `[Benchmark]` 보유
  타입에 `[Config]` 존재 확인)를 Tests에 추가.
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P2] X-4. 기준선 표의 환경 ID 오기 — 169,180 RPS 행이 ENV-A로 적혀 있으나 원 기록은 ENV-B

- **위치**: `docs/BENCHMARKS.md:40-42`(기준선 표) vs `:416-418`(2026-08-04 원 기록 "환경: ENV-B"),
  전파: `docs/GUIDE-PERFORMANCE.md:21`, `docs/standup/history/2026-08-04.md:130`
- **문제**: 원 기록과 스탠드업이 모두 ENV-B라고 말한다. 회귀 판정의 기준인 기준선 표의 환경
  표기가 틀리면 미래의 재측정 비교가 잘못된 머신을 기준으로 이뤄진다.
- **대안**: 사실 확인 후 표·가이드 정정(정황상 ENV-B가 맞다).
- **1.0 전 필수**: 권장(문서 수정만). / **난이도**: 낮음

### [P2] X-5. 지연 측정이 닫힌 루프뿐 — 고정 도착률(개방 루프) 모드 부재

- **위치**: `Bench/ChServerM.Bench.LoadRunner/Program.cs:382-441`, 서술: `docs/BENCHMARKS.md:423`
- **현재 구현**: 워커가 요청→응답→다음 요청의 닫힌 루프. 파이프라인 모드(burst)는 있으나 고정
  도착률 모드는 없다.
- **문제**: 닫힌 루프에서 각 표본이 진짜 RTT인 것은 맞아 고전적 coordinated omission은 없다.
  그러나 서버가 잠시 멈추면 **그 구간 동안 클라이언트도 요청을 멈추므로** 정체 구간이 워커당
  표본 1개로만 대표된다 — 개방계의 체감 p99/p999보다 낙관적이다. 파이프라이닝 938k RPS
  발견("닫힌 루프의 상한이 왕복 대기였다")이 이미 이 한계를 간접 증언한다. 부수적으로 부하 중
  **서버 프로세스 CPU%는 기록되지 않는다**(생성기 CPU만).
- **대안**: wrk2 방식의 고정 도착률 모드(예정 송신 시각 기준 지연 계산, 밀린 예정분도 표본화)를
  LoadRunner에 추가하고, 1.0 지연 주장(p999 포함)은 그 모드로 재확인. 서버 모드의 5초 주기
  출력에 CPU%(`Process.TotalProcessorTime` 델타) 추가.
- **1.0 전 필수**: 지연 분위수를 상업적 주장으로 쓸 거라면 권장, 처리량 주장에는 불필요.
- **난이도**: 중간

### [P3] X-6. 파티션 캐시 라인 패딩의 비일관 — `_pendingExternalWork`만 패딩

- **위치**: `Server/ChServerM.Concurrency/ExecutionPartition.cs:101-111`(패딩 근거 주석),
  `:110-132`(`_executedCount`, `_currentItemStartedTicks` — 비패딩), `:548-558`(`PaddedCounter` —
  128B·FieldOffset 64, 올바른 구현)
- **문제**: 패딩 주석의 근거("파티션은 별도 객체지만 GC가 인접 배치할 수 있다")가
  `_executedCount`(항목당 `Interlocked.Increment`)와 `_currentItemStartedTicks`(항목당
  `Volatile.Write` 2회)에도 똑같이 적용된다. 단, 16코어 14.90×/93.1% 실측이 실해가 크지 않음을
  보여주므로 개선 여지이지 결함이 아니다.
- **대안**: 항목당 갱신 가변 필드들을 패딩 블록 하나로 묶는다. **측정 없는 최적화 금지 원칙대로
  확장성 곡선 before/after 없이는 하지 않는다.**
- **1.0 전 필수**: 아니오. / **난이도**: 낮음(변경)·중간(효과 검증)

### [P3] X-7. `WorkBoxPool` 파티션 간 공유 + 상한 1,024의 churn — 이미 실측·문서화된 기지 항목

- **위치**: `Server/ChServerM.Concurrency/ExecutionPartition.cs:615-644`, 실측:
  `docs/BENCHMARKS.md:270-278`(P=8에서 게시당 70B)
- **판단**: 보조 경로(타이머 주입 등 저빈도) 전제 위에서 수용 가능하고, 그 전제와 탈출 조건이
  코드·문서 양쪽에 있다. 타이머 만료를 파티션에 대량 주입하는 조립이 생기면 그때 파티션별 풀로.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] X-8. `IPartitionExclusiveWork.ExecuteAsync`의 동기 예외(계약 위반) 시 게시자 영구 대기

- **위치**: `Server/ChServerM.Concurrency/ExecutionPartition.cs:443-446, 473-478`, 계약:
  `Server/ChServerM.Core/Execution/IPartitionExclusiveWork.cs:23-27`
- **문제**: 배타 작업이 동기로 예외를 던지면 소비 루프의 항목별 catch가 삼키고 로그만 남긴다.
  파티션은 무사하지만 게시자의 `IValueTaskSource`는 영원히 완료되지 않아 그 커넥션의 읽기
  루프가 영구 대기한다. 실물 구현(`PartitionDispatchGate`)은 계약을 지킨다 — 문제는 서드파티가
  `IExecutionPartition`(public API)에 직접 자기 구현을 넣을 때다. 정지 감지(`IsStalled`)도 못
  잡는다(파티션 표식은 finally로 정상 해제).
- **대안**: 최소한 `LogWorkFaulted` 메시지에 "배타 작업의 예외 유출 = 게시자 영구 대기 가능,
  계약 위반"을 명시. 더 나아가면 배타 작업 실행을 자체 try/catch로 감싸 별도 EventId로 구분 방출.
- **1.0 전 필수**: 아니오(로그 문구 개선은 저비용이라 권장). / **난이도**: 낮음

### [P3] X-9. `PartitionedExecutionModel.DisposeAsync`가 동기 블로킹(`Thread.Join`)

- **위치**: `Server/ChServerM.Concurrency/PartitionedExecutionModel.cs:269-291` →
  `ExecutionPartition.cs:332-361`
- **판단**: `DisposeAsync`(비동기 표면)가 내부에서 최대 `ShutdownTimeout`(기본 5초)까지 호출
  스레드를 `Join`으로 블로킹. 종료 경로 한정이라 실해 작음. 공유 데드라인 설계는 훌륭. 필요해지면
  `Join`을 대기 루프+`Task.Delay`로 비동기화 — 지금은 유지해도 무방.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] X-10. `[MemoryDiagnoser]` 중복 선언 — BenchConfig가 이미 추가한다

- **위치**: `Bench/ChServerM.Bench/RealTime/TimerWheelBenchmarks.cs:22-23`,
  `RoomBroadcastBenchmarks.cs:31-32`, `Framing/FragmentAssemblyBenchmarks.cs:46-47`,
  `Buffers/PooledBufferWriter*Benchmarks.cs`, `Observability/*OverheadBenchmarks.cs`,
  `Compression/CompressionBenchmarks.cs`, `Cluster/PeerLink*Benchmarks.cs` 등
- **판단**: `[Config]`와 `[MemoryDiagnoser]`를 함께 붙인 클래스와 안 붙인 클래스 혼재. 실측 영향
  없음 — `[MemoryDiagnoser]` 중복 제거로 통일(BenchConfig 단일 근원).
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] X-11. LatencyHistogram 분위수가 버킷 하한을 반환 — 체계적 하향 편향

- **위치**: `Bench/ChServerM.Bench.LoadRunner/Program.cs:957-992`
- **문제**: 로그-선형 버킷에서 rank 도달 버킷의 **하한**을 보고 — 항상 실제보다 작거나 같은 값.
  성능 주장에 쓰는 수치라면 보수적(상한 또는 중점) 쪽이 원칙에 맞다.
- **대안**: 버킷 상한(다음 경계) 보고로 변경. 기준선 수치가 미세하게 올라가므로 변경 시점을 기록.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

## 관점별 판정 (발견 사항 외 확인 내역)

1. **파티셔닝 실행 모델 — 합격.** 피보나치 해싱(곱셈-시프트 축소로 나눗셈·음수·2^n 제약 없음),
   슬롯 재사용 커넥션의 파티션 고정(`ConnectionId.ToPartitionKey`가 slot만 사용), 채널은
   무제한이지만 외부 유입만 증가-후-검사 카운터로 엄격 유계(9.6의 의도를 정확히 구현 —
   `TryWrite`+`Wait` 조합의 레거시 함정 자체를 회피), 항목별 try/catch와 `finally` 복원 모두 준수.
   종료는 전 파티션 `SignalStop` 후 공유 데드라인 드레인.
2. **false sharing** — `PaddedCounter`는 128B(인접 라인 프리페치까지) 정석 구현. 비일관 적용은 X-6.
3. **락-프리 품질** — TimerWheel의 세대|상태 단일 워드 CAS는 핸들 ABA를 정확히 막고, `Volatile`
   일관 적용·재시도 시에만 SpinWait 규약(9.3)을 지킨다. 유일한 실결함이 노드 풀 pop ABA(X-1).
   레거시 TimingWheelSlotM 결함(비일관 Volatile) 재발 없음.
4. **최신 대안** — 핫패스에 락이 없어 `System.Threading.Lock`은 해당 없음(올바름). 파티션=전용
   스레드 구조는 사실상 thread-per-core이며, 프레임당 채널 왕복 비용은 +0.26µs(바쁜 소비자
   실측)로 Vyukov MPSC 링 교체의 근거가 현재 없다. 워크 스틸링 비적용은 순서 보장 계약상
   타당하고 파티션별 executed/pending 진단으로 스큐가 관측 가능하다.
5. **벤치마크 방법론 — 합격.** ServerGC job 고정+MemoryDiagnoser+선언 순서+StopOnFirstError를
   설정으로 강제, 확장성은 프로세스 어피니티 실제 코어 제한(+SMT 형제 인접쌍 발견, 1코어 평탄
   대조군)으로 측정, 비율 게이트의 원리적 한계(두 팔 동반 퇴화)를 문서에 명시. LoadRunner는 별도
   프로세스+생성기 CPU 기록으로 측정자 병목을 방어(35-48% 비포화 확인). 남은 약점은 개방 루프
   부재(X-5)와 절대값 게이트 부재(문서에 이미 정직하게 명시됨).
6. **타이머/스케줄링 — 합격(X-1 제외).** 슬롯은 드라이버 전용으로 동기화 자체를 제거(9.1),
   커넥션당 타이머 없음, TickLoop은 절대 스케줄+캐치업 상한+틱 단위 격리+`finally` 신호, 스핀
   구간은 `SpinOnce(sleep1Threshold: -1)`로 Sleep(1) 승격 차단.
