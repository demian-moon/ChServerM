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
