# 감사 01 — Core 추상화 (`ChServerM.Core`)

> 전수 감사 2026-08-18. 대상: `Server/ChServerM.Core/` 87파일(obj 제외) 전량 정독.
> 우선순위: P0=정확성 버그 또는 1.0 전 필수 API 변경 · P1=성능·구조상 중요 · P2=개선 권장 · P3=선택.
> 인덱스: [00-summary.md](00-summary.md)

## 요약

`ChServerM.Core`는 전반적으로 **매우 높은 품질**의 추상화 계층이다. 무의존 하드 룰이 MSBuild
타깃(CHSM0001)으로 기계 강제되고, 벤더 타입 노출이 전혀 없으며, LINQ·`lock`·`params`·`Task.Run`·
`async void`·블로킹 호출이 어셈블리 전체에 **0건**이다. `TimeProvider`, throw-helper 계열,
`BinaryPrimitives` 전용 코덱, `ReadOnlySpan<MetricTag>` 메트릭 API, struct 제약 제네릭 작업
(`TryPost<TWork> where TWork : struct`) 등 .NET 10 관용구가 이미 정착해 있고,
`SearchValues`/`System.Threading.Lock`/`Vector512`/`CompositeFormat` 등은 Core에 적용 지점
자체가 없다(텍스트 파싱·락·SIMD 루프가 어댑터 소관). 피보나치 해싱 + 곱셈-시프트 축소,
"가장 제한적 기본값" 원칙, Try-패턴 일관 적용은 승계할 자산이다.

다만 그 원칙을 **정확히 한 곳이 위반**하고 있고(`DispatchStatus.Handled = 0`), Try-패턴
nullability 어노테이션 누락과 `ClusterView` 내부 배열 노출 등 **Shipped 동결 전에만 싸게 고칠 수
있는 결함**이 소수 있다. `PublicAPI.Shipped.txt`가 아직 비어 있어(전량 Unshipped) 지금이 실제로
마지막 기회가 맞다.

## 발견 사항

### [P0] C-1. `DispatchStatus.Handled = 0` — 기본값이 "성공"인 유일한 결과 enum

- **위치**: `Server/ChServerM.Core/Dispatch/DispatchStatus.cs:19`
- **현재 구현**: `Handled = 0`. 이 코드베이스의 다른 모든 결과 enum은 0을 센티넬 또는 가장
  제한적 값으로 둔다 — `SecureChannelStatus.None=0`, `HealthStatus.Unhealthy=0`,
  `PasswordVerification.None=0`, `VersionHandshakeStatus.None=0`, `ClusterRouteKind.Unspecified=0`,
  `SessionResumeStatus.Unspecified=0`, `CloseReason.None=0`.
- **문제**: `default(DispatchStatus)`·`default(ValueTask<DispatchStatus>)`·초기화 누락 경로가 전부
  **"정상 처리됨"으로 위장**된다. "Handled가 아닌 모든 값은 반드시 메트릭에 기록된다"는 이 enum의
  존재 목적(파일 문서 13행) 자체가 기본값에 의해 우회된다. 미들웨어가 결과를 설정하지 못한 채
  흘러가면 거부·유실이 성공으로 집계된다 — 프로젝트가 반복 거부해 온 "조용한 기본값이 실패
  지점과 겹치는" 정확히 그 패턴(CLAUDE.md 8.1 RS0026 사례와 동일 원리).
- **대안**: `None = 0`(센티넬, 관측되면 조립 버그) 추가, `Handled = 1`부터 재번호. 이 enum은
  와이어 포맷이 아니므로(와이어 수치는 `VersionHandshakeCodec`이 별도 동결) 지금 재번호는 안전.
  Hosting의 switch/매핑 갱신 동반.
- **1.0 전 필수**: **예.** 릴리스 후 enum 값 변경은 바이너리 파괴적 변경이다.
- **난이도**: 낮음

### [P1] C-2. Try-패턴 out 매개변수의 nullability 어노테이션 누락 3곳

- **위치**:
  - `Server/ChServerM.Core/Cluster/ClusterView.cs:168` — `TryGetNode(NodeId, out ClusterNode? node)`
  - `Server/ChServerM.Core/Cluster/IClusterRouter.cs:59` — `TryGetOwner(PartitionKey, out ClusterNode? owner)`
  - `Server/ChServerM.Core/Serialization/IMessageSerializer.cs:43` — `TryDeserialize(in ReadOnlySequence<byte>, out TMessage message)`
- **현재 구현**: 어셈블리 전체에서 `[NotNullWhen(true)]`를 쓰는 곳은 `ISecretSource.TryGetSecret`
  단 한 곳뿐. 클러스터 두 API는 `out ClusterNode?`인데 어노테이션이 없어 `true` 반환 후에도
  호출자가 null 경고를 받거나 `!`를 강요당한다. 반대로 `TryDeserialize`는 `out TMessage`
  (무어노테이션)라 참조형 `TMessage`에서 **실패 시에도 non-null을 거짓 보장**한다 — BCL
  `Dictionary.TryGetValue`가 `[MaybeNullWhen(false)]`를 쓰는 정확한 이유.
- **대안**: 클러스터 둘에 `[NotNullWhen(true)]`, `TryDeserialize`에 `[MaybeNullWhen(false)]`.
  PublicAPI 승인 파일의 nullability 표기도 함께 갱신.
- **1.0 전 필수**: **예.** 어노테이션은 컴파일 계약이며 나중에 바꾸면 소비자 빌드 경고가 뒤바뀐다.
- **난이도**: 낮음

### [P1] C-3. `ClusterView.Nodes`가 내부 배열을 그대로 노출 — 불변 스냅샷 계약이 캐스팅 한 번에 깨진다

- **위치**: `Server/ChServerM.Core/Cluster/ClusterView.cs:151` (`Nodes = sorted;`), 156행
- **현재 구현**: 생성자에서 만든 `ClusterNode[] sorted`를 `IReadOnlyList<ClusterNode>` 프로퍼티에
  직접 대입.
- **문제**: 호출자가 `(ClusterNode[])view.Nodes`로 다운캐스트하면 배열을 **쓸 수 있다**. 이 타입의
  문서(76~107행)와 `IClusterRouter`의 전체 동시성 스토리("한 작업은 뷰를 한 번만 읽는다",
  라우터-뷰 결박)가 스냅샷 불변성 위에 서 있는데, 그 불변성이 타입으로 강제되지 않는다 —
  "규약을 주석에만 적으면 반드시 샌다"(CLAUDE.md 9.7) 위반.
- **대안**: `Array.AsReadOnly(sorted)`(뷰 생성은 저빈도라 래퍼 1회 할당 무해) 또는 내부는 배열
  유지 + 프로퍼티만 `ReadOnlyCollection` 반환. public 시그니처(`IReadOnlyList<ClusterNode>`) 불변.
- **1.0 전 필수**: 시그니처 변경은 없으나 방어 동작 변경이므로 지금이 적기.
- **난이도**: 낮음

### [P1] C-4. ID·값 타입 전부 `ISpanFormattable`/`IUtf8SpanFormattable` 미구현 — 무할당 로깅 축과 어긋난다

- **위치**: `Identity/ConnectionId.cs:23`, `Identity/SessionId.cs:21`(JobId·NodeId 포함),
  `Identity/ObjectId.cs:32`, `Identity/MessageId.cs:24`, `Identity/PartitionKey.cs:27`,
  `Time/MonotonicTimestamp.cs:35`, `Diagnostics/EventId.cs:21`, `Content/ContentFingerprint.cs:41`,
  `Handshake/ProtocolVersionRange.cs:26`
- **현재 구현**: 진단 표현이 전부 `override string ToString()`(할당) 하나뿐.
  `string.Create(InvariantCulture, $"...")`로 단일 할당까지는 이미 최적화돼 있다.
- **문제**: 로깅 기본 축은 ZLogger(무할당)이고, ZLogger·`Utf8.TryWrite`·보간 핸들러는
  `ISpanFormattable`/`IUtf8SpanFormattable` 구현 타입을 **문자열 할당 없이** 인라인 포맷한다.
  커넥션 수립/종료·거부·라우팅 로그마다 ID가 실리는데, 지금은 그때마다 `ToString()` 할당이
  강제된다. 핫패스(프레임당)는 아니지만 커넥션 이벤트 빈도로는 실질 이득이 있고, 무엇보다
  **인터페이스 추가는 지금(전량 Unshipped)이 가장 싸다**.
- **대안**: 위 값 타입들에 `ISpanFormattable, IUtf8SpanFormattable` 구현
  (`TryFormat(Span<char>...)`/`TryFormat(Span<byte>...)`, 기존 `ToString`은 위임으로).
  효과 주장은 프로젝트 규칙대로 로깅 벤치와 함께.
- **1.0 전 필수**: 추가적(additive)이라 이후에도 가능은 하나, 표면 확장이므로 동결 전 일괄 반영이 정석.
- **난이도**: 중간 (타입 10여 개 × 2 인터페이스 + PublicAPI 갱신 + 테스트)

### [P2] C-5. `SessionHandshakeCodec.TryReadResumeResponse`가 정의되지 않은 상태 바이트를 "성공"으로 통과시킨다

- **위치**: `Server/ChServerM.Core/Sessions/SessionHandshakeCodec.cs:168`
- **현재 구현**: `status = (SessionResumeStatus)payload[StatusOffset];` — 값 검증 없이 캐스팅하고
  `true`를 반환한다. 바이트가 0(`Unspecified`)이든 200이든 "형식이 맞다"고 답한다.
- **문제**: 같은 어셈블리의 `VersionHandshakeCodec`은 "부트스트랩에 관대한 수신은 없다"며 고정
  필드 전수 검증을 하고, `FrameDecodeStatus.InvalidFlags`는 "모르는 비트를 무시하지 않는다"를
  명문화한다. 이 코덱만 손상 프레임을 파싱 성공으로 위장시켜 그 원칙과 어긋난다. `Unspecified`
  enum 문서 스스로 "빈 버퍼를 성공으로 오독하지 않는다"고 했는데 그 방어가 실제 코드에 없다.
- **대안**: `payload[0]`이 `Resumed(1)`/`Rejected(2)`가 아니면 `false` 반환. 시그니처 불변,
  와이어 동작만 엄격화.
- **1.0 전 필수**: 시그니처는 그대로지만 **와이어 수용 동작은 동결 대상**이므로 지금 고치는 것이 맞다.
- **난이도**: 낮음

### [P2] C-6. `NodeId.None`(=0)과 "유효한 노드 0"의 의미 충돌 + `IsNone` 부재

- **위치**: `Server/ChServerM.Core/Identity/SessionId.cs:122-159` (NodeId),
  `Identity/ObjectId.cs:67-74` (`Create`가 nodeId 0 허용)
- **현재 구현**: `ObjectId.Create`는 노드 0~1023을 유효로 받는다. 한편 `NodeId.None => default`는
  값 0이다. 즉 노드 번호 0으로 배포된 노드는 `NodeId.None`과 구분 불가. 또 형제 ID 타입들
  (`ConnectionId`·`SessionId`·`JobId`·`ObjectId`·`MessageId`)이 전부 갖춘 `IsNone` 프로퍼티가
  NodeId에만 없다.
- **문제**: `ClusterNode`·`ClusterView`가 NodeId를 정체성 키로 쓰는데(중복 검사까지 하면서),
  "미설정"과 "0번 노드"가 같은 값이면 조립 실수(노드 번호 미기입)가 유효한 구성으로 통과할 수
  있다 — 이 프로젝트가 센티넬을 두는 이유 그 자체.
- **대안**: 둘 중 하나를 동결 전에 결정한다. (a) 노드 0을 예약하고 `NodeId` 생성자·
  `ObjectId.Create`에서 클러스터 문맥의 0을 거부, `IsNone` 추가. (b) 0을 유효로 확정하고
  `NodeId.None`을 제거(또는 문서로 "None 개념 없음" 명시). 어느 쪽이든 지금이 유일하게 싼 시점.
- **1.0 전 필수**: **예** — 표면(멤버 추가/삭제) 또는 계약 의미가 바뀐다. **설계 결정 필요(사용자 판단)**.
- **난이도**: 중간

### [P2] C-7. `ClusterView` 조회 딕셔너리 — `FrozenDictionary` 후보

- **위치**: `Server/ChServerM.Core/Cluster/ClusterView.cs:111,133` (`Dictionary<NodeId, ClusterNode> _byId`)
- **현재 구현**: 생성 후 절대 변경되지 않는 `Dictionary`를 `TryGetNode`/`Contains`가 읽는다.
- **문제**: 뷰는 멤버십 변경 시에만 만들어지고(저빈도) 조회는 라우팅 결정마다 일어난다
  (`IClusterRouter` 문서가 "메시지마다 불릴 수 있다"고 명시). 정확히
  `System.Collections.Frozen.FrozenDictionary`(BCL, 무의존 룰 충족)의 설계 시나리오다.
- **대안**: `_byId`를 `FrozenDictionary<NodeId, ClusterNode>`로 교체(`ToFrozenDictionary`).
  내부 구현만 바뀌고 public 표면 불변. before/after 벤치 동반("측정 없는 최적화 금지").
- **1.0 전 필수**: 아니오 (내부 구현).
- **난이도**: 낮음

### [P2] C-8. `MonotonicTimestamp.Add`의 double 경유 변환 — 정밀도 손실

- **위치**: `Server/ChServerM.Core/Time/MonotonicTimestamp.cs:97`
  (`(long)(delta.TotalSeconds * timeProvider.TimestampFrequency)`)
- **현재 구현**: `TimeSpan` → double 초 → 주파수 곱 → long 절단(truncation).
- **문제**: Linux(주파수 10⁹)에서 double 반올림 + 0방향 절단으로 수백 ns 오차가 생기고, 큰
  delta에서는 53비트 가수 한계에 접근한다. `Add`로 만든 데드라인과 `ElapsedTo`(정수 경로)를
  대조하는 결정적 테스트가 틱 단위로 어긋날 수 있다. RealTime 축의 타이밍 휠이 이 타입 위에
  데드라인을 쌓으므로 저비용에 고칠 가치가 있다.
- **대안**: 정수 경로 — `Int128`(또는 `Math.BigMul`)로
  `delta.Ticks * frequency / TimeSpan.TicksPerSecond`. 주파수 10⁷(Windows)에서는 결과 동일,
  10⁹에서 정확해진다. 표면 불변.
- **1.0 전 필수**: 아니오 (구현만).
- **난이도**: 낮음

### [P3] C-9. `PartitionKey.ToIndex` — `partitionCount ≤ 0` 무방비

- **위치**: `Server/ChServerM.Core/Identity/PartitionKey.cs:53-58`
- **현재 구현**: 문서는 "1 이상이어야 한다"고 적었지만 검사가 없다. 0이면 항상 0을 반환(호출자
  쪽에서 엉뚱한 IndexOutOfRange로 표출), 음수면 `(ulong)` 캐스팅 랩어라운드로 쓰레기 값.
- **대안**: 핫패스이므로 릴리스 비용 0인 `Debug.Assert(partitionCount >= 1)` 한 줄.
  (`IExecutionModel`이 시작 시점 검증을 하므로 정상 경로는 안전 — 방어는 디버그로 충분.)
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

### [P3] C-10. `FrameDecodeResult.Equals`의 `ReadOnlySequence<byte>` 비교 — 박싱 + 미문서 의미

- **위치**: `Server/ChServerM.Core/Framing/FrameDecodeResult.cs:113-118`
- **현재 구현**: `Payload.Equals(other.Payload)` — `ReadOnlySequence<T>`는 `IEquatable`이 아니고
  `Equals(object)` 오버라이드도 없어 **`ValueType.Equals`로 폴백하며 인자를 박싱**한다. 의미도
  내용 비교가 아니라 위치(참조) 비교인데 문서화돼 있지 않다.
- **문제**: 핫패스는 아니지만(동등 비교는 사실상 테스트 전용) 무할당 어셈블리에서 유일한 잠재
  박싱 지점이고, 리플렉션 경로는 AOT 크기에도 미세하게 기여한다.
- **대안**: `Payload.Start.Equals(other.Payload.Start) && Payload.End.Equals(other.Payload.End)`
  (SequencePosition은 IEquatable)로 명시 비교하거나, 동등성에서 Payload를 제외하고 문서화.
- **1.0 전 필수**: 아니오 (동작 의미를 바꾼다면 지금이 낫다). / **난이도**: 낮음

### [P3] C-11. `MetricTag` 생성자 매개변수와 프로퍼티의 nullability 불일치

- **위치**: `Server/ChServerM.Core/Diagnostics/MetricTag.cs:28,39`
- **현재 구현**: 생성자는 `string value`(non-nullable, 검증 없음)인데 프로퍼티는 `string? Value`
  이고 문서가 "null일 수 있다(부재 표현)"고 말한다. 정직하게 null을 만들 방법이 계약에 없다.
- **대안**: 생성자를 `string? value`로 맞추거나, null 불허로 확정하고 프로퍼티를 `string`으로.
- **1.0 전 필수**: 표면 표기가 바뀌므로 동결 전 권장. / **난이도**: 낮음

### [P3] C-12. `ObjectId.Create` 문서-동작 불일치

- **위치**: `Server/ChServerM.Core/Identity/ObjectId.cs:63,69-70`
- **현재 구현**: `timestampMs` param 문서는 "TimestampBits비트에 맞게 **잘린다**"인데 코드는 범위
  초과 시 **throw**한다(올바른 쪽은 코드다).
- **대안**: 문서를 "범위를 벗어나면 예외"로 정정.
- **1.0 전 필수**: 아니오 (문서만). / **난이도**: 낮음

### [P3] C-13. `HealthReport`가 호출자 리스트를 복사 없이 보관

- **위치**: `Server/ChServerM.Core/Diagnostics/HealthReport.cs:26-31`
- **현재 구현**: `IReadOnlyList<HealthReportEntry>`를 그대로 저장 — 호출자가 원본 `List`를 이후에
  수정하면 "보고서"가 따라 변한다.
- **대안**: 비핫패스(문서 스스로 명시)이므로 `entries.ToArray()` 방어 복사. 표면 불변.
- **1.0 전 필수**: 아니오. / **난이도**: 낮음

## 승계할 자산

- **결과-값 규약의 일관성**: `FrameDecodeResult`·`SecureChannelResult`·`SessionWriteResult`·
  `AuthenticationResult` 등 팩토리 + 센티넬 차단(`Failed`가 성공 상태를 거부)이 전 어셈블리에
  균질하다. P0의 `DispatchStatus`만 이 대열에서 이탈해 있다.
- **`VersionHandshakeCodec`/`ContentFingerprintCodec`**: 동결 상수의 의도적 중복과 그 근거,
  엄격 파싱, stackalloc 무할당 — 문서·코드가 정확히 일치하는 모범.
- **`IExecutionPartition`의 배타성 정의**(스레드 어피니티가 아니라 완료 대기)와 3진입로 문서화,
  `ISessionStore`의 바이트 계약 + CAS + TTL을 v1에 넣은 근거 — 1.0 표면으로 손색없다.
- 4계층 한글 주석 규약(8.2)이 실제로 전 파일에서 지켜지고 있으며, 특히 "막고 있는 레거시 결함"
  명시가 일관된다.

## 1.0 전 필수 요약

C-1(DispatchStatus 재번호) · C-2(어노테이션 3곳) · C-3(ClusterView 배열 방어) ·
C-6(NodeId 0 의미 결정 — **사용자 결정 필요**) · C-5(재개 응답 상태 검증) —
전부 합쳐도 반나절 규모이며, `PublicAPI.Shipped.txt`가 아직 비어 있어 지금은 전부 Unshipped
편집으로 끝난다.
