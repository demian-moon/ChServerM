# 감사 04 — 전송 계층 + 전송 보안 (TCP · InMemory · HTTP · WebSocket · QUIC · TLS)

> 전수 감사 2026-08-18. 대상: `ChServerM.Transport.{Tcp,InMemory,Http,WebSocket,Quic}` ·
> `ChServerM.Security.Tls` 전 파일 정독. 우선순위: P0=정확성/1.0 필수 · P1=중요 · P2=권장 ·
> P3=선택. 인덱스: [00-summary.md](00-summary.md)

## 요약

전송 6종의 전반적 품질은 매우 높다 — TCP는 `ValueTask` 기반 `ReceiveAsync(Memory)` + 0바이트
수신(zero-byte read)으로 유휴 커넥션 버퍼를 절약하는 최신 패턴이고, 모든 전송이 유계 파이프·
백프레셔·3단 종료(Bind→Unbind→Stop)·`finally` 정리·커넥션당 타이머 0개 규약을 일관되게 지키며,
`async void`/`.Result`/`.Wait()`/무제한 큐는 전 파일에서 발견되지 않았다. QUIC은 net9+ 정식화
API(`QuicListener.ListenAsync` + `ConnectionOptionsCallback`)를, TLS는
`AuthenticateAsServerAsync(SslServerAuthenticationOptions)` + TLS 1.3 기본 + 세대 보관식 인증서
회전을 올바르게 쓴다.

그러나 **서버 TLS 핸드셰이크에 타임아웃이 없어**(hosting이 `ConnectionClosed` 토큰만 전달,
IdleTimeout 기본 꺼짐) 핸드셰이크를 완료하지 않는 클라이언트가 자원을 무기한 점유할 수 있는
DoS 표면이 1.0 전 필수 수정 항목이다. 그 외에는 `SslStreamCertificateContext` 미사용,
HTTP/WS/QUIC의 수용 제어·거부 메트릭 비대칭, WebSocket Origin 미검증, 수락 골격 6벌
복제(Template Method 미적용)가 주요 개선 대상이다.

## 발견 사항

### [P1] T-1. 서버 TLS 핸드셰이크 타임아웃·폭주 방어 부재 — **1.0 전 필수**

- **위치**: `Server/ChServerM.Hosting/SecuredConnectionHandler.cs:48-50`,
  `Server/ChServerM.Security.Tls/TlsTransportSecurity.cs:63-102`
- **현재 구현**: `SecureAsServerAsync(..., connection.ConnectionClosed)` — 핸드셰이크에 전달되는
  토큰이 커넥션 종료 토큰뿐이다. Core `ITransportSecurity.cs:51` 주석이 "핸드셰이크
  타임아웃(Phase 10)도 이 토큰으로 합류한다"고 예고했지만 **아직 합류하지 않았다**. 버전 협상
  핸드셰이크에는 5초 타임아웃이 있으나 그것은 TLS **이후** 단계다.
- **문제**: TCP 연결 후 ClientHello를 보내지 않거나 찔끔찔끔 보내는 클라이언트(slowloris)가
  `SslStream` 핸드셰이크 대기 상태로 커넥션 슬롯·메모리를 무기한 점유한다.
  `TcpTransportOptions.IdleTimeout`이 기본 비활성(Zero)이라 기본 조립에서는 회수 경로가 전혀
  없다. Kestrel조차 기본 10초 TLS 핸드셰이크 타임아웃을 둔다. AdmissionControl(속도 제어)은
  수립 후 점유 공격이라 막지 못한다.
- **대안**: `TlsSecurityOptions.HandshakeTimeout`(기본 10초 내외, 끌 수 없음 —
  `VersionNegotiationOptions.HandshakeTimeout`과 같은 규율)을 추가하고 `SecureAsServerAsync`
  내부에서 `CreateLinkedTokenSource(cancellationToken)` + `CancelAfter`로 상한을 건다. 또는
  `SecuredConnectionHandler`에서 링크하면 어댑터 전체에 일괄 적용된다.
- **1.0 전 필수**: **필수** — DoS 표면이며, 옵션 추가는 공개 API 변경(승인 파일 갱신)이라 1.0 전이 적기.
- **난이도**: 낮음

### [P2] T-2. `StopAsync` 1차 드레인 대기가 기본 토큰에서 무한 — 전 전송 공통

- **위치**: `TcpServerTransport.cs:273`, `InMemoryServerTransport.cs:164`,
  `HttpServerTransport.cs:219`, `WebSocketServerTransport.cs:187`, `QuicServerTransport.cs:389`
- **현재 구현**: `await Task.WhenAll(pending).WaitAsync(cancellationToken)` — 토큰이 취소돼야
  Abort + `ShutdownTimeout` 상한 경로로 넘어간다.
- **문제**: `StopAsync()`를 기본 인자(`CancellationToken.None`)로 부르면 1차 대기에 상한이 없다.
  상시 연결 워크로드에서는 종료가 영원히 끝나지 않는다 — 자체 규약("상한 없는 대기는 종료를
  영원히 막는다", CLAUDE.md 9.5/9.6)과 충돌. 호스팅이 항상 타임아웃 토큰을 넘긴다는 암묵
  전제가 계약(XML 문서)에 명시돼 있지 않다.
- **대안**: 토큰이 `CanBeCanceled == false`이면 `ShutdownTimeout`을 1차 대기에도 적용하거나,
  최소한 `IServerTransport.StopAsync` 계약 문서에 명시. 전자가 프레임워크 철학에 부합.
- **1.0 전 필수**: 권장(동작 변경이므로 1.0 전이 적기). / **난이도**: 낮음

### [P2] T-3. `SslStreamCertificateContext` 미사용 — 핸드셰이크당 체인 구축 비용 (TLS + QUIC)

- **위치**: `TlsTransportSecurity.cs:79-85`, `QuicServerTransport.cs:149-153`
- **현재 구현**: `SslServerAuthenticationOptions.ServerCertificate = certificate` — 원시
  `X509Certificate2`를 매 핸드셰이크에 전달.
- **문제**: 이 경로는 핸드셰이크마다 내부적으로 인증서 체인을 재구축한다(OS 인증서 저장소 조회
  포함 — 수 ms 급 비용이 접속 폭주 시 증폭). 중간 인증서 체인 전송도 컨텍스트 없이는 불완전할
  수 있다. Kestrel은 이 때문에 `ServerCertificateContext`를 쓴다.
- **대안**: 인증서 적재·회전 시점(`FileCertificateSource` 세대 교체 시)에
  `SslStreamCertificateContext.Create(cert, additionalCerts)`를 1회 만들어 보관하고
  `ServerCertificateContext`로 전달. `IServerCertificateSource`가 컨텍스트를 돌려주도록 확장하면
  TLS·QUIC 양쪽에서 재사용된다.
- **1.0 전 필수**: 권장 (`IServerCertificateSource` 반환 타입 변경이면 파괴적 — 1.0 전이 적기).
- **난이도**: 중간

### [P2] T-4. QUIC 서버 인증서 회전 불가 — `IServerCertificateSource` 미통합

- **위치**: `QuicServerTransport.cs:141-154`, `QuicTransportOptions.cs:48`
- **현재 구현**: `QuicTransportOptions.ServerCertificate` 고정 인스턴스만 받는다.
  `ConnectionOptionsCallback`은 연결마다 호출되므로 회전을 반영할 **정확한 자리**인데, 고정
  인증서를 캡처해 반환한다.
- **문제**: Let's Encrypt류(90일) 인증서를 쓰면 QUIC 전송만 재시작 없이는 갱신을 못 집는다.
  TLS 어댑터(`FileCertificateSource`)는 회전을 지원하므로 축 간 능력 비대칭.
- **대안**: `QuicTransportOptions`에 `ServerCertificateSource` 추가(어셈블리 의존을 피하려면
  `Func<X509Certificate2>`로도 충분) 후 `ConnectionOptionsCallback` 안에서 해석.
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P2] T-5. HTTP/WS/QUIC에 AdmissionControl·거부 메트릭 부재 — 전송 축 비대칭

- **위치**: `HttpTransportOptions.cs`·`WebSocketTransportOptions.cs`·`QuicTransportOptions.cs`
  (해당 속성 없음); 거부 시 로그만 남기는 `HttpServerTransport.cs:403-416`,
  `WebSocketServerTransport.cs:373-386`, `QuicServerTransport.cs:318-341`
- **현재 구현**: TCP·인메모리는 `IAdmissionControl`(T-16 방어) +
  `IMetricsSink.Count(ConnectionsRejected)`를 갖추었지만, HTTP·WebSocket·QUIC 3종은 정적 상한만
  있고 거부는 **로그로만** 관측된다.
- **문제**: (1) CLAUDE.md 9.6 "드롭 수를 메트릭으로 노출한다" 위반. (2) 재접속 스톰 방어가 전송
  선택에 따라 있다가 없다가 한다 — 조립 가능성 기준이 관측·방어 축에서 깨진다.
- **대안**: 세 옵션 클래스에 `AdmissionControl`/`MetricsSink` 속성을 추가하고 거부 경로에서
  TCP와 동일하게 호출(수락 진입점이 이미 한 곳씩이라 기계적 작업).
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P2] T-6. WebSocket 핸드셰이크 — Origin 미검증(CSWSH) · 버전 검사 허술 · 서브프로토콜 미협상

- **위치**: `WebSocketServerTransport.cs:252-264`
- **현재 구현**: 직접 구현한 RFC 6455 핸드셰이크가 `Sec-WebSocket-Key` 존재와
  `SecWebSocketVersion.ToString().Contains("13")`만 검사. `Origin` 헤더는 보지 않고,
  `Sec-WebSocket-Protocol` 요청은 무시(에코 없음).
- **문제**: (1) 이 전송의 존재 이유가 "브라우저·프록시 통과"인데 Origin 검증이 없으면 임의
  웹사이트의 JS가 사용자 브라우저에서 이 서버로 WebSocket을 열 수 있다(Cross-Site WebSocket
  Hijacking) — 표준 심층 방어 누락. (2) `Contains("13")`은 "130" 같은 값도 통과. (3) 서브프로토콜
  요청 클라이언트는 응답에 에코가 없으면 규격상 연결을 실패시켜야 한다 — 조용한 비호환.
- **대안**: `WebSocketTransportOptions.AllowedOrigins`(null = 검사 안 함, 명시 시 화이트리스트)
  추가, 버전은 정확 비교, 서브프로토콜은 최소한 문서화.
- **1.0 전 필수**: Origin 옵션은 권장(브라우저 배포를 실제 지원할 거면 필수에 가깝다).
- **난이도**: 낮음

### [P2] T-7. 수락 루프 골격 6벌 복제 — Template Method 미적용

- **위치**: `TcpServerTransport.cs:255-300·464-499`, `InMemoryServerTransport.cs:146-189·270-306`,
  `HttpServerTransport.cs:204-263·281-397`, `WebSocketServerTransport.cs:173-226·241-367`,
  `QuicServerTransport.cs:266-315·374-443`
- **현재 구현**: ①StopAsync 드레인 블록, ②"등록→핸들러→`finally` 제거·정리·completion" 골격,
  ③`NextConnectionId`, ④거부 로그/메트릭, ⑤`ActiveConnection` record, ⑥커넥션 어댑터의
  `_closedFlag/_completed/SignalClosed` 패턴이 거의 동일한 코드로 5~6곳에 존재. 주석마다
  "TCP·인메모리와 같은 골격"이라고 스스로 표시.
- **문제**: CLAUDE.md 4장이 Template Method를 "전송 구현체가 공유하는 커넥션 수락 루프 골격"에
  명시 지정했는데 미적용. 이미 미세한 표류가 보인다(로그 문구 차이, TCP만
  `IHealthCheck/IDiagnosticsSource` 구현, HTTP/WS만 상태 3값). H1 부류 결함을 5곳에 각각 고친
  이력 자체가 복제 비용의 증거 — 다음 결함도 5번 고치게 된다.
- **대안**: Core를 오염시키지 않는 내부 공유 계층(internal-shared 소스 패키지 또는 링크드
  파일)으로 ①②③④를 추출. 커넥션 어댑터 쪽은 WS·QUIC가 사실상 동일(스트림+파이프2+펌프2)이라
  우선 통합 후보.
- **1.0 전 필수**: 선택(내부 구조). / **난이도**: 중간

### [P2] T-8. `QuicClientTransport` — 단일 락이 전 종단의 연결 수립을 직렬화

- **위치**: `QuicClientTransport.cs:38-39·99-137`
- **현재 구현**: `SemaphoreSlim(1,1)` 하나를 잡은 채 `QuicConnection.ConnectAsync`(네트워크 왕복 +
  TLS 핸드셰이크)를 수행. `Dictionary<EndPoint, QuicConnection>` 전체가 이 락 뒤에 있다.
- **문제**: 서로 다른 종단 A·B로의 동시 연결에서 A의 느린 핸드셰이크가 B의 캐시 히트 경로까지
  막는다(head-of-line blocking). 클러스터 클라이언트에서 콜드스타트가 직렬화. 9.1 위반.
- **대안**: `ConcurrentDictionary<EndPoint, Lazy<Task<QuicConnection>>>`(종단별 게이트)로 종단
  단위 직렬화만 남긴다 — 캐시 히트는 무락, 동일 종단 중복 수립만 병합.
- **1.0 전 필수**: 권장. / **난이도**: 낮음

### [P3] T-9. ConnectionId 32비트 랩 어라운드 — 세대(generation) 필드가 항상 1

- **위치**: `TcpServerTransport.cs:501-502`(및 전 전송 동일 패턴 — `HttpServerTransport.cs:399`,
  `WebSocketServerTransport.cs:369`, `QuicServerTransport.cs:454`, 클라이언트 전송들)
- **현재 구현**: `new((uint)Interlocked.Increment(ref _nextSlot), generation: 1)` — 슬롯은 2^32에서
  순환하고 세대는 상수 1.
- **문제**: 초당 1.6k 커넥션이면 약 한 달에 랩한다. 랩 시점에 같은 ID의 장수 커넥션이 살아
  있으면 `_connections[id] = ...`가 기존 항목을 덮어쓰고, 이후 **구 커넥션 핸들러의 `finally`가
  신 커넥션의 항목을 제거**해 신 커넥션이 추적(드레인·idle 스윕·진단)에서 빠진다. ID 타입에
  세대 필드가 이미 있는데 활용하지 않는 점이 아깝다.
- **대안**: 랩 횟수를 세대로 반영(64비트 카운터 분할: `generation = (counter >> 32) + 1`)하거나
  최소 `TryAdd` 실패 시 재발급.
- **1.0 전 필수**: 선택(상업용 장기 가동 프레임워크라면 권장으로 승격 고려). / **난이도**: 낮음

### [P3] T-10. QUIC 연결 수락 루프 — 지속 실패 시 백오프 없음

- **위치**: `QuicServerTransport.cs:186-195`
- **문제**: TCP 수락 루프는 FD 고갈에 100ms 백오프를 두었는데(2026-08-04 감사 반영), QUIC은
  지속적 실패 상태에서 즉시 재시도 루프가 코어를 태울 수 있다. TCP와 달리 수락 루프 고장이
  `IHealthCheck`로 드러나지 않는다(TCP만 `_acceptFault` 장치 보유).
- **대안**: 연속 실패 카운터 + 짧은 백오프, TCP와 동일한 `_acceptFault`/`CheckAsync` 노출.
- **1.0 전 필수**: 선택. / **난이도**: 낮음

### [P3] T-11. WS·QUIC 커넥션 — 생성자에서 `Task.Run`으로 펌프 시작 (this-escape)

- **위치**: `WebSocketDuplexConnection.cs:90-92`, `QuicStreamConnection.cs:86-87`
- **문제**: `SocketConnection.cs:145-154`는 정확히 이 이유로 `Start()`를 분리했다. 필드 대입이
  모두 끝난 뒤라 실해는 없지만, 같은 코드베이스에서 규약이 갈려 있어 나중에 생성자에 로직이
  추가되면 잠복 결함이 된다.
- **대안**: `Start()` 패턴으로 통일. / **1.0 전 필수**: 선택. / **난이도**: 낮음

### [P3] T-12. HTTP/WS/QUIC 클라이언트 전송이 `ITransportBufferLimits` 미구현 + 커넥션 미추적

- **위치**: `HttpClientTransport.cs:36`, `WebSocketClientTransport.cs:26`, `QuicClientTransport.cs:35`
- **문제**: ADR-0007 조립 검사("최대 프레임 > 버퍼" 교착 방지)가 클라이언트 방향에서 이 3종만
  사각. HTTP는 커넥션 추적 없이 `_invoker.Dispose()`만 한다(활성 스트림이 강제 절단으로 관측될
  수 있음).
- **대안**: 옵션의 `PauseWriterThreshold`(HTTP는 `StreamReceiveWindowSize`)를
  `ITransportBufferLimits`로 노출. HTTP 클라이언트는 TCP식 커넥션 추적 또는 최소한 문서화.
- **1.0 전 필수**: 선택(인터페이스 추가는 비파괴적). / **난이도**: 낮음

### [P3] T-13. WS·QUIC 수신 펌프 — 유휴 커넥션 버퍼 절약(0바이트 read) 미적용

- **위치**: `WebSocketDuplexConnection.cs:133-135`, `QuicStreamConnection.cs:128-129`
- **문제**: `writer.GetMemory(4096)` 후 수신 대기 — 유휴 커넥션도 커넥션당 최소 4KB 파이프
  블록을 상시 점유. TCP는 `WaitForDataBeforeAllocating`으로 해결했다(1만 유휴 × 4KB = 40MB).
  .NET 8+에서 관리형 WebSocket은 0바이트 `ReceiveAsync`를 지원한다. QuicStream은 Stream
  계약이라 0바이트 read 의미가 보장되지 않으므로 별도 검증 필요.
- **대안**: WS 수신 펌프에 TCP와 동일한 `_waitForData` 패턴 적용(옵션화) + 벤치마크 확인.
- **1.0 전 필수**: 선택. / **난이도**: 낮음~중간

### [P3] T-14. TCP keep-alive 간격 — 이식 가능한 표준 옵션이 이미 존재

- **위치**: `TcpTransportOptions.cs:70-83·321-325`
- **문제**: `EnableKeepAlive`는 on/off만 노출하고 간격 제어는 "레거시의 `IOControlCode`가
  Windows 전용이었다"는 근거로 배제했는데, 근거가 낡았다 — .NET 5+의
  `SocketOptionName.TcpKeepAliveTime/TcpKeepAliveInterval/TcpKeepAliveRetryCount`는
  Windows·Linux·macOS 공통 표준 옵션이다.
- **대안**: 주석 갱신 또는 `KeepAliveTime/Interval/RetryCount` 옵션 3종 추가.
- **1.0 전 필수**: 선택. / **난이도**: 낮음

### [P3] T-15. TCP Bind/Unbind 동시 호출의 미세 레이스

- **위치**: `TcpServerTransport.cs:165-186·243-248`
- **문제**: Bind 진행 중(소켓 대입 전)에 다른 스레드가 Unbind를 부르면 "언바인드됐지만 수락
  중"인 상태가 될 수 있다. 정상 조립(호스팅 단일 스레드 생명주기)에서는 도달 불가 — 실위험 낮음.
- **대안**: 계약 문서에 "Bind 완료 전 Unbind 금지" 명시 또는 상태 전이를 단일 필드 상태
  머신(HTTP/WS의 `_state` 3값 방식)으로 통일.
- **1.0 전 필수**: 선택. / **난이도**: 낮음

## 잘 된 부분

- **TCP 소켓 경로가 교과서적**: `NetworkStream` 배제 + `Memory<T>` 오버로드 직결, 0바이트 수신
  옵션(기본 켬), 취소는 토큰이 아니라 `Socket.Dispose()`(Kestrel과 동일 — 플랫폼별 취소 불확실성
  회피), 부분 전송 처리(`SendAllAsync`), 벡터드 송신은 **벤치마크로 기본 끔**(측정 없는 최적화
  금지 준수). `SocketAsyncEventArgs` 수동 재사용이 없는 것은 결함이 아니다 — `ValueTask` 소켓
  API가 내부적으로 `IValueTaskSource`를 캐시해 상각 무할당이며, 현 시점 권장 패턴.
- **Pipelines 통합**: pause/resume 임계값이 전 전송에서 옵션으로 노출되고
  `ITransportBufferLimits`로 조립 시점 교착 검사(ADR-0007)에 연결. `useSynchronizationContext:
  false` 고정, `PipeScheduler.Inline` 미사용 — 위험 요소 없음.
- **수락 루프 예외 분류**(TCP)가 모범적: 일시 오류 즉시 재시도 / FD 고갈 백오프 / 치명 오류는
  `_acceptFault` → `IHealthCheck` 노출 — "조용히 수용을 멈춘 서버" 문제를 정면으로 해결.
- **3단 종료·이중 종료 레이스**가 5개 전송에서 일관되고, 과거 감사(H1·H2·H3)의 재발 방지가
  주석으로 각인. `ConnectionClosed` 전파(HTTP는 Kestrel `RequestAborted` 링크)도 올바르다.
- **TLS**: 자체 암호 0, TLS 1.3 단독 기본, 최신 API, 파일 기반 회전(폴링 — `FileSystemWatcher`의
  k8s 함정 회피)과 직전 세대 보관(use-after-dispose 방지), Windows PEM/Schannel 함정 흡수.
- **동시성 규약**: 커넥션당 타이머 0(전송당 idle 스윕 1개), 무제한 큐 0, `async void` 0,
  `.Result`/`.Wait()` 0, 락-프리 상태 복원은 전부 `finally`(9.2 준수).
