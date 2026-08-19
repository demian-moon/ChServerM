# FlatGameRoom 샘플 — 로그인·세션·FlatBuffers 실데이터 총망라

> 위치: [`Samples/ChServerM.Samples.FlatGameRoom`](https://github.com/demian-moon/ChServerM/tree/main/Samples/ChServerM.Samples.FlatGameRoom)
>
> **한 조립에 프레임워크의 보안·세션·직렬화 축을 전부 얹은 참조 예제**다.
> GameRoom 샘플(룸 축 최소 예제)에 인증·상태 필터·세션(재개 토큰) 축을 더하고,
> 페이로드를 전부 FlatBuffers(FlatSharp) 테이블로 바꿨다. "실서비스 게임 서버라면
> 이 순서로 조립한다"를 보여주는 것이 목적이다.

## 무엇을 실증하는가

| # | 실증 항목 | 코드 위치 |
|---|---|---|
| 1 | **로그인(인증 미들웨어)** — `IAuthenticator` 검증, 실패 = 커넥션 종료(사유 비공개, T-20) | `DemoAuthenticator.cs` |
| 2 | **상태 필터(기본 거부, T-19)** — 로그인 전에는 Login·세션 재개만 통과 | `Program.cs` `BuildServer` |
| 3 | **세션 수립 + 재개 토큰 회전** — 재접속한 클라이언트가 재로그인 없이 상태 복원 | `SessionResumeStateBridge.cs` |
| 4 | **FlatBuffers 실데이터 왕복** — 요청·응답·브로드캐스트 전부 타입 있는 테이블 | `Schemas/flat_game_room.fbs` |
| 5 | **룸 브로드캐스트(1회 인코딩)** — 채팅·이동을 같은 룸 멤버에게 배달 | `FlatGameRoomService.cs` |
| 6 | **소스 생성 디스패치** — `[MessageHandler]` + `MapGeneratedHandlers`, 직렬화 제공자만 교체 | `Handlers.cs` |

같은 핸들러 작성 방식이 EchoServer(MemoryPack)·StatelessWeb(Protobuf)과 동일하다 —
**직렬화 제공자 한 줄만 갈아 끼우면 직렬화 축이 바뀐다**(ADR-0012/0014). 이것이
"축을 골라 조립한다"는 프레임워크 명제의 세 번째 실증이다.

## 축 조합 (realtime-stateful 프로필)

| 축 | 선택 | 이유 |
|---|---|---|
| 전송 | `TcpServerTransport` | 상시 연결 게임 프로필 |
| 프레이밍 | 고정 16B 헤더 | 플래그·시퀀스·버전 필드 필요(브로드캐스트/세션) |
| 직렬화 | **FlatSharp (Greedy)** | 이 샘플의 주인공. Lazy 는 어댑터가 조립 시점 거부 — `IMessageSerializer` 계약("호출이 끝나면 페이로드는 무효")과 충돌하기 때문 |
| 실행 모델 | `PartitionedExecutionModel` | 룸 축의 전제 — 커넥션 파티션의 배타 슬롯이 브로드캐스트 쓰기 소유권의 근거(ADR-0064) |
| 세션 | `UseSessions` + `InMemorySessionStore` | 재개 토큰(32B, 1회용 회전)·TTL 30분 |
| 룸 | `ChServerM.RealTime.Rooms` | 선택 축 — 룸 디렉터리·1회 인코딩 브로드캐스터 |

## 메시지 프로토콜

앱 대역(1~40000) ID 를 쓴다. 세션 핸드셰이크(40007 재개 요청 / 40008 재개 응답 /
40009 수립 통지)는 프레임워크 예약 대역이며 와이어 형식은 `SessionHandshakeCodec` 이
영구 동결한다 — 앱이 정의하지 않는다.

| ID | 메시지 | 방향 | 페이로드(FlatBuffers) |
|---|---|---|---|
| 1 | Login | C→S | `LoginRequest { displayName, clientToken }` → 응답 `LoginReply { result, playerId, motd }` |
| 2 | JoinRoom | C→S | `JoinRoomRequest { roomId }` → `JoinRoomReply { result, memberCount }` |
| 3 | ChatSend | C→S | `ChatSend { text }` |
| 4 | ChatBroadcast | S→C | `ChatBroadcast { senderName, text, sentAtUnixMs }` |
| 5 | MoveUpdate | C→S | `MoveUpdate { x, y, heading }` |
| 6 | MoveBroadcast | S→C | `MoveBroadcast { playerId, x, y, heading }` |
| 7 | LeaveRoom | C→S | `LeaveRoomRequest { notifyOthers }` → `LeaveRoomReply { result }` |

**요청과 브로드캐스트의 ID 를 나누는 이유** — 테이블이 다르기 때문이다. 요청에는
발신자·시각이 없다: `senderName`·`sentAtUnixMs`·`playerId` 는 **서버가 채운다**
(클라이언트 시계·신원 주장을 믿지 않는다). 브로드캐스트 프레임의 시퀀스는 항상 0 —
헤더를 N 명이 공유하므로 커넥션별 일련번호를 실을 수 없다(ADR-0064).

## 와이어 흐름

### 1. 접속 → 로그인 → 세션 수립

```text
클라이언트                                 서버
   │ ── [1] LoginRequest ────────────────▶ │  상태 필터: Connected 에서 Login 허용
   │                                       │  AuthenticationMiddleware → DemoAuthenticator 검증
   │                                       │    실패 = 커넥션 종료(ErrorCode 6000, 사유 비공개)
   │                                       │    성공 = 상태 Connected → LoggedIn 전이
   │                                       │  LoginHandler: 세션 생성(TryCreateAsync)
   │ ◀─ [40009] 세션 수립 통지 ─────────── │    세션 번호 + 최초 재개 토큰(32B)
   │ ◀─ [1] LoginReply ─────────────────── │    playerId·motd
```

- 로그인 **전**에 JoinRoom(2)을 보내면 상태 필터의 기본 거부가 커넥션을 닫는다 —
  응답 프레임조차 없다(정보 최소화). 자체 검증의 첫 시나리오가 이 음성 경로다.
- `LoginResult` 는 값이 `Ok` 하나뿐이다 — 자격 불일치는 와이어에 실리지 않는다
  (계정 열거 방지, T-20). enum 을 유지하는 이유는 스키마 진화 자리 확보다.

### 2. 룸 입장 → 채팅·이동 브로드캐스트

```text
A ── [3] ChatSend{"안녕"} ──▶ 서버 ── [4] ChatBroadcast{A, "안녕", 서버시각} ──▶ B (발신자 제외)
A ── [5] MoveUpdate{x,y,h} ─▶ 서버 ─ 좌표 범위 검증 ─ [6] MoveBroadcast{A.playerId,…} ─▶ B
```

브로드캐스트는 룸 축의 1회 인코딩 계약을 따른다 — 페이로드를 멤버 수만큼 다시
직렬화하지 않고, 인코딩된 프레임 하나를 참조 계수로 공유해 각 멤버의 파티션에
배달한다. 배달 큐가 유계이므로 느린 수신자는 **거부로 관측**된다(붕괴 대신 거부).

### 3. 재접속 → 세션 재개 (이 샘플의 핵심 시나리오)

```text
A(구 커넥션 끊김) — 룸에서 자동 퇴장(세 갈래 퇴장 경로의 하나)
A(새 커넥션)                               서버
   │ ── [40007] 재개 요청(세션번호+토큰) ─▶ │  UseSessions 가 자동 처리: 토큰 대조 → 회전
   │ ◀─ [40008] 재개 응답(새 토큰) ──────── │  SessionResumeStateBridge(앱 미들웨어):
   │                                       │    세션 상태에서 playerId·이름 복원
   │                                       │    상태 Connected → LoggedIn 전이 (재로그인 불필요)
   │ ── [2] JoinRoomRequest ─────────────▶ │  바로 룸 재입장 가능
```

**역할 경계(ADR-0036)** — 이 샘플이 가르치는 가장 중요한 부분:

| 단계 | 누가 하는가 |
|---|---|
| 세션 **수립**(언제 만들지, 무엇을 담을지) | **앱** — `LoginHandler` 가 `TryCreateAsync` + 40009 통지 |
| 재개 요청 처리(토큰 대조·회전·40008 응답·`ISessionFeature` 바인딩) | **프레임워크** — `UseSessions()` 자동 배선 |
| 재개 **후** 앱 상태 복원(신원·커넥션 상태 전이) | **앱** — `SessionResumeStateBridge` 미들웨어 |

- 재개 토큰은 **1회용**이다: 재개마다 회전되고, 옛 토큰 재사용은 거부된다(자체
  검증이 실증). 탈취 토큰의 재사용 창을 좁히는 표준 설계다.
- 재개 후 B 가 받는 퇴장 통지의 발신자 이름은 **세션 상태에서 복원된 값**이다 —
  재개가 신원까지 복원했음을 이것으로 검증한다.

## ⚠ 조립 함정 2개 (이 샘플이 주석으로 경고하는 것)

1. **상태 필터 + 세션 재개(감사 2026-08-18 H-7)** — `UseSessions()` 가 40007 라우팅을
   자동 배선해도, `MessageStateFilterMiddleware` 의 화이트리스트에
   `FrameworkMessageIds.SessionResume` 를 **직접 Allow 하지 않으면** 필터의 기본
   거부가 먼저 걸려 재개가 영영 커넥션 종료가 된다. `BuildServer` 의 Allow 목록 참조.
2. **FlatSharp 는 Greedy 만** — `(fs_serializer:"Greedy")` 를 스키마 테이블마다
   지정한다. Lazy/Progressive 는 반환 객체가 페이로드 버퍼를 계속 참조하므로
   `FlatSharpMessageSerializer` 생성자가 조립 시점에 예외로 거부한다(ADR-0012).

미들웨어 순서는 **상태 필터 → 인증 → 재개 브리지**다. 필터·인증의 역순은
`Build()` 가 조립 시점 예외로 거부한다(모르는 ID 로 인증을 우회하는 구멍 방지).

## 실행 방법

```bash
# 자체 검증(기본): 서버+클라이언트 2개를 루프백 TCP 로 띄워 시나리오 24개를 검증하고 종료
dotnet run --project Samples/ChServerM.Samples.FlatGameRoom -c Release

# 상시 서버
dotnet run --project Samples/ChServerM.Samples.FlatGameRoom -c Release -- --serve 5000
```

자체 검증 시나리오: 로그인 전 Join 거부(음성) → A·B 로그인(MOTD 왕복, 세션 번호+토큰
수신) → 룸 100 입장(인원 1→2, 중복 거부) → 채팅 브로드캐스트(발신자·본문·서버 시각) →
이동 float 무손실 왕복 → A 재접속+재개(토큰 회전, 옛 토큰 거부, 재로그인 없이 재입장) →
퇴장 통지(복원된 이름) → 멱등 퇴장 → 잉여 프레임 0·배달 거부 0.

> 빌드 참고: FlatSharp.Compiler 는 net9 도구라 .NET 10 단독 환경에서는
> `DOTNET_ROLL_FORWARD=LatestMajor` 가 필요하다(`eng/build.ps1` 이 설정한다).
> 이 샘플도 다른 샘플과 같이 `PublishAot=true` 로 Native AOT 게이트에 포함된다.

## 파일 안내

| 파일 | 역할 |
|---|---|
| `Schemas/flat_game_room.fbs` | FlatBuffers 스키마 — 테이블 10종 + 결과 enum 3종. 각 필드에 "왜"가 주석으로 있다 |
| `FlatGameRoomProtocol.cs` | 메시지 ID(1~7) + 커넥션 상태 비트(`Connected`/`LoggedIn`) |
| `DemoAuthenticator.cs` | `IAuthenticator` 데모 구현(표시 이름 + 공유 비밀) + `PlayerFeature`(커넥션 신원). **데모다** — 실서비스는 플랫폼 티켓/OAuth 검증이 이 자리에 온다 |
| `SessionResumeStateBridge.cs` | 재개 성공 → 앱 상태 복원을 잇는 커스텀 미들웨어. 미들웨어 작성 예제로도 읽을 수 있다 |
| `FlatGameRoomService.cs` | 도메인 로직 — 세션 수립, 룸 입/퇴장(세 갈래 퇴장 합류), 1회 인코딩 브로드캐스트 |
| `Handlers.cs` | `[MessageHandler]` 타입 있는 핸들러 5종 — 손으로 쓴 Map 등록이 없다 |
| `Program.cs` | 조립(`BuildServer`) + 자체 검증/`--serve` + 테스트 클라이언트(유계 채널 수신함) |

## 다른 샘플과의 관계

| 샘플 | 조합 | 이 샘플과의 차이 |
|---|---|---|
| EchoServer | TCP + MemoryPack + 파티션 | 최소 왕복. 인증·세션 없음 |
| StatelessWeb | HTTP/2 + Protobuf + 병렬 | 무상태 프로필 — 세션 외부화의 반대편 극단 |
| GameRoom | TCP + 원시 바이트 + 룸 | 룸 축 계약(1회 인코딩·격리·퇴장 합류)의 최소 실증 |
| **FlatGameRoom** | TCP + **FlatSharp** + 룸 + **인증·상태 필터·세션** | 전부 얹은 종합판 — 실서비스 조립의 출발점 |
