# ChServerM 로드맵

체크박스가 **진행률의 유일한 기준**이다. `/standup wrap`이 이 파일을 갱신한다.
완료 기준: 코드 + 테스트 통과 + (성능 관련이면) 벤치마크 수치가 있을 때만 `[x]`.

---

## Phase 0 — 기반 (Foundation)

솔루션 골격과 빌드 규약. 여기서 정한 컴파일 옵션이 이후 모든 성능 작업의 전제가 된다.

- [ ] `ChServerM.sln` 생성, `Server/` `Client/` `Tests/` `Bench/` `Samples/` 솔루션 폴더 구성
- [ ] `Directory.Build.props` — `net10.0`, C# 14, nullable, `AllowUnsafeBlocks`, `IsAotCompatible`, ServerGC, TieredPGO
- [ ] `Directory.Packages.props` — 중앙 패키지 버전 관리 활성화
- [ ] `.editorconfig` — 코드 스타일 + 분석기 심각도 (성능 규칙 CA18xx는 warning-as-error)
- [ ] `ChServerM.Core` 프로젝트 생성 — 서드파티 의존 0 검증 테스트 포함
- [ ] CI 스크립트 (build + test + AOT 컴파일 검증)

## Phase 1 — Core 추상화

**가장 중요한 단계.** 여기서 그은 경계가 프레임워크의 확장성을 결정한다. 구현은 넣지 않는다.

- [ ] `IMessageSerializer` / `IMessageSerializer<T>` — `Span<byte>` 기반, 할당 없는 시그니처
- [ ] `IFrameDecoder` / `IFrameEncoder` — `ReadOnlySequence<byte>` 입력
- [ ] `IServerTransport` / `IClientTransport` / `IConnection` — 전송 중립 커넥션 추상화
- [ ] `IMessageDispatcher` / `IMessageHandler<T>` — 디스패치 계약
- [ ] `IServerMiddleware` + 파이프라인 델리게이트 타입 — Chain of Responsibility 계약
- [ ] `ISessionStore` / `ISession` — 상태 저장 추상화
- [ ] `IServerLogger` / `IMetricsSink` — 관측 추상화
- [ ] `IPayloadCodec` / `ITransportSecurity`
- [ ] 각 축의 `XxxOptions` 타입 + 검증 계약
- [ ] `docs/ARCHITECTURE.md`에 의존 방향·확장 지점 확정 기록

## Phase 2 — 호스팅 & 조립 (Builder)

축을 실제로 "골라 끼우는" 표면. Phase 1 추상화가 실제로 조립 가능한지 검증하는 단계다.

- [ ] `ServerBuilder` 플루언트 API — `.UseTransport()` `.UseSerializer()` `.Use<TMiddleware>()`
- [ ] DI 컨테이너 통합 (`Microsoft.Extensions.DependencyInjection`)
- [ ] 미들웨어 파이프라인 컴파일 (델리게이트 체인, 런타임 리플렉션 없이)
- [ ] 서버 생명주기 — 시작/graceful shutdown/드레인
- [ ] 옵션 검증 — 잘못된 축 조합을 **시작 시점에** 실패시킨다
- [ ] `ClientBuilder` 대칭 구성
- [ ] 조립 테스트: 축을 교체해도 컴파일·동작이 유지되는지 검증

## Phase 3 — 메모리 & 버퍼

- [ ] `ChServerM.Buffers` — 슬랩 할당기, 커넥션당 버퍼 대여
- [ ] `ArrayPool` / `MemoryPool` 래핑 정책 결정 (ADR)
- [ ] `IBufferWriter<byte>` 기반 쓰기 경로
- [ ] 풀 누수 감지 진단 (DEBUG 빌드)
- [ ] 벤치마크: 대여/반납 처리량, GC Gen0 수집 횟수

## Phase 4 — TCP 전송 (첫 실동 구현)

- [ ] Kestrel Socket Transport 재사용 vs 순수 `Socket`+Pipelines 비교 → ADR
- [ ] `ChServerM.Transport.Tcp` — accept 루프, 수신/송신 Pipelines
- [ ] length-prefix 프레이밍 (varint + fixed32)
- [ ] 백프레셔 — `PipeOptions` 임계값, 느린 소비자 처리
- [ ] 커넥션 생명주기 — idle timeout, half-open 감지, graceful close
- [ ] 통합 테스트: 연결/에코/대량 동시 접속
- [ ] 벤치마크: 에코 RPS, p50/p99/p999 레이턴시, 커넥션당 메모리

## Phase 5 — 직렬화 어댑터

축 교체가 실제로 동작함을 증명하는 단계. **최소 2개 구현이 필수.**

- [ ] `ChServerM.Serialization.MemoryPack`
- [ ] `ChServerM.Serialization.Protobuf`
- [ ] `ChServerM.Serialization.FlatBuffers` (FlatSharp)
- [ ] 벤치마크 3자 비교 → `docs/BENCHMARKS.md` + 기본값 선정 ADR
- [ ] 동일 샘플이 어댑터 교체만으로 동작하는지 검증

## Phase 6 — 디스패치 & 소스 제너레이터

- [ ] `ChServerM.SourceGen` — 메시지 ID → 핸들러 스위치 테이블 생성
- [ ] 핸들러 등록 컴파일 타임 검증 (중복 ID, 누락 핸들러)
- [ ] 리플렉션 기반 폴백 디스패처 (개발 편의용, 프로덕션 비권장)
- [ ] 벤치마크: 디스패치 오버헤드 (생성 코드 vs 리플렉션)

## Phase 7 — 동시성 실행 모델

- [ ] `ChServerM.Concurrency` — 채널 워커 풀 모델
- [ ] 스레드-퍼-코어 모델 + CPU 어피니티
- [ ] 액터 모델 어댑터 검토 (Orleans / Proto.Actor)
- [ ] 벤치마크: 코어 수 대비 확장성 곡선 (선형성 확인)

## Phase 8 — HTTP / 무상태 전송

- [ ] `ChServerM.Transport.Http` — Kestrel 기반, 동일 파이프라인 재사용
- [ ] 무상태 모드 — 세션을 `ISessionStore`로 외부화
- [ ] WebSocket 전송
- [ ] QUIC / HTTP/3 검토

## Phase 9 — 관측 & 운영

- [ ] `ChServerM.Observability` — OpenTelemetry 트레이스·메트릭
- [ ] ZLogger 어댑터
- [ ] 헬스체크 / 라이브 진단 엔드포인트
- [ ] 메트릭 데코레이터가 핫패스에 미치는 오버헤드 측정

## Phase 10 — 상태 & 클러스터

- [ ] `ChServerM.Persistence.Redis` (StackExchange.Redis)
- [ ] 로컬 KV 검토 (Tsavorite / Garnet)
- [ ] `IClusterMembership` — 정적 목록 구현
- [ ] 파티셔닝 / 라우팅 전략
- [ ] 서비스 디스커버리 어댑터

## Phase 11 — 패키징 & 출시 품질

- [ ] NuGet 패키징 (축별 개별 패키지)
- [ ] Native AOT 샘플 검증
- [ ] public API 문서 생성 (XML doc → 문서 사이트)
- [ ] 축 조합별 샘플 정리 (`Samples/`)
- [ ] 종단 부하 테스트 시나리오 (NBomber)

---

## 백로그 (단계 미배정)

- UDP 신뢰 전송 (게임용)
- 압축 어댑터 (LZ4 / Zstd)
- TLS 전송 보안
- 컴파일 타임 DI (Pure.DI / Jab) — AOT 경로용
- 레이트 리미팅 / 서킷 브레이커 미들웨어
- 관리 대시보드
