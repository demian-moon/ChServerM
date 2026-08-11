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
        // 메시지 ID 1 번: 받은 페이로드를 그대로 돌려보낸다. 여기서부터 핸들러를 늘려간다.
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
Console.WriteLine($"MyChServer 시작: {server.LocalEndPoint} — Ctrl+C 로 종료");

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // 기본 동작(즉시 종료)을 막고 정상 종료 경로를 탄다.
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException)
{
    // 정상 종료 요청.
}

Console.WriteLine("종료 중 — 신규 수용 중단 후 드레인한다.");
await server.UnbindAsync();

using CancellationTokenSource drain = new(TimeSpan.FromSeconds(10));
await server.StopAsync(drain.Token);
