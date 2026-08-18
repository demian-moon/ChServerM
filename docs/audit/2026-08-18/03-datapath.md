# 감사 03 — 데이터 경로 (Buffers · Framing · 직렬화 · 압축 · SourceGen)

> 전수 감사 2026-08-18. 대상: `ChServerM.Buffers` · `ChServerM.Framing` · `ChServerM.Compression.LZ4` ·
> `ChServerM.Serialization.{MemoryPack,Protobuf,FlatBuffers}` · `ChServerM.SourceGen` 전 파일 정독.
> 우선순위: P0=정확성/1.0 필수 · P1=중요 · P2=권장 · P3=선택. 인덱스: [00-summary.md](00-summary.md)

## 요약

**P0(정확성 버그)은 발견하지 못했다.** 고정 헤더 디코더의 단일 세그먼트 fast path + 16B stackalloc
slow path, `uint` 채 비교로 부호 오버플로 차단, varint 정규형 강제, MemoryPack/Protobuf의
`IBufferWriter`·`ReadOnlySequence` 직결 무복사 경로, LZ4의 "버퍼 잡기 전 선언 길이 검증"(zip bomb
방어), FlatSharp Lazy 거부(계약 위반을 조립 시점 예외로), `IIncrementalGenerator` +
`ForAttributeWithMetadataName` + `EquatableArray` 증분 파이프라인, 배열 기반 라우팅 테이블까지
감사 관점 대부분이 이미 모범적으로 구현돼 있고 근거가 BENCHMARKS.md 실측(프레이밍 16~34ns,
LZ4 vs Brotli 비교 등)으로 방어된다. 남는 것은 운영 스케일에서의 메모리 정착 문제 1건(P1)과
방어적 하드닝·개선 권장 P2/P3 들이다. **1.0 전 public API 표면 변경을 강제하는 발견은 없다**
(P1 제안도 additive API로 해결 가능).

## 발견 사항

### [P1] D-1. `PooledBufferWriter`의 정착 크기가 영구 유지된다 — 커넥션 수 × 최대 응답 크기만큼 메모리가 눌러앉는다

- **위치**: `Server/ChServerM.Buffers/PooledBufferWriter.cs:88-93` (`Clear`), `:26-29`(의도된 사용법 주석)
- **현재 구현**: "커넥션당 하나를 만들어 응답마다 재사용"이 의도된 사용법이고, `Clear()`는 버퍼를
  유지한 채 길이만 되돌린다. 성장은 2배 대여-복사-반납이며 **축소 경로가 없다**.
- **문제**: 한 커넥션이 큰 응답(예: 기본 상한 1MiB 근처)을 한 번이라도 직렬화하면 그 버퍼는
  커넥션 수명 내내 풀 밖에 잡혀 있다. 1만 접속 최악 시나리오에서 수 GB가 스파이크 후에도
  반납되지 않는다 — 레거시의 "커넥션당 64KB 고정 버퍼(1만 접속 = 640MB)"를 결함으로 판정한
  프로젝트 기준(FramingOptions.cs:24 주석)에서, 이 구조의 최악치는 그보다 크다.
  BENCHMARKS.md:1263도 이 특성을 인지하고 있다.
- **대안**: `Clear(int maxRetainedCapacity)` 오버로드 또는
  `PooledBufferWriter(initialCapacity, maxRetainedCapacity)` 옵션 — Clear 시 용량이 임계 초과면
  반납하고 다음 사용 때 재대여(대여/반납은 이미 `BufferPoolDiagnostics`로 관측됨). 임계값은
  벤치마크와 함께 결정.
- **1.0 전 필수**: 아님 — additive API. 다만 기본 생성자 계약 논의가 쉬운 지금이 낫다.
- **난이도**: 낮음

### [P2] D-2. LZ4 해제가 벤더의 문서화되지 않은 예외 계약에 의존 — 사전 정합성 가드와 퍼징으로 보강

- **위치**: `Server/ChServerM.Compression.LZ4/Lz4PayloadCodec.cs:160-178` (`DecodeBlock`), `:104-115`
- **현재 구현**: 손상 입력의 실패를 `InvalidOperationException`/`ArgumentException`/
  `IndexOutOfRangeException` 3종 catch로 값(-1)으로 바꾼다. 주석 스스로 "던지지 않는다가 벤더의
  문서화된 계약은 아니다"라고 인정한다. K4os는 unsafe 포인터 경로를 쓰는 라이브러리이고 이
  코드는 원격 입력을 직접 받는다.
- **문제**: (1) 목록에 없는 예외 타입이 나오면 그대로 전파돼 계약("실패는 값이다",
  IPayloadCodec.cs:34-36) 위반 = DoS 경로. (2) `compressedLength`가
  `LZ4Codec.MaximumOutputSize(claimed)`를 초과하는 블롭(정상적으로는 존재 불가)도 벤더 디코더까지 도달.
- **대안**: ① 벤더 호출 전 `compressedLength > LZ4Codec.MaximumOutputSize((int)claimed) → false`
  사전 거부(1줄, 할당 없음). ② 손상·절단·비트 플립 입력 코퍼스로 반복 퍼즈 테스트를 Tests에
  추가해 3종 catch의 충분성을 회귀로 고정.
- **1.0 전 필수**: 아님(public API 불변). 단 신뢰 경계 하드닝이므로 THREAT-MODEL 전에 권장.
- **난이도**: 낮음

### [P2] D-3. `EnsureCapacity` 정수 오버플로 시 조용한 계약 위반 (성장 생략)

- **위치**: `Server/ChServerM.Buffers/PooledBufferWriter.cs:140-148`
- **현재 구현**: `int required = _written + Math.Max(sizeHint, 1);` — `_written + sizeHint`가 int를
  넘으면 음수가 되어 `required <= buffer.Length`가 참이 되고, **성장 없이 통과**한다. 이후
  `GetSpan`은 sizeHint보다 작은 스팬을 돌려준다(IBufferWriter 계약 위반 — 호출한 직렬화기의
  IndexOutOfRange 또는 오동작으로 뒤늦게 드러난다).
- **문제**: 현재 호출자(프레이밍 상한 64MiB, 압축 상한)로는 실질 도달 불가하지만, public 타입이라
  외부 소비자의 sizeHint를 통제할 수 없다. "조용한 실패" 유형이라 프로젝트 원칙과 어긋난다.
- **대안**: `long required = (long)_written + Math.Max(sizeHint, 1);` 후 `Array.MaxLength` 초과 시
  즉시 실패. 핫패스 비용 0(동일 비교 1회).
- **1.0 전 필수**: 아님(동작 변경은 실패 경로뿐). / **난이도**: 낮음

### [P2] D-4. Buffers 어셈블리가 문서상 역할("슬랩 할당, 보유 상한 풀")에 미달 — 모든 축이 `ArrayPool.Shared` 하나를 공유

- **위치**: `Server/ChServerM.Buffers/`(파일 2개), 대조: CLAUDE.md 5절 레이아웃 설명
- **현재 구현**: 슬랩 할당기·전용 풀 없음. 프레이밍(디코더 stackalloc 제외), LZ4 다중 세그먼트,
  FlatSharp 다중 세그먼트, FrameWriter 압축 경로가 전부 `ArrayPool<byte>.Shared`를 직접 쓴다.
- **문제**: Shared 풀은 프로세스 전역이라 다른 라이브러리와 경합하고 보유 상한 제어가 없다.
  BENCHMARKS.md:1308이 이미 "커넥션 버퍼를 전용 풀로 옮기는" 방향을 언급 — 인지된 갭. 다만
  현 사용처는 전부 짧은 스코프 대여(finally 반납)라 실측 문제가 관측되기 전 선행 구현은
  "측정 없는 최적화 금지"에 걸린다.
- **대안**: 현상 유지 + CLAUDE.md 레이아웃 설명을 실태에 맞추거나, 전용 풀 도입 시
  벤치마크(Shared 대비)와 ADR을 함께.
- **1.0 전 필수**: 아님. / **난이도**: 중간(전용 풀 구현 시)

### [P3] D-5. `VarintFrameDecoder`에 단일 세그먼트 fast path 없음

- **위치**: `Server/ChServerM.Framing/VarintFrameDecoder.cs:75-121`
- **현재 구현**: 항상 `SequenceReader<byte>`로 바이트 단위 `TryRead`. 고정 헤더 디코더
  (FixedHeaderFrameDecoder.cs:114-119)는 `FirstSpan` fast path가 있는데 varint 쪽은 없다.
- **문제**: `SequenceReader.TryRead`는 세그먼트 경계 검사가 바이트마다 붙는다.
  `buffer.FirstSpan.Length >= MaxHeaderSize(8)`이면 스팬 인덱싱 경로를 둘 수 있다. 단,
  실측(16~34ns)이 이미 목표 안이므로 벤치 수치가 나올 때만.
- **대안**: 스팬 오버로드 추가 후 before/after를 `perf(framing)` 커밋에.
- **1.0 전 필수**: 아님(internal 코덱). / **난이도**: 낮음

### [P3] D-6. `BufferPoolDiagnostics` 카운터 3개가 같은 캐시 라인 (9.4 false sharing)

- **위치**: `Server/ChServerM.Buffers/BufferPoolDiagnostics.cs:25-27`
- **현재 구현**: `_rented/_returned/_leaked` long 3개가 인접 배치, 전 스레드가 `Interlocked.Increment`.
- **판단**: 대여/반납은 생성·성장·Dispose 시점뿐이라(메시지당 아님) 정상 상태에서는 냉경로 —
  주석도 "파티션별 분리는 경합이 관측되면"으로 유보. 관측 후 대응이면 충분.
- **대안**: 경합 관측 시 128B 패딩 또는 파티션별 카운터.
- **1.0 전 필수**: 아님. / **난이도**: 낮음

### [P3] D-7. 소스젠 모델의 Location 포함으로 인한 증분 캐시 정밀도 저하

- **위치**: `Server/ChServerM.SourceGen/MessageHandlerGenerator.cs:98`, `LocationModel.cs:12-19`
- **현재 구현**: `HandlerModel`/`RowModel`이 오프셋 기반 `LocationModel`을 담는다(값 동등성은
  확보 — `Location` 원본을 담는 흔한 실수는 피했다).
- **판단**: 핸들러 선언 위쪽 코드 편집 시 오프셋이 밀려 디스패치 맵이 재생성되지만, 생성 자체가
  싸서 실해는 작다. Roslyn 커뮤니티 표준 트레이드오프 범위 — **현상 유지 권장**.
- **1.0 전 필수**: 아님. / **난이도**: 중간

### [P3] D-8. 생성된 `MapGeneratedHandlers`의 핸들러당 매개변수 1개 — 대규모에서 시그니처 비대

- **위치**: `Server/ChServerM.SourceGen/MessageHandlerGenerator.cs:201-223`
- **판단**: 핸들러 수십 개까지는 "빠뜨림이 컴파일 오류"라는 장점이 우세(의도된 설계, ADR-0014).
  수백 개 규모가 되면 어트리뷰트 인자 기반 핸들러 그룹별 부분 맵 생성을 검토. 지금은 변경 불요.
- **1.0 전 필수**: 아님. / **난이도**: 중간

## 잘 된 부분 (감사 관점별 확인 결과)

- **프레이밍**: `BinaryPrimitives` 선택 근거가 코덱 주석에 명시되고 와이어 레이아웃 지식이
  `FrameHeaderCodec` 한 곳에 격리. 단일 세그먼트 fast path(`FirstSpan`) + 경계 넘는 헤더
  stackalloc, `uint` 채 상한 비교(음수 길이 원천 차단), 검증 순서(버전→길이→플래그→예약) 문서화.
  체크섬 부재는 Phase 9 AEAD 위임으로 ADR화.
- **압축**: K4os 1.3.8(최신), 블록 포맷 + 자기서술 4B 접두(ADR-0019), 해제 상한을 계약 표면에
  강제(`maxDecodedLength` 필수 인자), 문턱(1024B)·비압축성 스킵·플래그 자동 부여는
  `FrameWriter`에서 finally 반납과 함께 정상 구현.
- **직렬화**: MemoryPack `Serialize(IBufferWriter)`/`Deserialize(ReadOnlySequence, ref)` 직결 +
  엄격 소비 검사 + struct 박싱 회피 null 검사, Protobuf `WriteTo(IBufferWriter)`/
  `ParseFrom(ReadOnlySequence)` 직결, FlatSharp Greedy 강제(계약 위반을 생성자 예외로).
  `ToArray()` 류 중간 복사 없음. AOT 억제(IL2091/IL2059)에 근거 주석.
- **SourceGen**: `IIncrementalGenerator` + `ForAttributeWithMetadataName` + 값 동등성 record +
  `EquatableArray`(ImmutableArray 참조 비교 함정 회피) — 규약 완비. 라우팅은 최종적으로 배열
  테이블(MessageDispatcherBuilder.cs:233)로 굳는다.
- **무할당 위반 없음**: 런타임 어셈블리 핫패스에서 박싱·클로저·LINQ·문자열 포맷 발견 0건 —
  LINQ는 빌드 타임(SourceGen) 전용.
- `SearchValues` 적용처는 현재 없음(델리미터 프레이밍 미구현) — 해당 축 구현 시 후보로 기억.
