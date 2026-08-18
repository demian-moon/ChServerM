# ChServerM 전수 감사 종합 — 2026-08-18

> **범위**: Server 30개 어셈블리 + Bench + Samples + 빌드 인프라 전체(~35k LOC 프로덕션 코드)를
> 8개 영역으로 나눠 병렬 정밀 감사. 모든 소스 파일을 실제로 정독하고 근거 위치를 `파일:줄`로
> 남겼다. 관점: ① 알고리즘·구현 최신성(.NET 10 BCL 대안), ② 정확성, ③ 구조(축 경계·의존 방향),
> ④ 하드 룰 준수(무할당·병렬성 규약 9절), ⑤ 1.0 Shipped 동결 전 마지막 기회인 API 변경.
>
> **시점 의미**: `PublicAPI.Shipped.txt`가 전 어셈블리에서 비어 있다(전량 Unshipped). 즉 이
> 감사에서 "1.0 전 필수"로 분류된 API 변경은 지금은 Unshipped 편집으로 끝나지만, 1.0 선언
> 후에는 전부 파괴적 변경이 된다. **이 감사가 그 마지막 기회를 위한 목록이다.**

## 반영 현황 (2026-08-18, 감사 직후 세션)

**A(P0) 4건 + B(P1) 10건 + 설계 결정 4건이 같은 날 반영됐다.** 각 수정에 회귀 테스트 동반.

- ✅ A-1~A-4 전부 수정: TickLoop 틱 유실, TimerWheel 풀 ABA(ConcurrentQueue), Consul
  BuildView(사전 검증+충돌 제외+루프 예외 격리+테스트), DispatchStatus `None=0` 재번호
- ✅ B-1~B-10 전부 수정: TLS `HandshakeTimeout`(기본 10초, 끌 수 없음),
  `ICircuitBreaker.ReleaseProbe()`(중립 반납), Consul HttpClient Timeout 정합,
  HealthHttpEndpoint accept 격리, DataTable rowCount/tableCount 그럴듯함 검증,
  TickLoop 이중 Dispose, nullability 어노테이션 3곳, ClusterView `Array.AsReadOnly`,
  재개 응답 상태 바이트 엄격화, ClientBuilder 협상 옵션 값 복사
- ✅ 설계 결정 4건(사용자 확정, 전부 권장안):
  - C-① **노드 0 예약** — `NodeId` 생성자가 0 거부, `IsNone` 추가, `ClusterNode`·Consul
    옵션이 미설정 거부. 노드 번호는 1부터
  - C-② **`FrameCodecCapabilities` 추가** — `IFrameEncoder/IFrameDecoder.Capabilities`
    (Flags·Sequence·ProtocolVersion), CompositionGuard 가 압축·협상의 죽은 조립을 시작
    시점 거부
  - C-③ **`Room.Disband()` → 멤버 스냅샷 반환** + `RoomDirectory.TryDisband` out 오버로드
  - C-④/⑤ VERSIONING.md "Shipped 이동은 1.0에 1회" 명문화 ·
    `MetricNames.BackpressureDuration` 상수 제거(0.x minor)
- ✅ **버전 0.1.0 → 0.2.0 승격** — pack 단계의 API 호환성 게이트가 위 변경들을 파괴적
  변경으로 정확히 검출했다(설계대로). 0.x 정책(ADR-0069)대로 minor 승격 + ApiCompat 억제
  파일 2개(Core 15건·Rooms 1건 — 의도한 변경과 1:1 일치 검수) 생성.
- ✅ 부수: audit 게이트가 신규 공표된 SSH.NET 2025.1.0 High 권고(GHSA-q939-rpr3-3284,
  Testcontainers 전이 의존)를 잡아 2026.0.0 전이 고정으로 해소.
- ✅ **전체 게이트(eng/build.ps1) 6단계 전부 통과** — restore·build·test·pack(ApiCompat)·
  audit·AOT(publish+실행 검증) "모든 단계 통과".
- 남은 것: **D 목록(1.0 전 권장)과 E 백로그는 미착수** — 이 문서의 해당 절이 이후 작업 목록이다.

## 영역별 문서

| 문서 | 영역 | P0 | P1 | P2 | P3 |
|---|---|---|---|---|---|
| [01-core.md](01-core.md) | Core 추상화 (89파일) | 1 | 3 | 4 | 5 |
| [02-hosting.md](02-hosting.md) | Hosting 조립 계층 (40소스) | – | 2 | 6 | 7+ |
| [03-datapath.md](03-datapath.md) | Buffers·Framing·직렬화·LZ4·SourceGen | – | 1 | 3 | 4 |
| [04-transport.md](04-transport.md) | 전송 5종 + TLS | – | 1 | 7 | 7 |
| [05-concurrency-bench.md](05-concurrency-bench.md) | Concurrency + Bench 방법론 | 1 | – | 4 | 6 |
| [06-persistence-cluster.md](06-persistence-cluster.md) | 세션 저장소 3종 + 클러스터·Consul | 1 | 1 | 3 | 2 |
| [07-realtime-match-datatable.md](07-realtime-match-datatable.md) | 틱·룸·AOI·매치·DataTable | 1 | 4 | 4 | 3 |
| [08-observability-build.md](08-observability-build.md) | 관측·분석기·샘플·빌드 인프라 | – | 1 | 7 | 5 |

## 총평

**코드베이스는 1.0에 근접한 품질이다.** 8개 영역 전부에서 하드 룰(핫패스 무할당, 파티셔닝 우선,
`finally` 복원, 유계 큐, `async void`/블로킹 0건)이 실제 코드로 지켜지고 있음을 확인했고, .NET 10
관용구(`FrozenDictionary`, `System.Threading.Lock`, `TimeProvider`, `IIncrementalGenerator`,
`ValueTask` 소켓, zero-byte read, `InlineArray`)가 이미 정착해 있다. "구식 패턴을 쓴 레거시
코드"라 부를 만한 것은 발견되지 않았다 — 발견된 문제는 대부분 **일관성의 미세한 이탈**(원칙을
지킨 99곳과 어긴 1곳)과 **와이어·API 계약을 동결하기 전에만 싸게 고칠 수 있는 것들**이다.

정확성 버그(P0)는 4건으로, 전부 수정 난이도 "낮음"이다. 반면 **설계 결정이 필요한 항목이
4건** 있어 이것부터 정리해야 나머지 작업의 순서가 선다.

## 1.0 전 필수 목록 (우선순위·의존 순서)

### A. 정확성 버그 수정 (P0 — 즉시, 전부 난이도 낮음)

| # | 항목 | 위치 | 요지 |
|---|---|---|---|
| A-1 | **TickLoop `MaxCatchUpTicks=0` 틱 절반 유실** [R-1] | `RealTime/TickLoop.cs:182-189` | 정시 루프가 매 반복 틱 1개를 건너뜀. 음수 delta 미처리 |
| A-2 | **TimerWheel 노드 풀 pop ABA** [X-1] | `RealTime/Timers/TimerWheel.cs:523-543` | 활성 타이머 노드가 풀에 재진입 → 타이머 유실·오발화. 풀을 `ConcurrentQueue`로 |
| A-3 | **Consul BuildView 멤버십 루프 영구 정지** [S-1] | `Cluster.Consul/ConsulClusterMembership.cs:503` | 카탈로그 비정상 데이터(특히 서비스 ID 중복 — 표준 패턴)가 무로그 루프 사망. 검증+격리+테스트 |
| A-4 | **`DispatchStatus.Handled = 0`** [C-1] | `Core/Dispatch/DispatchStatus.cs:19` | 기본값이 "성공"인 유일한 결과 enum. `None=0` 추가·재번호 — 릴리스 후엔 바이너리 파괴적 |

### B. 1.0 전 필수 결함 수정 (P1 — A 다음)

| # | 항목 | 위치 | 요지 |
|---|---|---|---|
| B-1 | **TLS 핸드셰이크 타임아웃 부재 (DoS)** [T-1] | `Security.Tls` + `Hosting/SecuredConnectionHandler.cs` | slowloris형 점유 공격 무방비. `HandshakeTimeout` 옵션(공개 API) 추가 |
| B-2 | **서킷 브레이커 취소=성공 오염** [H-1] | `Hosting/CircuitBreaker.cs` + `Core/Resilience/ICircuitBreaker.cs` | OCE가 성공으로 집계 — 회로가 안 열리거나 헛닫힘. **Core 인터페이스에 중립 반납 추가 필요(동결 전 유일 기회)** |
| B-3 | **Consul WaitTime(5분) > HttpClient(100초)** [S-2] | `Cluster.Consul` | 기본값 조합만으로 101초마다 영구 경고 반복 |
| B-4 | **헬스 엔드포인트 accept 루프 조기 종료** [O-1] | `Diagnostics.Http/HealthHttpEndpoint.cs:149-176` | 일시 예외 1건에 liveness가 죽어 정상 프로세스가 재시작당함 |
| B-5 | **DataTable 스냅샷 rowCount 무검증 선할당** [R-5] | `DataTable/StaticTableSnapshot.cs:258-370` | 악의적 스냅샷으로 OOM. 와이어에 나가기 전이 가장 싸다 |
| B-6 | **TickLoop 이중 Dispose 조기 반환** [R-2] | `RealTime/TickLoop.cs:146-160` | 1줄 수정 |
| B-7 | Try-패턴 nullability 어노테이션 3곳 [C-2] | Core | 컴파일 계약 — 동결 후 변경 시 소비자 경고 뒤바뀜 |
| B-8 | ClusterView 내부 배열 노출 방어 [C-3] | `Core/Cluster/ClusterView.cs:151` | 불변 스냅샷 계약을 타입으로 강제 |
| B-9 | 세션 재개 응답 상태 바이트 무검증 [C-5] | `Core/Sessions/SessionHandshakeCodec.cs:168` | 와이어 수용 동작은 동결 대상 |
| B-10 | 클라이언트 협상 옵션 미복사 [H-2] | `Hosting/ClientBuilder.cs` | 문서화된 계약 위반 정합화 |

### C. 설계 결정 필요 (사용자 판단 — B와 병행 가능, 전부 동결 전 마지막 기회)

| # | 결정 | 선택지 | 참조 |
|---|---|---|---|
| C-① | **`NodeId.None`(=0) vs 유효 노드 0** | (a) 노드 0 예약+`IsNone` 추가 (b) 0 유효 확정+`None` 제거 | [C-6](01-core.md) |
| C-② | **프레이밍 계약에 capabilities 표면 추가 여부** | `IFrameEncoder/Decoder`에 `FrameCodecCapabilities` 추가 → 압축+varint 등 죽은 조립을 시작 시점에 거부 가능. 미추가 시 영구 런타임 발견 | [H-8](02-hosting.md) |
| C-③ | **`Room.Disband` 반환형** | int(현행) → 멤버 스냅샷 반환(해산 창 멤버 증발 수습 가능). API 모양 변경 | [R-6](07-realtime-match-datatable.md) |
| C-④ | **Shipped 이동 정책 확정** | "1.0에 1회" vs "릴리스마다" — VERSIONING.md 자기모순 해소. 1.0 절차의 전제 | [O-5](08-observability-build.md) |
| C-⑤ | **미방출 메트릭 `backpressure.duration`** | 구현 vs 제거 — 계약 동결 전 양자택일 | [O-2](08-observability-build.md) |

### D. 1.0 전 권장 (P1~P2 중 동결·기본값과 얽힌 것)

- ID 값 타입에 `ISpanFormattable`/`IUtf8SpanFormattable` 일괄 구현 [C-4] — additive지만 동결 전 일괄이 정석
- 전송 축 비대칭 해소: HTTP/WS/QUIC에 AdmissionControl+거부 메트릭 [T-5], QUIC 인증서 회전 [T-4], WebSocket Origin 옵션 [T-6]
- `SslStreamCertificateContext` 도입 [T-3] — `IServerCertificateSource` 반환형이 바뀐다면 동결 전
- `StopAsync` 무토큰 시 무한 드레인 [T-2] — 동작 계약 확정은 동결 전
- TickLoop 기본 스핀 창(효과 없는 조합) 재조정 [R-9] — 기본값 변경은 동결 전
- 매치메이커 앵커 재개 개선(공짜)+패스 상한 옵션 [R-4], 타이머 취소 노드 회수 또는 한계 문서화 [R-3]
- `ServerBuilder.Build()` 비멱등 차단 [H-3], `DisposeAsync` try/finally [H-6]
- 히스토그램 단위/버킷 메타데이터 [O-3], FramesRejected 이중 집계 [R-7], error_code 태그 의미 분리 [O-9]
- 운영 하드닝: GC runtimeconfig 자동 게이트 [O-4], 결정적 빌드 검증 연결 [O-6], Bench csproj의 ADR-0031 오타 잔재 제거 [X-2], BENCHMARKS 기준선 ENV 표기 정정 [X-4]

### E. 1.0 이후 백로그 (요약 — 상세는 각 문서)

- **성능(측정 선행)**: PooledBufferWriter 보유 상한 [D-1], InterestGrid 셀 레이아웃/SoA-SIMD [R-8],
  Redis EVALSHA [S-3], PG `Max Auto Prepare` 문서화 [S-4], `FrozenDictionary` 후보(ClusterView
  [C-7], DataTable [R-11]), varint fast path [D-5], 개방 루프(고정 도착률) 부하 모드 [X-5]
- **구조**: 전송 수락 골격 Template Method 통합 [T-7], WS/QUIC 0바이트 read [T-13],
  ConnectionId 세대 활용 [T-9], QUIC 클라이언트 종단별 게이트 [T-8]
- **도구**: 분석기 CHSM3004/3005(무제한 채널·TryWrite+Wait) [O-7], 적합성 테스트 6종 보강 [S-5],
  LZ4 퍼즈 테스트 [D-2], xunit v3 등 업그레이드 후보(ADR 필요) [O-12]

## 권장 진행 순서 (기존 로드맵과의 결합)

현재 로드맵의 다음 단계는 "정식 24h soak → 1.0 경로"였다. 이 감사 결과를 반영하면:

1. **C-①~⑤ 설계 결정** (사용자와 함께 — 반나절)
2. **A(P0 4건) + B(P1 10건) 수정** — 전부 합쳐 1~2일 규모. 각 수정에 회귀 테스트 동반,
   public 표면 변경은 Unshipped 갱신
3. **D 중 채택분 반영** (특히 기본값·계약 확정류)
4. **그 다음에 정식 24h soak** — soak는 최종 빌드를 검증해야 의미가 있으므로 수정 후로 순서
   조정. 상세 로거(`--logger "console;verbosity=detailed"`) 필수
5. Shipped 전량 이동 → VersionPrefix 1.0 → 게이트 재확인 → v1.0 태그 (기존 순서 유지)

## 감사에서 확인된 승계 자산 (변경 금지)

각 문서의 "잘 된 부분" 절 참조. 특히: Core 결과-값 규약의 균질성, TCP 소켓 경로(zero-byte read +
Dispose 취소), PartitionDispatchGate 프레임당 무할당, TimerWheel 세대|상태 단일 워드 CAS,
랑데뷰 해싱 구현, CONSISTENCY.md 주장의 전수 코드 일치, 벤치 방법론의 정직성(기각 기록·SMT
발견·측정자 병목 방어), 선택 축의 csproj 수준 격리 증명.
