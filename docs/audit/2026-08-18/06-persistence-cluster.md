# 감사 06 — 영속성(세션 저장소) + 클러스터 (Persistence 3종 · Conformance · Cluster · Consul)

> 전수 감사 2026-08-18. 대상: `ChServerM.Persistence.{InMemory,Redis,Postgres}` ·
> `Tests/ChServerM.Persistence.Conformance` · `ChServerM.Cluster` · `ChServerM.Cluster.Consul` ·
> `ChServerM.Cluster.Hosting` 전 파일 정독 + `docs/CONSISTENCY.md` 주장 대조.
> 우선순위: P0=정확성/1.0 필수 · P1=중요 · P2=권장 · P3=선택. 인덱스: [00-summary.md](00-summary.md)

## 요약

세 세션 저장소 어댑터는 전반적으로 우수하다 — 계약(바이트+CAS+TTL)이 세 구현에서 각자의
네이티브 수단(참조 CAS / 단일 키 Lua / 조건부 UPDATE)으로 일관되게 만족되고, CONSISTENCY.md의
주장(단일 키 선형화, Renew 버전 불변, 만료 즉시 판정, PG 청소 배치 상한, Redis 스크립트 단일
키)은 **전부 코드에서 실제로 성립함을 확인했다**. 클러스터 축도 랑데뷰 해싱(splitmix64, 원시
나머지 금지 준수), 유계 채널+FlushResult 검사(ClusterPeerSet), 블로킹 쿼리+인덱스 역행 리셋 등
레거시 회귀 방지가 충실하다.

그러나 알려진 **Consul `BuildView` 버그는 P0으로 확정**했다 — Consul 카탈로그의 비정상
데이터(범위 초과 노드 번호·중복 번호·중복 서비스 ID) 하나가 어느 catch에도 걸리지 않는 예외로
멤버십 루프를 **조용히 영구 정지**시키며, 특히 이름 중복은 Consul 서비스 ID가 에이전트-로컬
유일이라는 특성상 현실적으로 발생 가능하다. 그 외 Consul 기본 설정 모순(WaitTime 5분 >
HttpClient 기본 100초) 1건이 1.0 전 수정 대상이다.

## 발견 사항

### [P0] S-1. Consul `BuildView` — 미검증 카탈로그 데이터가 멤버십 갱신 루프를 영구 정지시킨다

- **위치**: `Server/ChServerM.Cluster.Consul/ConsulClusterMembership.cs:503`
  (`new NodeId(id)`, `new ClusterNode(...)`), `:507` (`new ClusterView(...)`) /
  전파 경로: `QueryAsync` :410-429 (catch 목록), `RunAsync` :250-257
- **현재 구현**: `BuildView`가 노드 번호를 `ushort.TryParse`로만 거르고(`:482`)
  `ClusterNode`/`ClusterView` 생성자에 그대로 넘긴다. 주석(`:506`)은 "중복 번호·이름은
  ClusterView가 판정한다"고 하는데, ClusterView의 판정은 **예외 던지기**다
  (`Server/ChServerM.Core/Cluster/ClusterView.cs:139-148`).
- **트리거 조건 4종 (코드에서 확정)**:
  1. **범위 초과**: 메타 노드 ID가 1024~65535면 `ushort.TryParse`는 통과하지만 `NodeId` 생성자가
     `ObjectId.MaxNodeId=1023`(10비트) 초과로 `ArgumentOutOfRangeException` (`SessionId.cs:132`).
  2. **노드 번호 중복**: 두 등록이 같은 메타 번호를 들면 `ClusterView.cs:139` → `ArgumentException`.
  3. **이름 중복**: 이름은 `service.Id ?? $"node-{id}"`인데 **Consul 서비스 ID는 에이전트-로컬
     유일**이라 서로 다른 노드가 같은 서비스 ID(예: `"chserverm"`)로 등록하는 것이 오히려 표준
     패턴이다 → `ClusterView.cs:144` → `ArgumentException`. **가장 현실적인 트리거.**
  4. **빈 서비스 ID**: `service.Id`가 `""`(null 아님)이면 `??` 폴백을 타지 않아 `ClusterNode`
     생성자가 던진다(`ClusterView.cs:54`).
- **전파**: `QueryAsync`는 `HttpRequestException`/`TaskCanceledException`/`JsonException`/`OCE`만
  잡고, `RunAsync`는 `OCE`만 잡는다. `ArgumentException` 계열은 둘 다 통과해 `_loop` 태스크가
  fault → **루프 사망, 로그 0줄**. 이후 `Current`는 마지막 뷰에 동결되고 `WaitForChangeAsync`
  대기자(=`WatchAsync` 소비자, 소유권 재검토, `ClusterPeerSet` 링크 정리)는 영원히 깨어나지
  않는다. 부수 피해로 `DisposeAsync`(`:214`)의 `WaitAsync`도 OCE/Timeout만 잡아 종료 시 원
  예외가 재부상. 기동 시(`CreateAsync`)에는 같은 데이터가 기동 실패(fail-fast라 상대적으로
  양호하나 메시지가 원인과 멀다).
- **수정 방안**:
  1. `BuildView`에서 `id > ObjectId.MaxNodeId` 명시 검사 → 기존 `LogMalformed` + `continue` 합류.
  2. `BuildView` 안에서 로컬 `HashSet`으로 번호·이름 중복 사전 감지 — 어느 쪽이 진짜인지 알 수
     없으므로 **충돌한 엔트리 모두 제외 + 경고**(자기 자신이 빠지면 `WarnIfSelfMissing`이 이미
     알린다). 빈 `service.Id`는 `string.IsNullOrWhiteSpace` 폴백으로.
  3. 심층 방어: `RunAsync`의 결과 처리 전체를 catch-all로 감싸 "마지막 구성 유지 + `RetryDelay`
     재시도" 경로로 합류(CLAUDE.md 9.2 예외 격리 — 항목 하나가 루프를 죽이지 않게).
  4. 테스트 추가 — `Tests/ChServerM.Cluster.Consul.Tests/ConsulClusterMembershipTests.cs`에 범위
     초과·중복 등록 시나리오가 **현재 전혀 없다**(테스트 10종 확인).
- **1.0 전 필수**: **필수** (가용성 정지 + 무로그, 2026-08-12 보안 검토 기록과 일치).
- **난이도**: 낮음

### [P1] S-2. Consul 기본 설정 자체 모순 — WaitTime(5분) > 소유 HttpClient 기본 Timeout(100초)

- **위치**: `ConsulClusterMembership.cs:136` (`new HttpClient()` — Timeout 미설정),
  `ConsulClusterMembershipOptions.cs:68` (WaitTime 기본 5분)
- **현재 구현**: `httpClient`를 안 넘기면 기본 `HttpClient`(Timeout 100초)를 만들면서 블로킹 쿼리
  `wait`는 기본 300초를 붙인다.
- **문제**: 유휴 클러스터에서 모든 블로킹 쿼리가 100초에 `TaskCanceledException`으로 잘려
  **"Consul 조회 실패" 경고가 ~101초마다 영구 반복**된다. 변화 감지 자체는 동작하지만 (1) 경고
  소음이 진짜 Consul 장애를 가리고, (2) 매 실패마다 `RetryDelay` 재시도로 불필요한 왕복이
  생긴다. Consul은 `wait`에 최대 1/16 지터를 더하므로 클라이언트 타임아웃은
  `wait × 1.0625 + 여유`가 권장.
- **대안**: 소유 클라이언트 생성 시 `http.Timeout = WaitTime * 1.0625 + 10초`. 주입된
  클라이언트는 `CreateAsync`에서 `http.Timeout <= WaitTime`이면 경고 또는 예외.
- **1.0 전 필수**: 필수(기본값 조합만으로 밟는 함정). / **난이도**: 낮음

### [P2] S-3. Redis — 매 호출 스크립트 전문 EVAL 전송 가능성

- **위치**: `RedisSessionStore.cs:191-199, 248-252, 271-274`
- **현재 구현**: `ScriptEvaluateAsync(문자열, ...)`에 스크립트 본문(쓰기 스크립트 ~380B)을 매번 넘긴다.
- **문제**: StackExchange.Redis의 문자열 오버로드는 EVALSHA 승격을 보장하지 않는다 — 보장된
  메커니즘은 `LuaScript.Prepare`/`IServer.ScriptLoad`(+`LoadedLuaScript`)다. 쓰기마다 스크립트
  전문이 네트워크를 타면 페이로드가 작은 세션(수십 B)에서는 스크립트가 본문보다 크다.
- **대안**: `LuaScript.Prepare`를 정적 필드로 캐시하거나 ScriptLoad+SHA(NOSCRIPT 폴백) 적용.
  적용 전 MONITOR/벤치로 현재 실제 전송 형태를 확인하고 `perf(...)` 커밋에 before/after.
- **1.0 전 필수**: 아님. / **난이도**: 낮음~중간

### [P2] S-4. Postgres — prepared statement 전략 부재 (Npgsql `Max Auto Prepare` 기본 꺼짐)

- **위치**: `PostgresSessionStore.cs:193, 231, 266, 291, 313` (모든 명령 경로)
- **현재 구현**: `NpgsqlDataSource` 사용은 모범적(구식 커넥션 관리 아님). 그러나 매 호출 새
  `NpgsqlCommand`를 만들고 `Prepare()`도 부르지 않아, 준비는 전적으로 연결 문자열의
  `Max Auto Prepare`에 달렸는데 **Npgsql 기본값은 0(비활성)**.
- **문제**: 동일 SQL 5종을 고빈도 반복하는 워크로드에서 parse/bind 비용이 매 호출 누적. 데이터
  원본 소유권이 호출자에 있어 어댑터가 강제할 수 없는 값이다.
- **대안**: `PostgresSessionStoreOptions` XML 문서와 CONSISTENCY/BENCHMARKS에
  `Max Auto Prepare=10` 권장 명시(문서 한 줄). 부수: `AddWithValue` 대신 타입 명시
  `NpgsqlParameter<T>`로 박싱·추론 제거, 쓰기 `state.ToArray()`(:235)와 읽기
  `(byte[])GetValue`(:210)의 복사 축소는 원격 왕복 565µs 대비 미미하므로 벤치와 함께.
- **1.0 전 필수**: 문서화는 권장, 코드 변경은 아님. / **난이도**: 낮음

### [P2] S-5. 적합성 스위트 공백 — 계약이 코드에는 있는데 테스트에는 없는 것

- **위치**: `Tests/ChServerM.Persistence.Conformance/SessionStoreConformanceTests.cs` (21종)
- **현재 커버리지**: 값 의미·CAS·ABA·만료·경합 1승자까지 잘 덮는다. 러너 멈칫
  방어(`SkipIfStallConsumedTheMargin`)도 정교하다.
- **빠진 것**:
  1. **대형 값**(수 MB) 왕복 — Redis/PG의 크기 절벽·프로토콜 한계 미검증
  2. **동시 생성**(N명이 `None`으로 생성) 정확히 1승자 — 현재 경합 테스트(:416)는 갱신만.
     Postgres의 `ON CONFLICT` 경로(:108-116)가 정확히 이 경합을 처리하는 코드인데 검증이 없다
  3. Renew vs Write 동시 경합(InMemory의 제자리 갱신 :270이 가장 미묘한 지점)
  4. 충돌로 거부된 쓰기가 **TTL을 건드리지 않는다**는 검증
  5. `TryRemoveAsync`/`TryRenewAsync`에 `expectedVersion=None` → `false` 계약 고정(세 구현이
     일치하지만 테스트가 없어 네 번째 어댑터에서 갈릴 수 있다)
  6. 만료된(존재했던) 키 읽기 시 destination 불변 — unknown 키 판만 있다
- **대안**: 위 6종 추가. 전부 기존 패턴 재사용으로 가능.
- **1.0 전 필수**: 2·5번은 권장(계약 고정 목적), 나머지 선택. / **난이도**: 낮음

### [P3] S-6. InMemory — 만료 청소가 전수 스캔

- **위치**: `InMemorySessionStore.cs:281-298`
- **판단**: 30초마다 전체 스냅샷 열거 + 항목별 원자 삭제. 저장소당 타이머 1개(9.5 준수).
  O(전체)이지만 주기가 길고 항목당 수십 ns라 수십만 세션까지 실질 부담 없음 — "측정 없는 최적화
  금지" 원칙상 현행이 합리적. 세션 수백만+짧은 주기가 필요해지면 만료 시각 버킷(계층 타이밍 휠)
  또는 우선순위 큐. 그 전에 항목 수 메트릭 노출이 먼저다.
- **1.0 전 필수**: 아님. / **난이도**: 중간(대안 구현 시)

### [P3] S-7. 기타 소소한 기록

- **Redis 쓰기당 소형 할당**: `VersionToBytes`가 8B 배열을 호출마다 할당
  (`RedisSessionStore.cs:288-293`, 쓰기당 2회) + `KeyFor`의 문자열. 원격 왕복 452µs 대비 무의미 — 기록만.
- **Redis 취소 토큰**: 진입 시에만 검사(SE.Redis가 in-flight 취소 미지원) — 계약 문서에 한 줄 가치.
- **Postgres 타이머**: `TimeProvider.System` 하드코딩(`PostgresSessionStore.cs:148`) — InMemory는
  주입 가능해서 비대칭. 만료 판정은 DB `now()`라 정당하나 청소 주기 테스트만 불편.
- **Consul 재시도 백오프**: 고정 1초(지수 아님). 로컬 에이전트 대상이라 수용 가능 — 기록만.
- **InMemory `TryRenewAsync`의 `while(true)`**: 모든 경로가 return이라 루프가 장식 — 코스메틱.

## 잘 된 부분

- **CONSISTENCY.md 주장 전수 검증 통과**: 단일 키 선형화(3구현 각각의 원자성 수단 확인), Renew
  버전 불변, 만료 즉시 판정(InMemory 모든 경로 `IsExpired`, PG 모든 SQL에 `Alive` 조건, Redis
  PX), PG 청소 `LIMIT @batch`, Redis 전 스크립트 `KEYS[1]` 단일 키(ADR-0058), 클러스터 모드 검증
  테스트 실존. **문서와 코드가 어긋나는 지점은 발견하지 못했다.**
- **직렬화 경계**: 바이트 계약(`IBufferWriter`/`ReadOnlyMemory`)이 직렬화 축과 완전히 분리,
  InMemory는 충돌 거부 경로 무복사(버전 검사 후 복사)까지 챙겼다.
- **StackExchange.Redis 사용 패턴**: 멀티플렉서 호출자 소유·미해제, fire-and-forget 없음,
  WATCH/MULTI 대신 Lua(멀티플렉서와의 궁합 근거까지 문서화) — 전부 권장 패턴.
- **클러스터**: 랑데뷰 해싱(splitmix64 전단사, 동점 결정적, 조회 무할당 — 검산 완료),
  `ClusterRouteResolver`의 뷰-라우터 짝 보장과 `WatchAsync` 세대 합침(무제한 큐 구조적 회피),
  `ClusterPeerSet`의 유계 채널+`TryWrite` false 검사+`FlushResult` 검사+대여 버퍼 `finally` 반납,
  `ConsulNodeIdLease`의 acquire 본문 파싱(상태 코드 함정 회피)과 세션 정리. Consul 인덱스 역행
  리셋(:279)과 index 0→1 보정(:456), long-polling 사용 확인.

**P0 하나(BuildView)와 P1 하나(HttpClient Timeout)만 1.0 전에 닫으면, 이 영역은 출시 품질이다.**
