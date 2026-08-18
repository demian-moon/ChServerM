# 감사 02 — Hosting 조립 계층 (`ChServerM.Hosting`)

> 전수 감사 2026-08-18. 대상: `Server/ChServerM.Hosting/` 40소스 전량 정독 + Core 계약
> (`IServerTransport`, `ICircuitBreaker`, `MessageId`) 대조. 우선순위: P0=정확성/1.0 필수 ·
> P1=중요 · P2=권장 · P3=선택. 인덱스: [00-summary.md](00-summary.md)

## 요약

`ChServerM.Hosting`은 전반적으로 매우 높은 품질이다 — 미들웨어가 라우팅보다 앞이라는 구조적
보장, 배열 인덱싱 라우팅, `PartitionDispatchGate`의 프레임당 할당 0 설계(IValueTaskSource +
IThreadPoolWorkItem 재사용), 종료 순서 강제(전송 먼저·실행 모델 나중), `finally` 복원 규율(9.2),
유계 자료구조(9.6), `FrozenDictionary`/`System.Threading.Lock`/`TimeProvider`/`InlineArray` 등
최신 BCL 채택이 일관되게 지켜져 있다. 다만 **서킷 브레이커의 취소 분류(P1)** 와 **클라이언트의
협상 옵션 미복사(P1)** 는 정확성 문제이고, `ServerBuilder.Build()` 비멱등·실패 경로 문자열 할당·
`DrainAsync`의 TimeProvider 미사용 등 P2가 몇 건 있다. `PublicAPI.Shipped.txt`가 아직 비어
있어(전량 Unshipped) API 표면 변경은 지금이 마지막이자 유일한 기회다.

## 발견 사항

### [P1] H-1. 서킷 브레이커 — 취소(OCE)가 "성공"으로 보고되어 회로 판정을 오염시킨다

- **위치**: `Server/ChServerM.Hosting/CircuitBreakingSessionStore.cs:244-257`,
  `Server/ChServerM.Hosting/CircuitBreaker.cs:123-142`, `Server/ChServerM.Core/Resilience/ICircuitBreaker.cs`
- **현재 구현**: `Report()`가 비-인프라 예외(`OperationCanceledException` 포함)를
  `RecordSuccess()`로 보고한다("시험 자리는 반납해야 한다"). `ICircuitBreaker`에는
  TryEnter/RecordSuccess/RecordFailure 세 가지뿐 — **중립 반납 API가 없다.**
- **문제**: (1) HalfOpen에서 취소된 시험 호출이 `_halfOpenSuccesses`를 증가시켜, **실제 성공 없이
  취소 2건만으로 회로가 닫힐 수 있다**(기본 임계 2). (2) Closed에서 취소가
  `_consecutiveFailures`를 0으로 리셋해, 호출자 측 타임아웃이 링크드 토큰 OCE로 나타나는
  배포(흔한 패턴)에서는 실패가 취소와 섞여 **회로가 영영 열리지 않을 수 있다.** 죽은 저장소의
  실패 양상이 OCE인 조립에서 브레이커가 장님이 된다.
- **대안**: `ICircuitBreaker`에 중립 반납(예: `ReleaseProbe()` 또는 `RecordOutcome(CircuitOutcome)`)
  추가, `CircuitBreakingSessionStore.Report`의 비-실패 분기가 그것을 쓰게 한다. Closed에서 중립은
  카운터를 건드리지 않고, HalfOpen에서는 슬롯만 반납한다.
- **1.0 전 필수**: **필수** — Core public 인터페이스 변경이라 Shipped 동결 후에는 파괴적 변경.
- **난이도**: 중간

### [P1] H-2. `ChServerMClient`가 `VersionNegotiationOptions`를 복사하지 않고 참조로 보관 — 문서화된 계약 위반

- **위치**: `Server/ChServerM.Hosting/ClientBuilder.cs:230, 247, 352-364`; 계약 문서:
  `VersionNegotiationOptions.cs:28` ("조립 시점 전용. `Build()` 가 값을 복사한다")
- **현재 구현**: 서버 측 `VersionNegotiatingConnectionHandler`는 생성자에서 값을 복사하지만,
  클라이언트는 `ChServerMClient`가 옵션 **인스턴스**를 보관하고 `NegotiateAsync`가 접속 시마다
  `SupportedVersions`/`HandshakeTimeout`을 다시 읽는다.
- **문제**: Build 후 옵션 변경이 커넥션마다 반영된다. `Validate()`는 Build 시 1회뿐이므로, 조립 후
  `HandshakeTimeout = TimeSpan.Zero` 같은 변이가 검증 없이 라이브에 들어간다. 같은 어셈블리
  안에서 서버와 클라이언트의 규약이 어긋난다.
- **대안**: `ClientBuilder.Build()`에서 `SupportedVersions`·`HandshakeTimeout`을 값으로 복사해
  `ChServerMClient`에 넘긴다(내부 생성자라 공개 API 변경 없음).
- **1.0 전 필수**: 권장(공개 API 변경 없음 — 동작 계약 정합화). / **난이도**: 낮음

### [P2] H-3. `ServerBuilder.Build()`가 비멱등 — 두 번 부르면 관측 미들웨어가 조용히 중복 배선된다

- **위치**: `Server/ChServerM.Hosting/ServerBuilder.cs:403-422`
- **현재 구현**: Build가 공유 `_dispatcher`에 `PrependMiddleware(new MetricsMiddleware…)`·
  `PrependMiddleware(new TracingMiddleware())`·`MapRaw(SessionResume…)`를 **추가**한다.
- **문제**: 세션 조립이면 두 번째 Build가 `MapRaw` 중복으로 예외(시끄럽게 실패 — 그나마 낫다).
  그러나 메트릭/추적만 켠 조립이면 두 번째 Build가 **조용히 미들웨어를 이중 배선**해 프레임 수·
  지연이 2배로 계수되고 span이 중첩된다.
- **대안**: `_built` 플래그로 두 번째 Build를 `InvalidOperationException`으로 거부하거나, 자동
  배선분을 로컬 복제 빌더에서 수행해 비변이로 만든다.
- **1.0 전 필수**: 권장(API 표면 변경 없음 — 동결 전이 편하다). / **난이도**: 낮음

### [P2] H-4. 거부·실패 경로의 `status.ToString()` — 가장 뜨거운 열화 경로에서 프레임당 문자열 할당

- **위치**: `Server/ChServerM.Hosting/Dispatch/MetricsMiddleware.cs:81`,
  `Dispatch/TracingMiddleware.cs:96-97`
- **현재 구현**: `DispatchStatus`가 `Handled`가 아니면 `status.ToString()`으로 태그를 만든다.
  `Enum.ToString()`은 매 호출 힙 할당이다.
- **문제**: 속도 제한·열화 거부는 정확히 **과부하 시 프레임마다** 발생하는 경로다 — 부하가 가장
  높을 때 GC 압력을 더한다. "핫패스 무할당" 하드 룰과 상충.
- **대안**: `DispatchStatus`는 유한 enum이므로 `static readonly string[]`(또는 switch 식의 상수
  문자열) 이름 캐시로 대체. Metrics·Tracing 둘 다 공유.
- **1.0 전 필수**: 아니오(내부 구현). / **난이도**: 낮음

### [P2] H-5. `DrainAsync`가 `TimeProvider`를 무시한다 — `UseTimeProvider`가 서버 생명주기에 전달되지 않음

- **위치**: `Server/ChServerM.Hosting/ChServerMServer.cs:222-249`, `ServerBuilder.cs:483-485`
- **현재 구현**: `DrainAsync`가 `Task.Delay(delay, ct)`(시스템 타이머)·`Stopwatch.GetTimestamp()`·
  `new CancellationTokenSource(timeout)`(시스템 시계)를 직접 쓴다. `ServerBuilder._timeProvider`는
  핸들러들에만 전달되고 `ChServerMServer` 생성자에는 넘어가지 않는다.
- **문제**: 프레임워크 전체가 `TimeProvider` 주입을 일관 적용하는데 정작 무중단 배포 절차만
  실시간에 묶인다 — 드레인 테스트가 기본값으로 실제 5초+30초를 기다려야 한다.
- **대안**: 내부 생성자에 `TimeProvider` 추가,
  `Task.Delay(delay, timeProvider, ct)`·`new CancellationTokenSource(timeout, timeProvider)`·
  `timeProvider.GetTimestamp()`로 교체. 공개 API 변경 없음.
- **1.0 전 필수**: 아니오(내부 생성자). / **난이도**: 낮음

### [P2] H-6. `ChServerMServer.DisposeAsync` — `StopAsync` 예외 시 전송이 정리되지 않는다

- **위치**: `Server/ChServerM.Hosting/ChServerMServer.cs:253-266`
- **현재 구현**: `await StopAsync(immediate.Token); await _transport.DisposeAsync();` — try/finally 없음.
- **문제**: `StopAsync` 안의 `_executionModel.DisposeAsync()`가 던지면 `_transport.DisposeAsync()`가
  건너뛰어져 수락 소켓·포트가 산 채로 남는다. `_disposed`는 이미 1이라 재시도도 불가능 — 이중
  Dispose 가드가 오히려 누수를 고착시킨다.
- **대안**: `try { await StopAsync(...); } finally { await _transport.DisposeAsync(); }`.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P2] H-7. `UseSessions` + 상태 필터 조합의 죽은 조립이 검증되지 않는다

- **위치**: `Server/ChServerM.Hosting/ServerBuilder.cs:403-408`,
  `Dispatch/MessageStateFilterMiddleware.cs`(기본 거부), `FrameworkMessageIds.SessionResume = 40007`
- **현재 구현**: `UseSessions`는 40007을 자동 매핑하지만, 앱이 `MessageStateFilterMiddleware`를
  조립하면서 초기 상태에 `Allow(40007, …)`를 빠뜨리면 재개 요청이 필터의 기본 거부에 걸려
  **커넥션이 즉시 닫힌다**(`RejectedByState` = 무조건 종료). 어느 문서도 이 상호작용을 언급하지
  않고, Build도 잡지 않는다.
- **문제**: "재개가 항상 거부되는" 런타임 미스터리 — 프레임워크가 자동 배선한 경로가
  프레임워크의 다른 미들웨어에 조용히 차단되는 조합. `MessageDispatcherBuilder`는
  `_middlewareInstances`로 필터 존재를 이미 알고 있으므로 조립 시점 검증 가능.
- **대안**: `_sessionResume`이 있고 필터가 등록됐을 때 규칙에 40007이 없으면 Build에서
  예외(또는 최소한 문서 상호 참조 경고). 규칙 조회를 위해 필터에 internal 접근자 하나 필요.
- **1.0 전 필수**: 아니오(검증 강화는 additive). / **난이도**: 중간

### [P2] H-8. CompositionGuard 커버리지 — 압축 축 + 플래그 없는 프레이밍이 조립 시점에 안 잡힌다 (**Core API 결정 필요**)

- **위치**: `Server/ChServerM.Hosting/CompositionGuard.cs`(검사 1개뿐),
  `ServerBuilder.cs:122-123`(문서로만 경고)
- **현재 구현**: `UsePayloadCodec` 문서가 "varint 프레이밍과는 조립할 수 없다"고 적지만 검사는
  없다 — 송신 시 인코더가 `Compressed` 플래그를 거부하는 **런타임 예외**로만 드러나고, 수신
  측에서는 플래그가 없어 코덱이 영영 발동하지 않는다(조용한 무동작).
  `VersionNegotiationOptions.cs:21-27`도 같은 부류의 미검증 조합(협상 + varint)을 "Core 계약에
  표면이 없어 불가능"이라고 명시한다.
- **문제**: 두 조합 모두 "조립 시점 실패가 런타임 디버깅보다 싸다"는 이 계층의 자기 원칙에
  어긋난다. 원인은 Core 프레이밍 계약에 capabilities 표면이 없는 것 — **Shipped 동결 후에는
  인터페이스 확장이 파괴적 변경이 된다.**
- **대안**: 1.0 전에 `IFrameEncoder`/`IFrameDecoder`에 capabilities 표면(예:
  `FrameCodecCapabilities` flags 프로퍼티)을 추가할지 지금 결정하고, 추가한다면
  CompositionGuard에 `EnsureCodecRequiresFlagCapableFraming` 검사를 더한다.
- **1.0 전 필수**: **결정 필수**(Core public API 확장 여부 — 지금이 마지막 기회). / **난이도**: 중간

### [P3] H-9. CircuitBreaker Open→HalfOpen 전이 레이스 — 카운터 리셋이 CAS 이후

- **위치**: `Server/ChServerM.Hosting/CircuitBreaker.cs:104-111`
- **문제**: CAS 직후·리셋 직전에 다른 스레드가 `TryTakeProbeSlot`으로 카운터를 올릴 수 있고,
  리셋이 그 증가를 지워 이후 반납 시 카운터가 음수가 된다 → 상한(기본 1)보다 많은 시험 호출이
  회복 중인 대상에 나갈 수 있다. 창이 극히 좁고 자기 교정되지만 9.3의 원자성 일관 원칙에 어긋난다.
- **대안**: 상태를 (state, epoch) 하나의 워드로 합치거나, Open() 시점(전이 전 유일 지점)에만
  카운터를 리셋. / **1.0 전 필수**: 아니오. / **난이도**: 중간

### [P3] H-10. 세션 조립 시 라우팅 배열이 40008 엔트리(~320KB)로 커진다

- **위치**: `Server/ChServerM.Hosting/Dispatch/MessageDispatcherBuilder.cs:346-381`
- **판단**: 배열 크기 = 최대 등록 ID + 1. `UseSessions`가 40007을 등록하는 순간 앱이 ID 10까지만
  써도 40008 × 8B 배열이 잡힌다(문서에 명시, 서버당 1개라 실해 작음). 앱 대역(1–40000)과
  프레임워크 대역(40001+)의 2단 테이블이 대안이나 측정 없는 최적화 금지 원칙상 현 상태 유지도
  정당 — 기록만. / **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] H-11. `StartAsync` 실패 시 `_started`가 1로 남는다 — 바인드 실패 후 재시도 불가

- **위치**: `Server/ChServerM.Hosting/ChServerMServer.cs:131-141`
- **문제**: 포트 점유 같은 일시 실패 후 재시도하려면 서버 전체를 다시 조립해야 한다. "1회용"
  문서는 성공한 시작에 대한 것이지 실패한 바인드까지 커버한다고 읽히지 않는다.
- **대안**: 실패 시 `Volatile.Write(ref _started, 0)` 롤백, 또는 XML 문서에 명시.
- **1.0 전 필수**: 아니오(문서만이라도). / **난이도**: 낮음

### [P3] H-12. `PerConnectionRateLimiter` — private `Bucket` 타입을 feature 키로 사용, 인스턴스 2개 조립 시 충돌

- **위치**: `Server/ChServerM.Hosting/Dispatch/PerConnectionRateLimiter.cs:61-67`
- **문제**: 키가 타입이므로, 서로 다른 옵션의 리미터 두 개를 파이프라인에 조립하면 같은 버킷을
  공유해 두 정책이 하나로 뭉개진다.
- **대안**: 인스턴스별 래퍼 feature로 전환, 또는 문서 한 줄("이 리미터는 파이프라인당 하나만").
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] H-13. BCL 최신화 소소한 기회들

- `PerAddressConnectionRateAdmissionControl.cs:213-224` — 수동 `RoundUpToPowerOfTwo` 루프 →
  `BitOperations.RoundUpToPowerOf2`.
- `InMemoryTokenReplayGuard.cs:103` — 거부 경로에서도 `token.ToArray()` 선할당. .NET 9+
  `ConcurrentDictionary.GetAlternateLookup<ReadOnlySpan<byte>>`로 리플레이-거부 경로의 할당 제거
  가능(인증은 커넥션당 1회라 이득은 작다).
- `ChServerMServer.cs:261-262` — CTS 생성+`CancelAsync` 대신 `new CancellationToken(canceled: true)`.
- `MemoryLoadLevelSource.cs:108` — `Interlocked.Read` 대신 `Volatile.Read`(의미 동일, 문서 일관).
- 이미 잘 쓰는 것: `FrozenDictionary`/`FrozenSet`, `System.Threading.Lock`, `TimeProvider`,
  `InlineArray`, `HashCode.AddBytes`, `CancellationTokenSource(TimeSpan, TimeProvider)`.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] H-14. `SessionResumeToken.GetHashCode`가 비밀의 앞 4바이트를 그대로 반환한다

- **위치**: `Server/ChServerM.Hosting/Sessions/SessionResumeToken.cs:153-158`
- **문제**: "전체를 섞지 않으려" 앞 4바이트를 리틀엔디언 int로 반환 — 주석의 논리가 뒤집혀 있다.
  해시 코드가 관찰 가능한 자리에 노출되면 **비밀 원문 4바이트가 그대로 샌다.** 전체를 섞은
  해시는 원문 복원이 안 되므로 오히려 안전하다.
- **대안**: `HashCode.AddBytes` 전체 해시로 교체 + 문서 수정.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] H-15. 문서·자기 일관성 잡음

- `ServerBuilder.cs:488-501` — `BuildHealthRegistrations`용 XML 문서가 `BuildDiagnosticsSources`
  위에 `<summary>` 두 개로 겹쳐 붙어 있고 정작 `BuildHealthRegistrations`(줄 520)는 무문서.
- `ServerBuilder.cs:472` — "미들웨어는 ConfigureDispatcher 에서 이미 배선됐다"는 주석이
  stale(실제 배선은 같은 Build 안 줄 419-422).
- `SessionResumeDispatch.cs:136-143, 173-180, 188-195` — 응답에 `sequence: 0` 하드코딩.
  `FrameWriter` 문서가 "기본값 0은 있는 척하는 헤더 필드"라며 필수 인자로 만든 바로 그 값 —
  프레임워크 자신이 원칙을 어긴다(최소한 주석으로 근거를).
- `PayloadCompressionOptions.Validate()`는 아무도 호출하지 않는다(빌더를 거치지 않고
  `FrameWriter`에 직접 전달되는 옵션).
- `ServerBuilder.AddHealthCheck` — 중복 이름 미검증("등록마다 고유해야" 문서만 있음).
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [평가] H-16. `.UseTcp(port)` 확장 메서드 어셈블리 위치 (ROADMAP 미결 항목)

- **현재 상태 평가**: 인스턴스 전달(`UseTransport(new TcpServerTransport(...))`)로 의존 방향을
  지키는 현 선택은 **옳다**. 확장 메서드를 전송 어셈블리에 두는 안은 방향 역전이라 기각이 맞고,
  남은 선택지는 (a) Hosting과 어댑터를 모두 참조하는 별도 조립 어셈블리(`ChServerM.Hosting.Extensions`
  또는 어댑터별 `ChServerM.Composition.Tcp`), (b) 메타 패키지. 어느 쪽이든 **순수 additive**라
  Shipped 동결과 충돌하지 않는다 — **1.0 차단 요소가 아니며 1.0 이후 결정해도 API 부채가 생기지
  않는다.** 유일한 주의점: 확장 메서드의 시그니처가 어댑터 옵션 타입을 노출하므로, 만들 때 그
  어셈블리 자체에도 PublicApiAnalyzers를 걸 것.

## 검증 완료로 확인한 잘 된 부분

- **인증 우회 불가 구조**: 미들웨어 체인이 라우팅 터미널을 감싸는 구성이라 미등록 ID로도
  필터·인증·속도 제한을 우회할 수 없다. 필터→인증→인가 순서 게이트(`EnsureKnownMiddlewareOrder`)의
  3쌍 비교도 정확하다.
- **생명주기**: `async void` 없음, 이중 Dispose 안전(`Interlocked.Exchange`), 종료 순서 강제,
  `DrainAsync`의 드레인 상한/절차 취소 토큰 분리는 모범적.
- **9절 준수**: 락-프리 상태의 `finally` 복원(스윕 게이트, 메트릭 게이지, 추적 feature, 브레이커
  시험 슬롯), 무제한 큐 없음(재조립·해제 버퍼·리플레이 등록부·주소 슬롯 전부 유계),
  `Volatile`/`Interlocked` 일관 사용, 커넥션당 순차 컨텍스트를 이용한 무락 버킷(9.1 파티셔닝).
- **핫패스 할당**: 프레임당 할당 0 주장(동기 핸들러 기준)은 `PartitionDispatchGate`·
  `MessageContext` 재사용·`stackalloc` 태그로 실제 뒷받침된다. 예외는 H-4의 실패 경로
  `ToString()` 하나다.
