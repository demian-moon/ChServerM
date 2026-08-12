# 시작 가이드 — 5분 안에 에코 서버 띄우기

ChServerM 은 완성된 서버가 아니라 **축을 골라 조립하는 프레임워크**다.
이 문서는 가장 빠른 경로 하나만 보여준다: 동봉 샘플 실행(1분) → 최소 서버 직접 조립(4분).
축의 전체 목록과 설계 원리는 [ARCHITECTURE.md](ARCHITECTURE.md)로 미룬다.

## 0. 준비물

- **.NET SDK 10.0** — 2절(패키지로 조립)은 10.x 아무 버전이면 된다.
  1절(동봉 샘플)은 저장소 빌드라 `global.json` 이 고정한 10.0.2xx 를 따른다.
- 저장소 클론은 **1절(동봉 샘플)에만** 필요하다 — 2절부터는 nuget.org 패키지로 조립한다.

## 1. 동봉 샘플 실행 (1분)

```bash
# 전체 빌드 (최초 1회)
dotnet build ChServerM.slnx -c Release

# 에코 샘플 — 인자 없이 실행하면 자체 검증을 돌고 exit code 로 보고한다
dotnet run --project Samples/ChServerM.Samples.EchoServer -c Release

# 서버로 계속 띄우기
dotnet run --project Samples/ChServerM.Samples.EchoServer -c Release -- --serve 5000
```

자체 검증이 "같은 핸들러 코드를 TCP 와 인메모리 전송 양쪽에서" 돌려 통과를 보고하면
환경 구성이 끝난 것이다.

## 2. 최소 서버를 직접 조립하기 (4분)

### 2.1 프로젝트 만들기

```bash
dotnet new console -n MyServer
dotnet add MyServer package ChServerM
```

메타 패키지 `ChServerM` 하나가 이 가이드가 쓰는 축 전부(+ 인메모리 루프백 전송,
MemoryPack 직렬화, 소스 제너레이터, 사용 규약 분석기 CHSM3xxx)를 가져온다.
다른 축이 필요하면 `ChServerM.*` 개별 패키지를 추가·교체한다
([축 선택 가이드](GUIDE-CHOOSING-AXES.md)).

| 이 예제가 쓰는 축 | 축 | 선택 |
|---|---|---|
| `Hosting` | 조립 표면 | `ServerBuilder` — 프레임워크의 정면 출입구 |
| `Framing` | 프레이밍 | 고정 헤더 (length-prefix) |
| `Concurrency` | 실행 모델 | 키 기반 파티션 — 커넥션별 순서 보장 |
| `Transport.Tcp` | 전송 | raw TCP |

### 2.2 `Program.cs`

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;

// ── 프레이밍: 최대 페이로드는 기본값에 기대지 말고 워크로드에 맞게 명시한다.
const int MaxPayload = 64 * 1024;
const int MaxFrame = MaxPayload + FrameHeader.Size;

FramingOptions framing = new() { MaxPayloadLength = MaxPayload };
FixedHeaderFrameEncoder encoder = new(framing);

// ── 실행 모델: 같은 커넥션의 메시지는 순차, 다른 커넥션끼리는 병렬.
await using PartitionedExecutionModel executionModel = new();

// ── 조립. 어긋난 조합(최대 프레임 > 전송 버퍼 한계)은 Build() 가 즉시 거부한다.
await using ChServerMServer server = new ServerBuilder()
    .UseTransport(new TcpServerTransport(
        new IPEndPoint(IPAddress.Any, 5000),
        new TcpTransportOptions
        {
            // 쓰기 일시정지 임계값은 최대 프레임보다 커야 한다. 작으면 큰 프레임에서
            // 커넥션이 조용히 교착한다 — 그래서 Build() 가 검증한다.
            PauseWriterThreshold = 2L * MaxFrame,
            ResumeWriterThreshold = MaxFrame,
        }))
    .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
    .UseExecutionModel(executionModel)
    .ConfigureDispatcher(dispatcher => dispatcher
        // 메시지 ID 1 번: 받은 페이로드를 그대로 돌려보낸다.
        // ID 0 은 '설정되지 않음' 센티넬이라 등록이 거부된다. 앱 대역은 1~40000.
        .MapRaw(new MessageId(1), async context =>
        {
            await FrameWriter.WriteFrameAsync(
                context.Connection.Output, encoder,
                context.Envelope.MessageId, context.Payload,
                FrameFlags.None, context.Envelope.Sequence, context.CancellationToken);
            return DispatchStatus.Handled;
        }))
    .Build();

await server.StartAsync();
Console.WriteLine($"에코 서버: {server.LocalEndPoint} — Ctrl+C 로 종료");
await Task.Delay(Timeout.InfiniteTimeSpan);
```

```bash
dotnet run
```

이것으로 서버가 돈다. 와이어 형식은 `[고정 헤더][페이로드]` 프레임이다.

### 2.3 클라이언트로 왕복 확인

같은 프레이밍·디스패치를 클라이언트도 쓴다. 별도 콘솔 프로젝트에:

```csharp
using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;

const int MaxPayload = 64 * 1024;
const int MaxFrame = MaxPayload + FrameHeader.Size;

FramingOptions framing = new() { MaxPayloadLength = MaxPayload };
FixedHeaderFrameEncoder encoder = new(framing);

TaskCompletionSource<byte[]> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);

// 클라이언트도 같은 조립 검증을 받는다 — 서버와 같은 임계값을 준다.
await using ChServerMClient client = new ClientBuilder()
    .UseTransport(new TcpClientTransport(new TcpTransportOptions
    {
        PauseWriterThreshold = 2L * MaxFrame,
        ResumeWriterThreshold = MaxFrame,
    }))
    .UseFraming(new FixedHeaderFrameDecoder(framing), encoder)
    .ConfigureDispatcher(dispatcher => dispatcher
        .MapRaw(new MessageId(1), context =>
        {
            // Payload 는 핸들러가 반환하면 무효가 된다 — await 너머로 들고 가려면 복사한다.
            echoed.TrySetResult(context.Payload.ToArray());
            return new ValueTask<DispatchStatus>(DispatchStatus.Handled);
        }))
    .Build();

ClientSession session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

await FrameWriter.WriteFrameAsync(
    session.Connection.Output, encoder, new MessageId(1),
    "안녕, ChServerM"u8, FrameFlags.None, sequence: 0, session.Connection.ConnectionClosed);

Console.WriteLine($"에코: {Encoding.UTF8.GetString(await echoed.Task)}");

await session.Connection.DisposeAsync();
```

`에코: 안녕, ChServerM` 이 찍히면 왕복이 완성됐다.

## 3. 다음 단계 — 축 갈아 끼우기

위 코드에서 바꿀 수 있는 것들이 곧 프레임워크의 표면이다.

| 바꾸고 싶은 것 | 방법 | 참조 샘플 |
|---|---|---|
| 전송 (TCP → HTTP/2·WebSocket·QUIC·인메모리) | `UseTransport(...)` 한 줄 | `ChServerM.Samples.StatelessWeb` (HTTP/2) |
| 순서 보장 없이 병렬로 (무상태 웹) | `UseExecutionModel` 을 빼면 스레드풀 병렬 | `ChServerM.Samples.StatelessWeb` |
| 타입 있는 메시지 + 직렬화 (MemoryPack·Protobuf) | `[MessageHandler]` 선언 + `MapGeneratedHandlers` — 등록 코드는 소스 제너레이터가 만든다 | `EchoServer` (MemoryPack) · `StatelessWeb` (Protobuf) |
| 룸 입장·브로드캐스트 (게임·채팅) | `ChServerM.RealTime.Rooms` 선택 축 | `ChServerM.Samples.GameRoom` |
| TLS | `.UseTransportSecurity(...)` | `Tests/ChServerM.Security.Tls.Tests` |

샘플은 전부 인자 없이 실행하면 자체 검증을 돌고 exit code 로 보고한다. `--serve [포트]` 로
실제 서버로 띄울 수 있다.

## 4. 막히면

| 증상 | 원인 | 해법 |
|---|---|---|
| `Build()` 가 조립을 거부하는 예외 | 최대 프레임 > 전송 버퍼 한계 (ADR-0007) | 예외 메시지의 두 수치를 맞춘다 — 이 조합은 런타임이면 교착이었다 |
| 핸들러 등록이 거부됨 | `MessageId(0)` 은 '설정되지 않음' 센티넬 | 1 이상을 쓴다. 앱 대역은 1~40000 |
| 빌드 오류 `CHSM1xxx` / `CHSM2xxx` | 소스 제너레이터가 선언 오류를 컴파일 타임에 잡은 것 | [DIAGNOSTICS.md](DIAGNOSTICS.md) 의 표에서 ID 를 찾는다 |
| 페이로드가 깨져 읽힘 | `Payload` 를 `await` 너머로 들고 감 | 핸들러 반환 전에 역직렬화하거나 복사한다 |
| SDK 버전 불일치로 빌드 실패 | `global.json` 고정과 로컬 SDK 드리프트 | `global.json` 이 요구하는 10.0.2xx 를 설치한다 |

## 5. 더 읽을 것

- [ARCHITECTURE.md](ARCHITECTURE.md) — 계층·의존 방향·확장 지점
- [DECISIONS.md](DECISIONS.md) — 왜 이렇게 설계했는가 (ADR 로그)
- [BENCHMARKS.md](BENCHMARKS.md) — 성능 주장의 근거 수치
- [DIAGNOSTICS.md](DIAGNOSTICS.md) — 컴파일 타임 진단(CHSM) 목록
