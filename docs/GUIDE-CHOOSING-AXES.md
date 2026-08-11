# 축 선택 가이드 — 무엇을 언제 꽂는가

이 프레임워크에는 목표 워크로드가 없다 — **축 구현체의 조합이 워크로드를 만든다**(ADR-0004).
이 문서는 그 조합을 고르는 결정 문서다. 구조는 [ARCHITECTURE.md](ARCHITECTURE.md),
첫 실행은 [GETTING-STARTED.md](GETTING-STARTED.md), 각 결정의 원문은
[DECISIONS.md](DECISIONS.md)에 있다.

## 0. 첫 질문 — 커넥션에 상태가 있는가

축 하나하나를 고르기 전에 이것부터 정한다. 나머지 선택의 절반이 여기서 따라온다.

| | `realtime-stateful` | `stateless-web` |
|---|---|---|
| 커넥션 | 상시 연결, 서버가 먼저 보낼 수 있다(push) | 요청-응답, 노드 교체 자유 |
| 실행 모델 | `PartitionedExecutionModel` — 같은 커넥션 순차 | **없음** — 스레드풀 병렬 |
| 순서 보장 | 커넥션 단위로 있음 | 없음 (응답을 시퀀스로 짝짓는다) |
| 세션 | 인메모리 (커넥션 = 세션) | 외부화 (`Redis`/`Postgres`) — 수평 확장 |
| 참조 샘플 | `Samples/ChServerM.Samples.EchoServer` | `Samples/ChServerM.Samples.StatelessWeb` |

두 프로필이 **같은 핸들러 코드**로 동작한다 — 그것이 조립 가능성의 합격 기준이고,
중간 형태(일부 메시지만 순서 보장)도 같은 부품으로 조립할 수 있다.

## 1. 전송 — 누가 접속해 오는가

구현체 5종이 있고, 같은 핸들러가 다섯 곳에서 그대로 돈다.

| 구현체 | 고르는 경우 | 근거 |
|---|---|---|
| `Transport.Tcp` | 자체 클라이언트를 가진 상시 연결. **기본 선택** — 지연 바닥이 가장 낮다 | ADR-0001 (순수 Socket+Pipelines) |
| `Transport.Http` (HTTP/2) | 웹 인프라(LB·프록시·인그레스)를 거쳐야 할 때. 스트림 하나 = 커넥션 하나 | ADR-0057. 전송 세금 A/B: 지연 바닥은 TCP 가 ~6% 낫지만 **고동시성에선 다중화가 5.9배 역전** |
| `Transport.WebSocket` | 브라우저 클라이언트. 메시지 경계는 버리고 바이트 스트림으로 — 경계는 언제나 프레이밍 축이 긋는다 | ADR-0059 |
| `Transport.Quic` | 헤드오브라인 블로킹 회피, 0-RTT 재접속. TLS 필수(프로토콜 자체 요구). 리눅스는 msquic 필요 | ADR-0060 |
| `Transport.InMemory` | 테스트·프로세스 내 조립 검증. 소켓 없이 전체 파이프라인이 돈다 | Phase 2 |
| (UDP 신뢰 전송) | **없다.** UDP 축의 질문은 QUIC 이 흡수했다 — 실수요가 생기면 재개 | ADR-0060 |

**전송이 무엇이든 프레이밍·직렬화·핸들러는 그대로다.** 전송을 바꾸는 비용은
`UseTransport(...)` 한 줄 + 그 전송의 옵션(버퍼 임계값)이다.

## 2. 프레이밍 — 프레임 경계를 어떻게 긋는가

현재 구현체는 고정 헤더(`FixedHeaderFrameDecoder`/`Encoder`) 하나다. 헤더는 직렬화
라이브러리를 거치지 않는 고정 `struct` + `BinaryPrimitives` 다(ADR-0002) — 이 경계 덕에
프레이밍과 직렬화를 서로 모르게 교체할 수 있다.

**정할 것은 하나, `MaxPayloadLength`.** 기본값(1MB)에 기대지 말고 워크로드의 실제 최대
메시지로 명시한다. 전송 버퍼 임계값과 어긋나면 `Build()` 가 조립을 거부한다(ADR-0007) —
그 조합은 런타임이면 소리 없는 교착이었다.

## 3. 직렬화 — 페이로드를 무엇으로 읽고 쓰는가

| 구현체 | 고르는 경우 | 근거 |
|---|---|---|
| `Serialization.MemoryPack` | C# ↔ C#. **기본값** — zero-encoding, 소스 생성 | ADR-0013 |
| `Serialization.Protobuf` | 크로스 언어 상호운용, 스키마 진화(모르는 필드 보존) | ADR-0012 |
| `Serialization.FlatBuffers` (FlatSharp) | 역직렬화 없는 랜덤 접근 — 큰 메시지에서 일부 필드만 읽을 때 | ADR-0012 |

수치 비교는 [BENCHMARKS.md](BENCHMARKS.md)의 Phase 6 절이 정본이다.
핸들러 등록은 `[MessageHandler]` + `MapGeneratedHandlers(직렬화 제공자, ...)` — 직렬화기를
갈아 끼워도 핸들러 선언은 그대로다 (`EchoServer` 는 MemoryPack, `StatelessWeb` 은 Protobuf
로 같은 경로를 쓴다).

## 4. 실행 모델 — 순서 보장이 필요한가

- **필요하다** → `UseExecutionModel(new PartitionedExecutionModel(...))`. 커넥션 ID 의 안정
  해시로 파티션을 고르므로 같은 커넥션은 항상 같은 파티션 = 순차, 다른 커넥션은 병렬.
  파티션 안에서는 락도 `Concurrent*` 도 필요 없다 — 그 보장이 계약이다(CLAUDE.md 9.1).
- **필요 없다** → `UseExecutionModel` 을 부르지 않는다. 스레드풀 병렬이 되고 응답 순서가
  섞인다 — 클라이언트가 시퀀스 번호로 짝지어야 한다.
- 파티션 수는 옵션이다. 기본값에서 시작해 [GUIDE-PERFORMANCE.md](GUIDE-PERFORMANCE.md)의
  절차로 조정한다.

**주의: 룸 축(`RealTime.Rooms`)은 파티션 실행 모델을 전제한다** — 커넥션 파티션의 배타
슬롯이 브로드캐스트 쓰기의 소유권 근거다(ADR-0064). 무상태 프로필과 룸은 조합하지 않는다.

## 5. 세션 저장소 — 상태를 어디에 두는가

| 구현체 | 고르는 경우 |
|---|---|
| `Persistence.InMemory` | 단일 노드, 커넥션 = 세션 수명. 상태 유지 프로필의 기본 |
| `Persistence.Redis` | 다중 노드 무상태 프로필. Redis Cluster 지원(쓰기마다 난수 버전, ADR-0058) |
| `Persistence.Postgres` | 세션이 곧 영속 데이터일 때(재시작 생존), 이미 PostgreSQL 을 운영할 때 |

## 6. 횡단 축 — 필요할 때만 꽂는다

| 축 | 구현체 | 언제 |
|---|---|---|
| 압축 | `Compression.LZ4` | 페이로드가 크고 반복적일 때. 해제 상한이 필수 인자다(ADR-0019 — 압축 폭탄 방어) |
| 전송 보안 | `Security.Tls` | 공인망. 커넥션 파이프 데코레이터라 어느 전송에나 끼운다(ADR-0017). QUIC 은 자체 TLS |
| 인증·인가 | `Security.AspNetIdentity` + Hosting 미들웨어 | 미들웨어는 라우팅보다 앞이다 — 반대면 모르는 ID 로 인증을 우회한다 |
| 관측 | `Observability` (OTel/Prometheus) · `Diagnostics.Http` (헬스 프로브) | 프로덕션이면 항상. 큐 깊이·드롭 수를 보지 않으면 조용한 유실은 존재하지 않는 것과 같다(9.6) |
| 데이터 테이블 | `DataTable` | 밸런스 표(CSV)를 강타입으로. 스키마·접근자를 한 선언에서 생성(ADR-0043, CHSM2xxx) |

## 7. 선택 축 — 실시간 프리미티브 (전부 빼도 프레임워크는 성립한다)

| 어셈블리 | 무엇 | 언제 |
|---|---|---|
| `RealTime` | 틱 루프 · 타이밍 휠 · 시간 동기화 | 고정 주기 시뮬레이션, 대량 타이머 |
| `RealTime.Rooms` | 룸 멤버십 + 1회 인코딩 브로드캐스트 | 같은 페이로드를 N 명에게. 멤버당 ~400ns·0 B. 조립법은 `Samples/ChServerM.Samples.GameRoom` |
| `RealTime.Spatial` | 모튼 그리드 AOI · 집합 차분 · 무할당 SAT | 공간 관심 영역, 근접 판정 |

셋은 서로를 참조하지 않는다 — 룸만, 공간만, 틱만 따로 쓸 수 있다.

## 8. 클러스터 — 다중 노드가 정말 필요한가

무상태 프로필이면 클러스터 축 없이 노드를 늘리면 된다(세션 외부화가 전부다).
상태 유지 다중 노드가 필요할 때만: 멤버십(`Cluster` 정적 목록 / `Cluster.Consul`) +
랑데뷰 라우팅 + 피어 링크(`Cluster.Hosting`). **주의**: 리더는 상호 배제가 아니고
(ADR-0054), 노드 번호는 임차이며 겹침은 기동 실패로 드러난다(ADR-0056). 실제 네트워크
분단 아래서의 검증은 아직 없다 — STANDUP 의 열린 블로커다.

## 9. 규칙

- **조합의 정합성은 `Build()` 가 검증한다.** 시작 시점 실패가 런타임 교착보다 낫다(ADR-0007).
- **새 축을 만들려면 ADR 부터.** 두 번째 구현체가 실제로 필요할 때만 인터페이스를 뽑는다 —
  하나뿐인 추상화는 가설이다(ADR-0000).
- **흔한 실수는 분석기가 잡는다.** `ChServerM.Analyzers`(CHSM3xxx)를 분석기 참조로 추가한다
  — 템플릿과 샘플에는 이미 들어 있다. 진단 목록: [DIAGNOSTICS.md](DIAGNOSTICS.md).
