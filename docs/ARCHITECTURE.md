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
- **스케일아웃(상태 유지)**: `IClusterMembership` + 파티셔닝 키 라우팅. Phase 10.

## 확정된 것

- **ADR-0002** — 프레임 헤더는 직렬화 라이브러리를 거치지 않는다. 고정 크기 `struct` +
  `MemoryMarshal`/`BinaryPrimitives`로 직접 처리하고, 직렬화는 페이로드에만 적용한다.
  위 "프레임 처리 흐름"의 `IFrameDecoder` ↔ `IMessageSerializer` 경계가 이 결정을 구현한다.

## 미확정

- **ADR-0003** — 목표 워크로드 (실시간 게임 서버 + 매치메이킹으로 제안, 사용자 확인 대기).
  전송·동시성 모델의 우선순위를 결정한다
- **ADR-0001** — raw TCP 구현 방식 (Kestrel Socket Transport 재사용 vs 순수 `Socket`)
- **ADR-0002 남은 부분** — 페이로드 직렬화 기본값 (Phase 5 벤치마크)
