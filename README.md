# ChServerM

**모듈형 초고성능 서버/클라이언트 프레임워크** — C# / .NET 10, Native AOT.

단일 용도 서버가 아니라, **기능 축을 골라 조립해서 서버를 구성하는 프레임워크**다.
직렬화·전송·프레이밍·동시성 모델·세션 저장소를 전부 빌더에서 바꿔 끼운다:

```csharp
await using ChServerMServer server = new ServerBuilder()
    .UseTransport(new TcpServerTransport(endPoint, tcpOptions))   // ↔ InMemory · HTTP/2 · WebSocket · QUIC
    .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
    .UseExecutionModel(new PartitionedExecutionModel())           // 키 기반 샤딩 — 락 없이 순서 보장
    .ConfigureDispatcher(d => d.MapGeneratedHandlers(             // 소스 제너레이터 — 리플렉션 0
        MemoryPackMessageSerializerProvider.Instance, ...))
    .Build();
```

> **상태: 0.x 프리릴리스** — [nuget.org 에 발행됨](https://www.nuget.org/packages/ChServerM).
> 공개 API 는 아직 동결 전이다(SemVer 0.x — [VERSIONING](docs/VERSIONING.md)).

## 특성 — 전부 실측으로 방어한다

측정 없는 성능 주장은 하지 않는다. 수치의 근거와 환경은 전부
[BENCHMARKS](docs/BENCHMARKS.md)에 있다.

- **처리량 169k RPS · p50 104µs** (raw TCP 에코 기준선, 1만 동시 접속 안정)
- **코어 확장 14.67× / 16코어** (91.7% — share-nothing 파티셔닝, 핫패스 락 0)
- **프레임당 힙 할당 0 B** — CI 게이트가 상시 강제한다
- **Native AOT** — 전 라이브러리 AOT 호환, 기동 62ms · 워킹셋 11MB, 51MB 컨테이너
- **관측 내장** — 메트릭·트레이싱·헬스체크, 켠 오버헤드 ~72ns/프레임 실측

## 교체 가능한 축

| 축 | 구현 |
|---|---|
| 전송 | TCP · 인메모리 루프백 · HTTP/2(h2c) · WebSocket · QUIC |
| 직렬화 | MemoryPack(기본) · Protobuf · FlatBuffers |
| 프레이밍 | 고정 헤더(기본) · varint |
| 동시성 | 키 기반 파티션(순서 보장) · 스레드풀 병렬 |
| 세션 저장소 | 인메모리 · Redis · PostgreSQL |
| 보안 | TLS 1.3 · 버전 협상 · 인증/인가 미들웨어 |
| 클러스터 | 정적 목록 · Consul (멤버십·라우팅) |
| 선택 축 | 룸/브로드캐스트 · 공간 분할(AOI) · 틱 루프/타이밍 휠 · 매치메이킹 · 압축(LZ4) |

같은 핸들러 코드가 TCP 상태 유지 프로필과 HTTP 무상태 프로필 양쪽에서 그대로
동작하는 것이 이 프레임워크의 합격 기준이다 — `Samples/`가 그 증명이다.

## 시작하기

- **[시작 가이드](docs/GETTING-STARTED.md)** — 5분 안에 에코 서버
- [축 선택 가이드](docs/GUIDE-CHOOSING-AXES.md) · [성능 튜닝](docs/GUIDE-PERFORMANCE.md) · [레거시 마이그레이션](docs/GUIDE-MIGRATION.md)
- [아키텍처](docs/ARCHITECTURE.md) · [설계 결정(ADR)](docs/DECISIONS.md) · [배포 예제(Docker/K8s)](deploy/README.md)

```bash
dotnet add package ChServerM
```

메타 패키지 하나가 realtime-stateful 최소 조합(TCP·고정 헤더 프레이밍·파티션
실행·MemoryPack·분석기)을 가져온다. 다른 축은 `ChServerM.*` 개별 패키지로
추가·교체한다.

## 라이선스

[Apache License 2.0](LICENSE) · [서드파티 고지](THIRD-PARTY-NOTICES.md) ·
보안 신고는 [SECURITY.md](SECURITY.md)
