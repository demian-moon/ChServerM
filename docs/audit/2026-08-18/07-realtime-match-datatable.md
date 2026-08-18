# 감사 07 — 실시간 프리미티브 + 매치메이킹 + 데이터테이블 (RealTime · Rooms · Spatial · Matchmaking · DataTable)

> 전수 감사 2026-08-18. 대상: `ChServerM.RealTime` · `ChServerM.RealTime.Rooms` ·
> `ChServerM.RealTime.Spatial` · `ChServerM.Matchmaking` · `ChServerM.DataTable` ·
> `Samples/ChServerM.Samples.GameRoom` 전 파일 정독. 우선순위: P0=정확성/1.0 필수 · P1=중요 ·
> P2=권장 · P3=선택. 인덱스: [00-summary.md](00-summary.md)
> ※ TimerWheel 노드 풀 ABA(P0)는 [05-concurrency-bench.md](05-concurrency-bench.md) X-1에 기록.

## 요약

전반 품질은 높다: 틱 루프는 절대 시각 기준 스케줄(원점 + n×간격)로 드리프트 누적을 원천
차단하고 캐치업 상한·스킵 관측을 갖췄으며, `PeriodicTimer` 대신 전용 스레드+스핀 창을 택한
근거(OS 슬립 해상도와 sub-ms 지터, BENCHMARKS 실측)가 옵션 문서에 수치로 남아 있다. 타이밍
휠의 세대+상태 단일 워드 CAS(ABA 차단), Room의 copy-on-write 스냅샷, BroadcastFrame의 참조
계수+이중 해제 검출, InterestSet의 스왑-클리어 무할당 차분, DataTable의 로딩 시점 전수 검증·오류
일괄 보고·XxHash128 지문은 모두 정석적이고, 9.8 규약대로 좌표는 자체 struct가 아닌
`System.Numerics.Vector2`다. 선택 축 격리도 규약대로다(하단 검증 결과).

다만 **틱 루프의 `MaxCatchUpTicks=0` 경로에 정확성 버그(정상 상태에서 틱 절반을 건너뜀)** 가
있고, 타이머 휠의 취소 노드 지연 회수, 매치메이커의 최악 O(n²) 패스, 스냅샷 리더의 rowCount
기반 선할당은 1.0 전에 손보는 것이 좋다.

## 발견 사항

### [P0] R-1. `MaxCatchUpTicks=0`이면 정시 루프도 틱을 하나 걸러 하나 건너뛴다

- **위치**: `Server/ChServerM.RealTime/TickLoop.cs:182-189`
- **현재 구현**: 틱 실행 후 `behindTicks = (now − 다음마감)/간격 + 1`로 밀림을 계산한다. 정시
  실행이면 `now − 다음마감`이 음수인데, C# 정수 나눗셈은 0으로 절단되므로
  `behindTicks = 0 + 1 = 1`이 된다.
- **문제**: `MaxCatchUpTicks = 0`(옵션 문서가 명시 허용: "0이면 캐치업하지 않는다")일 때
  `1 > 0`이 항상 참이라 **완전히 정시인 루프가 매 반복 틱 1개를 건너뛴다** — 틱 0, 2, 4…만
  실행되어 실효 주파수가 절반이 되고 `SkippedTicks` 메트릭이 계속 오염된다. 기존
  테스트(`TickLoopTests.cs:139-162`)는 "틱 번호가 점프한다"만 단언해서 이 버그가 있어도(오히려
  버그 때문에) 통과한다. `MaxCatchUpTicks ≥ 1`에서는 `+1` 과대 계산이 발현되지 않는다.
- **대안**: `long delta = now − 다음마감; long behindTicks = delta >= 0 ? delta / _intervalRaw + 1 : 0;`
  로 고치고, "정시 루프 + Max=0에서 스킵 0" 회귀 테스트 추가.
- **1.0 전 필수**: **필수** (정확성 버그). / **난이도**: 낮음

### [P1] R-2. 두 번째 `DisposeAsync`가 루프 종료 신호를 조기 완료시킨다

- **위치**: `Server/ChServerM.RealTime/TickLoop.cs:146-160`
- **현재 구현**: `Interlocked.Exchange(_state, Disposed)`의 반환값이 `StateRunning`이 아니면 else
  분기에서 `_stopped.TrySetResult()`를 부른다. 이 분기는 "시작한 적 없음"을 의도했지만 "이미
  Disposed"(두 번째 호출)도 여기로 온다.
- **문제**: DisposeAsync가 두 번(특히 동시에) 불리면, 두 번째 호출이 루프 스레드가 아직 마지막
  틱을 실행 중인데도 `_stopped`를 완료시켜 첫 번째 호출자의 `await`가 조기 반환된다. "실행 중인
  틱은 끝까지 완주하고 그때까지 기다린다"는 문서 계약 위반 — 종료 경로에서 핸들러와 후속
  정리가 겹칠 수 있다.
- **대안**: `TrySetResult()`는 `previous == StateCreated`일 때만 부르고,
  `previous == StateDisposed`면 `await _stopped.Task`로 합류.
- **1.0 전 필수**: 필수 권장 (종료 경로 경합, 수정 1줄). / **난이도**: 낮음

### [P1] R-3. 취소된 타이머 노드가 슬롯 도달까지 회수되지 않아 무제한 누적 가능

- **위치**: `Server/ChServerM.RealTime/Timers/TimerWheel.cs:302-323` (특히 321행 주석),
  `TrySchedule`의 `_pendingCount` 계산
- **현재 구현**: 취소는 상태 CAS만 하고 물리적 슬롯 제거·풀 반납은 드라이버가 해당 슬롯에
  도달했을 때 한다(드라이버 전용 계약을 지키기 위한 의도적 설계). 취소 시 `_pendingCount`는
  즉시 감소.
- **문제**: "긴 지연 예약 → 즉시 취소 → 재예약" 패턴(세션 타임아웃 연장, 쿨다운 리셋 등
  실무에서 가장 흔한 타이머 사용)에서 죽은 노드가 마감 슬롯이 지날 때까지 링크에 남는다.
  `MaxPendingTimers`는 pending만 세므로 **취소-미회수 노드의 메모리는 어떤 상한에도 걸리지
  않는다** — 1시간짜리 타이머를 초당 1천 번 갱신하면 시간당 360만 개의 죽은 노드가 슬롯에
  쌓인다. 9.6("무제한 금지")의 사각지대.
- **대안**: Netty HashedWheelTimer 방식으로 취소 노드를 별도 MPSC 스택에 넣고 `Advance`
  서두에서 드레인·언링크(드라이버 전용 계약 유지). 슬롯이 단일 연결 리스트라 언링크에 prev
  포인터 또는 슬롯 재구성이 필요하니, 차선으로 "취소 노드 수" 카운터를 두고 임계 초과 시 전
  슬롯 청소 패스.
- **1.0 전 필수**: 재예약형 워크로드를 1.0 범위에 넣는다면 필수, 아니면 문서에 한계 명시 후 1.x.
- **난이도**: 중간

### [P1] R-4. 매치메이커 패스의 최악 비용이 O(n²)이고, 성공 시 앵커 0부터 재시작한다

- **위치**: `Server/ChServerM.Matchmaking/Matchmaker.cs:141-170` (RunPass), `194-283` (TryBuildMatch)
- **현재 구현**: 매치가 하나도 성립하지 않는 패스도 모든 앵커(최대 4,096)에 대해 후보 수집
  O(n)을 돈다 → 패스당 최대 ~1,670만 회의 `AreCompatible` 호출. 매치 성립 시 `while(progress)`가
  앵커 0부터 전체 재스캔. 취소·만료도 `List.RemoveAt` O(n)이라 대량 만료 시 O(n²).
- **문제**: 이 큐는 틱 루프가 드라이버다(모듈 문서). 기본 `MaxQueueDepth=4096`에서 최악 패스가
  수백 ms~초 단위로 틱 예산을 통째로 태울 수 있다. 큐 깊이로 유계라 붕괴는 아니지만, "패스당
  비용 유계"(ADR-0068 결정 4)의 '유계'가 실용 예산과 몇 자릿수 차이 난다. 또한 티켓 제거는 후보
  집합을 줄이기만 하므로 이미 실패한 앞선 앵커는 같은 패스에서 다시 성공할 수 없다 — 0부터
  재시작은 순수 낭비.
- **대안**: ① 레이팅 정렬 보조 인덱스(또는 버킷)로 앵커 창 범위를 이진 탐색해 후보 수집을
  O(창 내 인원)으로 축소, ② 매치 성립 후 현재 앵커 인덱스에서 재개(인덱스 보정), ③ 패스당 검사
  수/시간 상한 옵션. 벤치마크(큐 깊이 축)로 before/after.
- **1.0 전 필수**: ②는 필수 권장(공짜 개선), ①은 측정 후. / **난이도**: ② 낮음, ① 중간

### [P1] R-5. 스냅샷 리더가 와이어의 rowCount를 믿고 배열을 선할당한다

- **위치**: `Server/ChServerM.DataTable/StaticTableSnapshot.cs:258-265` (rowCount 읽기),
  `347-370` (Materialize의 `new string?[rowCount * strings]` 등)
- **현재 구현**: 내부 `Reader`는 문자열 길이에 대해 "길이를 믿고 배열을 잡지 않는다"(590행)를
  지키지만, `rowCount`는 `>= 0`만 검사하고 값 바이트를 한 개도 읽기 전에 `rowCount × 종류별 열
  수` 크기의 배열 4개를 할당한다.
- **문제**: 손상되거나 악의적인 스냅샷(`rowCount ≈ int.MaxValue`)이 수 GB 할당 → OOM. 이 형식의
  설계 의도가 서버→클라이언트 전송이므로 받는 쪽은 신뢰 경계 밖 바이트를 다루는 셈이고, 리더
  자신이 선언한 원칙과 모순된다.
- **대안**: 행당 최소 바이트 수(bool 1B, 정수/실수 8B, 문자열 4B 길이 접두)를 스키마에서 계산해
  `rowCount ≤ 남은 바이트 / 최소 행 크기`를 Materialize 전에 검증. `tableCount`도 같은 방식으로.
- **1.0 전 필수**: **필수** (형식이 와이어에 나가기 전이 고치기 가장 싸다). / **난이도**: 낮음

### [P2] R-6. `Room.Disband`가 멤버 스냅샷을 돌려주지 않아 해산 창의 멤버가 조용히 증발한다

- **위치**: `Server/ChServerM.RealTime.Rooms/Room.cs:131-147`, `RoomDirectory.cs:77-87`
- **현재 구현**: `Disband()`는 멤버 수(int)만 반환하고 배열을 비운다. "닫힘 통지는 해산 전에 앱이
  브로드캐스트한다"가 계약. `RoomDirectory.TryDisband`는 `TryRemove` 후 `Disband()`를 부른다.
- **문제**: 앱의 사전 통지 ~ `Disband()` 락 획득 사이에 `TryJoin`이 끼어들면(룸 참조를 이미 쥔
  스레드) 그 멤버는 `Joined`를 받고도 통지 없이 제거된다. 디렉터리에서 이미 빠진 룸이라 같은
  ID로 새 룸이 또 생길 수도 있다. 반환값이 count뿐이라 앱이 사후 수습할 방법이 없다.
- **대안**: `Disband()`가 해산 시점의 `IRoomMemberSink[]` 스냅샷을 반환하도록 변경(앱이 그
  목록으로 마지막 통지/정리 가능). **public API 변경이므로 지금이 싸다.**
- **1.0 전 필수**: API 모양 변경이므로 1.0 전 강력 권장 (Shipped 이후엔 파괴적 변경).
- **난이도**: 낮음

### [P2] R-7. `FramesRejected` 메트릭이 큐 포화 시 이중 집계된다

- **위치**: `Server/ChServerM.RealTime.Rooms/PartitionedMemberSink.cs:107-112`,
  `RoomBroadcaster.cs:119-124`
- **현재 구현**: 큐 포화 거부를 싱크의 `TryDeliver`가 `RoomMetricNames.FramesRejected`로 1회
  세고, 브로드캐스터도 같은 이름으로 `rejected` 합계를 다시 센다.
- **문제**: 양쪽에 같은 `IMetricsSink`를 꽂으면(자연스러운 구성) QueueFull 거부 1건이 2로
  집계된다. `Closed` 거부는 브로드캐스터만 세므로 두 카운터의 의미가 어긋나 있다 — 알람
  임계값이 흔들린다.
- **대안**: 집계 책임을 한 곳(브로드캐스터)으로 통일하고, 싱크 쪽은 `sink.queue.full` 같은 별도
  이름으로 분리하거나 제거.
- **1.0 전 필수**: 권장 (메트릭 이름은 대시보드 계약). / **난이도**: 낮음

### [P2] R-8. InterestGrid 질의가 후보마다 Dictionary 조회, 셀 제거가 선형 탐색

- **위치**: `Server/ChServerM.RealTime.Spatial/InterestGrid.cs:162-176` (질의),
  `257-271` (RemoveFromCell)
- **현재 구현**: 셀 리스트는 `ObjectId`만 담고 위치는 `_entries` 딕셔너리에 있어, 반경/영역
  질의의 정밀 필터가 후보 1개당 해시 조회 1회를 낸다. `RemoveFromCell`은 `List.IndexOf`
  O(셀 밀도) 후 스왑 제거. 좌표는 `Vector2`라 개별 `DistanceSquared`는 SIMD 가속을 받지만,
  AoS(딕셔너리) 레이아웃 탓에 9.8이 말하는 배치 SIMD 필터링(`Vector<float>`로 후보 N개 동시
  판정)은 구조적으로 불가능하다.
- **문제**: AOI는 매 틱 × 관찰자 수만큼 도는 핫패스다. 밀집 셀(브로드캐스트가 몰리는 지점)에서
  후보당 해시 조회가 지배 비용이 된다. 이동 시 셀 전이도 밀집 셀에서 IndexOf가 비싸진다.
- **대안**: ① 셀 리스트를 `(ObjectId, Vector2)` 쌍으로 바꿔 질의의 딕셔너리 조회 제거(이동 시 두
  곳 갱신 비용과 트레이드오프 — 벤치마크로 판정), ② 셀 내 인덱스를 Entry에 저장해 IndexOf
  제거, ③ 셀별 SoA(X[]/Y[])로 가면 `Vector<float>` 배치 거리 판정까지 열린다. 전부 "측정 없는
  최적화 금지" 대상이므로 Bench에 AOI 밀도 축 벤치마크를 먼저 추가. 2D 전용이라는 사실도
  문서에 명시할 가치.
- **1.0 전 필수**: 아니오 (내부 구현이라 사후 교체 가능). / **난이도**: 중간

### [P2] R-9. TickLoop 기본 옵션 조합이 자체 실측상 "효과 없는" 스핀 구성이다

- **위치**: `Server/ChServerM.RealTime/TickLoopOptions.cs:22-29, 46-63`
- **현재 구현**: 기본값이 틱 50ms + 스핀 창 1ms인데, 같은 파일 주석이 "구간이 OS 슬립
  해상도(15.6ms)보다 작으면 스핀 구간을 통째로 건너뛰어 효과가 없다(50ms 틱 + 1ms 스핀에서 p99
  13.8ms — 순수 슬립과 사실상 같다)"를 실측으로 못박고 있다.
- **문제**: 기본값이 문서화된 함정 그 자체. 사용자는 "스핀 창이 있으니 지터가 억제된다"고
  믿지만 기본 구성에서는 억제 효과가 없다.
- **대안**: 기본을 `Zero`(정직한 순수 슬립)로 내리거나, `Validate`/생성자에서
  "0 < 스핀 창 < 16ms && 틱 간격 > 스핀 창" 조합에 경고 로그.
- **1.0 전 필수**: 기본값 변경은 1.0 전이 적기. / **난이도**: 낮음

### [P3] R-10. TimeSyncExchange가 음수 왕복을 낼 수 있고 RttEstimator가 그걸 예외로 받는다

- **위치**: `Server/ChServerM.RealTime/TimeSync/TimeSyncExchange.cs:73`, `RttEstimator.cs:66-71`
- **문제**: `Compute`는 t₄≥t₁, t₃≥t₂만 검증한다. 두 시계의 진행률 차이·측정 오프셋으로
  `(t₄−t₁) − (t₃−t₂)`가 음수가 될 수 있는데, 그 값을 `RttEstimator.AddSample`에 먹이면
  `ArgumentOutOfRangeException` — 네트워크/시계 조건이 만든 값이 핫패스 예외가 된다.
- **대안**: `Compute`에서 왕복을 0으로 클램프하고 문서화하거나, 결과 struct에 `IsPlausible` 류
  플래그. / **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] R-11. DataTable 조회 자료구조·레이아웃·인터닝의 개선 여지

- **위치**: `Server/ChServerM.DataTable/StaticTable.cs:63, 219-241`, `StaticTableSchema.cs:150`,
  `StaticTableSnapshot.cs:596`
- **내용**: ① 키→행 조회가 `Dictionary<string,int>`(불변인데 일반 사전) —
  `FrozenDictionary<string,int>`가 정확히 이 용도이고 AOT 호환. ② 레이아웃은 종류별 배열
  분리이되 배열 내부는 행 우선(`row × typedCount + colIdx`) — 파일 주석은 "column store, 한 열
  훑기가 캐시에 유리"라고 주장하지만 실제 열 스캔은 stride 접근이다. 실제 접근 패턴이 행
  단위(생성된 행 뷰)라면 현 레이아웃이 오히려 맞으므로 **주석을 레이아웃에 맞게 고치는 쪽이
  먼저**. ③ 스냅샷 복원 시 문자열을 값마다 `GetString`으로 새로 만들어 반복 값(enum성 열)의
  중복 문자열이 인터닝 없이 쌓인다 — 복원 시 열 단위 dedup이 싸게 먹힌다. ④ `GetInt32`의
  `checked` 캐스트는 Int64 열을 Int32로 읽을 때 조회 시점 `OverflowException`을 낼 수 있어
  "조회는 실패하지 않는다" 계약의 예외 사례 — XML 문서에 명시.
- **1.0 전 필수**: 아니오 (전부 내부 구현·문서). / **난이도**: 낮음~중간

### [P3] R-12. CSV 인용 필드의 앞뒤 공백이 Trim으로 소실된다

- **위치**: `Server/ChServerM.DataTable/CsvStaticTableReader.cs:392, 402`
- **문제**: `ParseLine`이 필드 확정 시 `current.ToString().Trim()`을 호출해, 큰따옴표 안에 있던
  앞뒤 공백까지 제거(`"  a  "` → `a`). RFC 4180과 다르고, 인용의 존재 이유(공백 보존) 절반이
  무효인데 문서에 이 동작이 없다.
- **대안**: 인용 필드는 Trim을 건너뛰거나, 지원 범위 주석에 명시.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

## 선택 축 격리 검증 결과 (이상 없음)

- `RealTime`·`RealTime.Rooms`·`RealTime.Spatial`·`Matchmaking` → `ChServerM.Core`만
  참조(어댑터→Core 방향, 합법). `DataTable` → Core조차 참조 없음(`System.IO.Hashing`뿐) —
  CLAUDE.md 레이아웃 명세와 일치하며, 지문의 Core 타입 변환을 앱에 넘기는 경계도 지켜졌다.
- Core에는 ProjectReference가 0개(역참조 불가), 이 축들을 참조하는 곳은 Tests/Bench/Samples뿐이고
  메타 패키지(`Server/ChServerM/ChServerM.csproj`)가 선택 축 배제를 주석으로 명시 — **전부 빼도
  성립하는 구조가 csproj 수준에서 검증된다.**
- Rooms가 쓰는 `IFrameEncoder`/`IExecutionPartition`은 Core의 프레이밍·실행 추상화라 규약 위반이
  아니다. Samples.GameRoom은 룸 축의 3대 계약(1회 인코딩, 룸 격리, 세 갈래 퇴장 합류)을 자체
  검증으로 실증하고 AOT publish 게이트에도 걸려 있다.
