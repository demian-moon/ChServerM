# 마이그레이션 가이드 — 레거시 서버에서 옮겨오는 경로

이 문서는 `LegacyServer/`(111파일·약 26,000줄) + `LagacyClient/`(6파일·1,298줄)를
ChServerM 으로 옮기는 경로다. 근거는 전수 정독 분석 14종([docs/legacy/](legacy/00-overview.md))
이고, 각 대응의 원문 판정은 해당 문서에 있다.

## 0. 전제 — 개선해서 옮기는 것이 아니다

> **"설계를 승계하고 구현은 재작성한다."** (legacy/00-overview, ADR-0000)

전수 분석의 결론: 동작하며 승계 가치가 있는 코드는 **절반 이하**(승계·개작 대상 ~45%,
미참조 35%, 주석·폐기 20%)이고, 그 안에도 치명 결함 40건 이상이 있다. 결함의 공통 원인은
두 가지다 — ① 락 없는 카운터·플래그를 `try/finally` 없이 조작(예외 하나가 영구 정지),
② 풀 대여물의 소유권이 주석에만 있고 타입에 없음. ChServerM 은 이 둘을 규약(CLAUDE.md 9.2)
과 타입(Phase 3 소유권)으로 봉인했다 — **레거시 코드를 복사해 오면 봉인을 뜯는 것이다.**

## 1. 개념 대응표 — 레거시의 X 는 어디로 갔는가

자주 찾는 것부터. 전체 판정은 [legacy/00-overview.md](legacy/00-overview.md) §3.

| 레거시 | ChServerM | 비고 | 근거 |
|---|---|---|---|
| `ServerM` accept 루프 + `Task.Run` | `IServerTransport` + `ChServerMServer` (Bind→Unbind→Stop 드레인) | 생명주기가 계약이 됐다 | 01 |
| `AbNetworkBase`/`ClientM` Template Method 상속 | `ServerBuilder`/`ClientBuilder` 조립 | 상속 → 조립 | 01·05 |
| `IoPipelineSrvM`/`IoPipelineClaM` 쌍둥이 400줄 | `IFrameDecoder`/`IFrameEncoder` 한 벌 (서버·클라 공용) | 중복 폐기 | 05 |
| FlatBuffers 3단 패킷(PK_HEAD 28B+CONTENT_HEAD 24B) | 고정 헤더 struct(13~16B) ↔ 직렬화 분리 | 헤더 52B → 16B, ADR-0002 | 02 |
| `oid % n` 샤딩 (3곳에서 재발명) | `PartitionKey`(피보나치 해싱) + `PartitionedExecutionModel` | 🟢 최고 자산 — 구현은 재작성 | 01·04 |
| `UserM`/`InnerUserM` 래퍼 핸들 | `ConnectionId`(slot+generation) — 조회 할당 0 | 세션 쪽은 `ISessionStore` 와 함께 | 06 |
| `GlobalM.MakeGameOid()` 전역 카운터 | `ObjectId` — **노드 성분 필수** | 다중 노드 충돌·재사용 봉인 | 06 |
| `TimeEventSchedulerM` 5단 타이밍 휠 | `TimerWheel`(RealTime) — 스레드 없는 수동 구조, `TickLoop` 이 드라이버 | 설계 승계 + 결함 전수 수정 | 04·10 |
| `TimerM<T>` 커넥션당 타이머, 스케줄러 4종 | 전부 `TimerWheel` 로 통합 | 🔴 폐기 | 10 |
| `TickTimeM` | `TimeProvider` + `MonotonicTimestamp` | 시계 역행은 감추지 않고 드러낸다 | 10 |
| `AllowedPacketMan` 상태 화이트리스트 | 미들웨어(`IServerMiddleware`) + `IAuthorizationPolicy` — **라우팅보다 앞** | O(n) → O(1) | 02 |
| `MemPkDispatcher` + Dictionary | `[MessageHandler]` + 소스 생성 등록 | 컴파일 타임 검증(CHSM1xxx) | 02 |
| `PkObjM` 송신 배칭·64KB 고정 버퍼 | `IBufferWriter` 직접 쓰기 + 풀 대여(유휴 시 0) | 배칭 개념만 승계 | 02 |
| `CompressAndEncryptManM`(자체 RSA/AES/XOR) | `ITransportSecurity`(TLS 위임) + `IPayloadCodec`(LZ4) | 🔴 전량 재설계 — 자체 암호는 계승 금지 | 07 |
| `AuthM` PBKDF2 | `IAuthenticator` + Hosting 미들웨어 | 알고리즘 승계 | 07 |
| `MongoDBManagerM` 싱글턴 | `ISessionStore` 어댑터(DI 주입) | 싱글턴 폐기 | 08 |
| `AbLogM`(레벨 없음) + UDP 수신기 | `IServerLogger`(레벨·EventId) + `IMetricsSink` | 🔴 폐기 | 09 |
| `InterQuartileM`/`NetWorkDelayM` | `RttEstimator`(IQR, RealTime) | 알고리즘 승계 | 09 |
| `MortonCodeM` (+빈 QuadTree) | `RealTime.Spatial` 모튼 그리드 — 유일 생존 자산 | AOI 는 신규 설계 | 03 |
| `BoxColliderM` SAT·집합 차분 | `InterestSet` + 무할당 SAT(`Aabb`/`Obb`) | 알고리즘만 — 버그 8건 회귀 테스트로 봉인 | 03 |
| `MapObjM` 맵 브로드캐스트 | `RealTime.Rooms` 1회 인코딩 브로드캐스트 | 조립 예제: `Samples/ChServerM.Samples.GameRoom` | 03·06 |
| `NeedPkSendM` 더티 플래그 | `DirtySet<T>`(Rooms) | 🟢 승계 | 00 §3-9 |
| `MetaDataM`/`SrvTableM` + Excel 3,093줄 | `DataTable` 소스 생성 강타입 테이블(CHSM2xxx) | Excel 은 빌드 타임으로 | 11 |
| `IniSrvOptionM` 1073줄 INI 파서 | `XxxOptions` + `Validate()` | 🔴 폐기 | 11 |
| 소스 파일 링크 36개 공유 | `Core` 패키지 참조 + `Client.*` 분리 | 🔴 폐기 — 오염 경로 | 05 |
| `SrvGlobal`/`ServerGlobals` static 전역 | DI 스코프 | 🔴 폐기 | 01 |

## 2. 옮기는 순서 — 아래에서 위로, 결정 불가역부터

레거시의 데이터 흐름은 선형이다: `accept → 파이프 → 프레이밍 → 샤딩 → 화이트리스트 →
디스패치 → 핸들러`. 아래 계층부터 위로 채우면 각 단계가 앞 단계 위에서 검증된다 —
ChServerM 자체가 이 순서로 만들어졌다(Phase 1 계약 → 2 조립 → 3 버퍼 → 4 프레이밍 →
5 전송 → 7 디스패치 → 8 실행 모델).

1. **ID 부터 확정한다 — 특히 `ObjectId` 의 노드 성분.** 레거시처럼 `long` 증분으로 굳히면
   다중 노드에서 되돌릴 수 없다. 가장 먼저, 가장 신중하게.
2. **프레이밍과 직렬화를 뗀다.** 레거시는 FlatBuffers 가 헤더까지 침범해 둘이 붙어 있었다.
   이 경계가 생겨야 나머지 작업이 병렬로 갈 수 있다.
3. **인메모리 전송으로 파이프라인을 먼저 세운다.** 소켓 없이 프레이밍→디스패치→핸들러를
   끝까지 돌리고, 소켓(전송)은 그 다음이다.
4. **핸들러를 옮긴다.** 레거시 패킷 액션 1개 = `[MessageHandler]` 1개. 이때 페이로드
   수명(반환 후 무효)과 순서 보장(파티션) 규약이 레거시와 다르다 — 아래 3절.
5. **횡단 축(보안·압축·관측)은 미들웨어·데코레이터로 끼운다.** 레거시처럼 파이프라인
   본문에 심지 않는다.
6. **선택 축은 마지막에, 독립적으로.** RealTime 3종(틱·룸·공간)은 서로를 참조하지 않으니
   필요한 것만. **충돌 계층은 제일 마지막** — 레거시 충돌 코드는 미수정 버그 8건으로
   "검증된 적 없다" 판정이다. 옮기기 전에 단위 테스트부터 쓴다.
7. **클라이언트 경계를 초기에 끊는다.** 레거시는 소스 링크 공유가 클라 요구(UI 스레드,
   `A_SC_ANY_STATE` 기본값)를 서버로 역류시켰다. 패키지 참조로 못 박아 재발을 막는다.

**옮길 것이 아예 없는 항목** (잘못된 계획의 원천): `QuadTreeM`(빈 파일) ·
`MemoryPoolM`/`StackMemAllocM`(참조 0 — 버퍼는 처음부터 설계됐다) · `MembersM`(전량 주석) ·
레이팅 시스템(참조 0 — Phase 19 에서 `Samples/` 행) · `RoslynCompilerM`(미배선, 하드 룰 위반).

## 3. 핸들러를 옮길 때 바뀌는 규약

레거시 패킷 액션과 ChServerM 핸들러는 형태가 비슷해서 **그대로 옮겨질 것 같지만, 세 가지
계약이 다르다.** 셋 다 분석기·게이트가 잡아 주지만 알고 옮기는 것이 빠르다.

| 레거시에서 하던 것 | ChServerM 에서는 | 어기면 |
|---|---|---|
| 패킷 버퍼를 필드에 저장해 나중에 사용 | `Payload` 는 핸들러 반환 후 무효 — 복사(`ToArray`)하거나 반환 전에 역직렬화 | CHSM3003 경고 + 조용한 데이터 오염 |
| `.Result`/`Wait()`/`Thread.Sleep` | 전부 `await` | CHSM3002 경고 + 스레드풀 고갈 |
| `async void` 액션 | `async ValueTask` | CHSM3001 경고 + 관측 불가 예외 |
| 유저 단위 직렬성을 암묵 가정 | 파티션 실행 모델이 **계약으로** 보장 — 같은 커넥션 순차, 파티션 안에서는 락·`Concurrent*` 불필요 | 불필요한 동기화(성능) 또는 잘못된 공유(경합) |
| 무제한 큐에 밀어 넣기 | 유계 채널 + 백프레셔. `Wait` 모드면 반드시 `WriteAsync`(`TryWrite` 는 조용히 버린다) | 부하 시 패킷 유실 — 레거시 실사례 |

## 4. 회귀 방지 체크리스트

각 레거시 문서의 "새 코드에 절대 옮기면 안 되는 것" 절이 정본이다. 카테고리별 대표:

- **동시성**: `try/finally` 없는 소유권 선출(04) · `async void` 워커(04) · static 초기화
  순서 의존 `DivideByZeroException`(01) · 스레드 안전/비안전 쌍둥이 클래스(04)
- **버퍼**: `ArrayPool` 대여 후 반납 누락·경쟁(01·02·06) · 와이어 값으로 배열 할당 +
  프레임 상한 없음(07·01 — `CompositionGuard` 가 봉인) · 커넥션당 고정 64KB(02)
- **큐**: `FullMode.Wait` + `TryWrite`(04) · `CreateUnbounded` 실사용 경로(04)
- **타이머**: 휠 원점 미초기화(04) · 만료·취소 동일 경로(04) · `AddOrUpdate` 팩토리에서
  `IDisposable` 생성(10) · 경과 음수를 0 으로 뭉개기(10)
- **보안·기본값**: 무조건 `true` 체크섬(02) · 한 번도 실행되지 않는 압축 분기(07) ·
  세션 고정 IV·인증 없는 CBC(07) · **비밀번호 틀려도 로그인 통과**(05) · 없는 유저의
  기본 상태가 전부 허용(06 — ChServerM 은 "기본값은 가장 제한적"이 원칙) · 소스 내
  DB 자격증명(08) · 설정 파일 없으면 로깅 전면 소실(09)

마이그레이션 리뷰에서 이 목록과 대조한다 — "이 코드가 무엇을 하는가"보다
**"어떤 재발을 막는가"** 를 본다(CLAUDE.md 8.2).
