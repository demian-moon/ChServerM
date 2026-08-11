# 아키텍처

`CLAUDE.md`가 규칙(무엇을 지켜야 하는가)이고, 이 문서는 구조(무엇이 어디에 있는가)다.
Phase 1에서 Core 추상화를 확정하면서 채워 나간다.

## 의존 방향

한 방향으로만 흐른다. 역방향 참조는 빌드 실패로 막는다.

```
Samples / Tests / Bench
        │
        ▼
  ChServerM.Hosting          ← 조립(Builder + DI + 옵션 검증)
        │
        ▼
  ChServerM.<축>.<벤더>       ← 어댑터 (Transport.Tcp, Serialization.MemoryPack, ...)
        │
        ▼
  ChServerM.Core             ← 추상화 전용. 참조 없음
```

- 어댑터끼리 서로 참조하지 않는다. 공통 코드가 필요하면 Core나 Buffers로 내린다.
- `ChServerM.Buffers`는 Core만 참조하며 모든 어댑터가 참조할 수 있다.
- `ChServerM.SourceGen`은 분석기(analyzer)로 참조되며 런타임 의존이 아니다.

## 계층 책임

| 어셈블리 | 책임 | 하지 않는 것 |
|---|---|---|
| `Core` | 계약(인터페이스), 값 타입, 옵션 POCO, 에러 코드 | 구현, 서드파티 참조, 정적 상태 |
| `Buffers` | 풀링, 슬랩 할당, `IBufferWriter` 유틸 | 프로토콜 해석 |
| `Hosting` | 빌더, DI 등록, 파이프라인 컴파일, 생명주기 | 프로토콜·직렬화 구현 |
| `Transport.*` | 바이트 in/out, 커넥션 생명주기, 백프레셔 | 메시지 의미 해석 |
| `Serialization.*` | 바이트 ↔ 객체 | 프레이밍 |
| `Concurrency` | 스케줄링, 실행 모델 | I/O |
| `SourceGen` | 컴파일 타임 코드 생성 | 런타임 로직 |

**프레이밍과 직렬화는 분리한다.** 전송은 "완전한 한 프레임"까지만 책임지고, 직렬화는 프레임 바이트만 본다. 이 경계가 두 축을 독립적으로 교체 가능하게 만든다.

## 요청 처리 흐름 (설계 목표)

```
소켓 수신
  └─ Pipelines 버퍼 (Buffers: 풀에서 대여)
      └─ IFrameDecoder      → 완전한 프레임 경계 확정
          └─ 미들웨어 파이프라인 (Chain of Responsibility)
              ├─ 데코레이터: 메트릭 / 트레이싱 / 압축 해제 / 복호화
              └─ IMessageDispatcher  → 소스 생성 스위치 테이블
                  └─ IMessageHandler<T>   ← 사용자 코드
                      └─ 응답: IBufferWriter로 직접 씀 (중간 배열 없음)
```

목표: 이 경로 전체에서 메시지당 힙 할당 0. 버퍼는 풀에서 대여하고 프레임 완료 시 반납한다.

## 확장 지점

새 기능은 다음 중 하나로 들어간다. 여기에 해당하지 않으면 축 설계부터 다시 본다.

1. **새 축 구현체** — 기존 Core 인터페이스를 구현하는 어댑터 추가. Core 변경 없음.
2. **새 미들웨어** — `IServerMiddleware` 구현. 파이프라인에 삽입.
3. **새 데코레이터** — 기존 축 구현체를 감싸 횡단 관심사 추가.
4. **새 축** — Core에 인터페이스 추가. **ADR 필수.** 추상화 자체가 비용이므로 두 번째 구현체가 실제로 필요할 때만 만든다.

## 스케일 전략

- **스케일업**: 스레드-퍼-코어 + CPU 어피니티, 공유 상태 최소화, false sharing 회피. 검증 기준은 코어 수 대비 처리량 선형성.
- **스케일아웃(무상태)**: 세션을 `ISessionStore`로 외부화 → 노드를 자유롭게 추가/제거.
  `stateless-web` 프로필의 전송은 `ChServerM.Transport.Http`(Kestrel, **HTTP/2 스트림 하나 =
  커넥션 하나**, ADR-0057) — `WebApplication` 없이 `KestrelServer` 를 직접 세우며, 의존은
  공유 프레임워크(`FrameworkReference`)라 NuGet 패키지 0. `ChServerM.Transport.WebSocket`
  (ADR-0059)도 같은 호스팅 방식이며 **메시지 경계를 버리고 바이트 스트림으로** 나른다 —
  경계는 언제나 프레이밍 축이 긋는다. 같은 핸들러가 **네 전송**(인메모리·TCP·HTTP·WS)에서
  동작함을 `CrossTransportTests` 와 `StatelessWebProfileTests`(2노드 세션 외부화)가 고정한다.
- **스케일아웃(상태 유지)**: `IClusterMembership` + 파티셔닝 키 라우팅. Phase 15.

### 클러스터 축의 어셈블리 배치

| 어셈블리 | 무엇인가 | 참조 |
|---|---|---|
| `ChServerM.Core` (`Cluster/`) | 축 추상화 — `IClusterMembership` · `ClusterView` · `IClusterRouter` · `ClusterRoute` · `ClusterQuorum` | 없음 |
| `ChServerM.Cluster` | 축 위의 **결정 로직** — 랑데뷰 라우터 · `ClusterRouteResolver`(뷰↔라우터 짝, 소유권 감시, 리더 판정) · 정적 목록 참조 구현 | Core |
| `ChServerM.Cluster.Consul` | 멤버십 축의 **Consul 어댑터**. 벤더 격리 지점이며 **서드파티 의존 0**(`HttpClient` + 소스 생성 JSON, ADR-0055) | Core |
| `ChServerM.Cluster.Hosting` | 피어 링크 배선(`ClusterPeerSet`). **별도 어셈블리인 이유**: 어댑터에 넣으면 의존 방향이 뒤집히고, `Hosting` 에 넣으면 조립 계층이 클러스터를 알게 된다(ADR-0050) | Core · Hosting |

**클러스터를 통째로 빼도 프레임워크가 성립한다** — 위 넷을 지워도 `Hosting` 은 컴파일된다.
어댑터가 Core 만 참조하는 것이 그 성질의 근거다.

### 실시간 프리미티브 축(Part V, 선택)의 어셈블리 배치

| 어셈블리 | 무엇인가 | 참조 |
|---|---|---|
| `ChServerM.RealTime` | 실시간 프리미티브 — 고정 타임스텝 틱 루프(`TickLoop`, 절대 스케줄 + 유계 캐치업) · 계층적 타이밍 휠(`TimerWheel`, 레거시 `TimeEventSchedulerM` 설계 승계) · 시간 동기화(`MicrosecondClock`/`RemoteClock`/`TimeSyncExchange`) · RTT 추정(`RttEstimator`, IQR) · `IntervalGate` (ADR-0061~0063) | Core |
| `ChServerM.RealTime.Rooms` | 룸/채널 — 멤버십 생명주기(`Room`/`RoomDirectory`, COW 배열) · **직렬화 1회 브로드캐스트**(`RoomBroadcaster` + 참조 계수 `BroadcastFrame`) · 커넥션 파티션 배타 슬롯에서 쓰는 기본 싱크(`PartitionedMemberSink` — Output 단일 라이터 규약을 지키는 유일한 합법 경로) · 더티 추적(`DirtySet<T>`) (ADR-0064) | Core |
| `ChServerM.RealTime.Spatial` | 공간 — 모튼 코드(레거시 유일 생존 자산 승계) · AOI 균일 그리드(`InterestGrid`) · Enter/Leave 집합 차분(`InterestSet`) · 무할당 SAT 충돌(`Aabb`/`Obb`/`CollisionContact`) (ADR-0065). 좌표는 BCL `Vector2`, 각도는 라디안 하나 | Core |
| (없음) | Core 에 이 축의 계약은 **없다** — 틱 루프는 계약이 아니라 구현체다. 두 번째 구현이 필요해질 때 인터페이스를 뽑는다(ADR-0061 결정 1). 세 어셈블리는 서로도 참조하지 않는다 — 룸만, 공간만, 틱만 따로 쓸 수 있다 | — |

**이 축을 통째로 빼도 프레임워크가 성립한다**(ADR-0004의 명시 조건). Core 를 참조하되
(`MonotonicTimestamp`·진단 계약 재사용 — 시간 표현이 두 벌이 되는 것을 막는다) Core 는
이 어셈블리들을 모르고, 메트릭 이름도 `RealTimeMetricNames`/`RoomMetricNames` 로 자기
어셈블리에 있다. 휠은 스레드를 갖지 않는 수동 자료구조이고 틱 루프가 드라이버가 된다 —
결합 방식은 `TickLoopTimerWheelCompositionTests` 가 고정한다.

## 확정된 것

- **ADR-0004** — 프레임워크에 목표 워크로드는 없다. 축 조합이 워크로드를 만든다.
  검증용 참조 프로필 2개(`realtime-stateful`, `stateless-web`)를 상시 유지하고,
  **두 프로필이 같은 핸들러 코드로 동작하는 것**이 조립 가능성의 합격 기준이다.
  실시간 프리미티브(틱·룸·AOI)는 선택 패키지이며 Core는 그 존재를 알지 않는다.
- **ADR-0002** — 프레임 헤더는 직렬화 라이브러리를 거치지 않는다. 고정 크기 `struct` +
  **`BinaryPrimitives` 만**으로 직접 처리하고(2026-08-03 확정에서 `MemoryMarshal` 은
  배제 — 정렬·패딩·호스트 엔디안에 와이어 포맷이 끌려간다), 직렬화는 페이로드에만 적용한다.
  위 "프레임 처리 흐름"의 `IFrameDecoder` ↔ `IMessageSerializer` 경계가 이 결정을 구현한다.

## 미확정

- **ADR-0001** — raw TCP 구현 방식 (Kestrel Socket Transport 재사용 vs 순수 `Socket`)
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값 (Phase 6 벤치마크)
