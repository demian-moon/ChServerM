using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Execution;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 2026-08-04 감사에서 발견된 커넥션 생명주기 결함 3건(H1~H3)의 회귀 테스트.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 셋 다 "테스트 전부 녹색인데 프로덕션에서만 죽는" 부류였다 —
/// 등록 경합(H1)은 고빈도 접속 이탈에서만, 소유권 위반(H2)은 미들웨어가 디스패치 중
/// <c>Abort</c> 를 부를 때만, 무한 드레인(H3)은 상대가 읽지 않는 종료에서만 드러난다.
/// 각 결함의 발생 조건을 그대로 재현해 고정한다.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>H1</b> — 커넥션 등록이 핸들러 기동보다 늦어, 즉시 끝난 핸들러의 정리가 등록보다
///     먼저 실행되면 죽은 항목이 영구히 남았다(<c>ConnectionCount</c> 인플레이션 →
///     상한 판정이 살아 있는 연결을 거부)
///   </description></item>
///   <item><description>
///     <b>H2</b> — InMemory <c>Abort</c> 가 소유하지 않은 파이프 끝을 <c>Complete</c> 해,
///     디스패치에서 돌아온 읽기 루프의 <c>AdvanceTo</c> 가 던지고 HandlerFaulted 로
///     오분류됐다. 같은 코드가 TCP 에서는 멀쩡히 돌았다(ADR-0004 위반)
///   </description></item>
///   <item><description>
///     <b>H3</b> — InMemory <c>DisposeAsync</c> 의 드레인 플러시에 상한이 없어, 상대가
///     읽지 않으면 서버 종료가 커넥션 하나에 영구히 볼모로 잡혔다
///   </description></item>
/// </list>
/// </remarks>
public sealed class ConnectionLifecycleRegressionTests
{
    private const ushort ProbeMessageId = 100;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// [H2] 미들웨어·핸들러가 디스패치 중 <c>Abort</c> 를 불러도 읽기 루프는 예외 없이
    /// 정상 종료 경로를 밟는다 — HandlerFaulted 오분류가 없어야 한다.
    /// </summary>
    [Fact]
    public async Task AbortDuringDispatch_InMemory_TerminatesWithoutHandlerFault()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        RecordingLogger logger = new();

        // FramedConnectionOptions 문서가 안내하는 바로 그 패턴 — 디스패치 중 Abort.
        MessageDelegate aborter = context =>
        {
            context.Connection.Abort(new ConnectionCloseInfo(
                CloseReason.ApplicationError, ErrorCode.None, "테스트 정책 종료"));
            return new ValueTask<DispatchStatus>(DispatchStatus.Handled);
        };

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"abort-mid-dispatch-{Guid.NewGuid():N}");
        InMemoryTransportOptions transportOptions = new();
        InMemoryServerTransport transport = new(hub, endPoint, transportOptions, logger);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(decoder, encoder)
            .UseLogger(logger)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(ProbeMessageId), aborter))
            .Build();

        await server.StartAsync(timeout.Token);

        await using (InMemoryClientTransport client = new(hub, null, transportOptions))
        {
            await using IConnection connection = await client.ConnectAsync(endPoint, timeout.Token);

            await connection.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [1], FrameFlags.None, sequence: 0);

            // Abort 된 커넥션이므로 응답 대신 스트림 종료를 보게 된다.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ReceiveFrameAsync(connection, decoder, timeout.Token));
        }

        await WaitForZeroConnectionsAsync(() => transport.ConnectionCount, timeout.Token);

        // 결함의 핵심 증상 — 정책적 Abort 가 HandlerFaulted(1006)로 오분류되면 안 된다.
        Assert.DoesNotContain(1006, logger.EventIds);
    }

    /// <summary>
    /// [H3] 상대가 읽지 않아 백프레셔에 걸린 미전송 데이터가 있어도, 정상 종료의
    /// 드레인은 상한 시간 안에 끝나고 서버 정리가 완료된다.
    /// </summary>
    [Fact]
    public async Task BackpressuredDrain_InMemory_DisposeCompletesWithinBound()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        // 일시정지 임계값(64KiB)의 두 배를 플러시 없이 쌓아 두고 반환한다.
        // 종료 드레인이 이것을 밀어내려다 백프레셔에 걸리는 것이 결함 조건이다.
        MessageDelegate stuffer = context =>
        {
            PipeWriter output = context.Connection.Output;
            const int Chunk = 4096;

            for (int i = 0; i < 32; i++)
            {
                Span<byte> span = output.GetSpan(Chunk);
                span[..Chunk].Clear();
                output.Advance(Chunk);
            }

            return new ValueTask<DispatchStatus>(DispatchStatus.Handled);
        };

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"drain-bound-{Guid.NewGuid():N}");
        InMemoryTransportOptions transportOptions = new()
        {
            // 테스트가 결함 유무를 시간으로 판정하므로 상한을 짧게 잡는다.
            ShutdownTimeout = TimeSpan.FromMilliseconds(200),
        };
        InMemoryServerTransport transport = new(hub, endPoint, transportOptions);

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(decoder, encoder)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(ProbeMessageId), stuffer))
            .Build();

        await server.StartAsync(timeout.Token);

        await using (InMemoryClientTransport client = new(hub, null, transportOptions))
        {
            await using IConnection connection = await client.ConnectAsync(endPoint, timeout.Token);

            await connection.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [1], FrameFlags.None, sequence: 0);

            // 반쪽 종료 — 클라이언트의 송신만 닫는다(Output 은 이 테스트가 소유한 끝이다).
            // 서버 읽기 루프는 EOF 를 보고 종료 경로에 들어가지만, 클라이언트의 수신은
            // 살아 있으면서 읽지 않으므로 서버의 드레인 플러시는 백프레셔에 걸린다.
            // 수정 전에는 이 대기에 상한이 없어 여기서 영구 정지했다.
            await connection.Output.CompleteAsync();

            await WaitForZeroConnectionsAsync(() => transport.ConnectionCount, timeout.Token);
        }
    }

    /// <summary>
    /// [H1] 접속 직후 즉시 끊기를 반복해도 커넥션 목록에 죽은 항목이 남지 않고,
    /// 이후의 정상 접속이 상한 판정에 거부되지 않는다.
    /// </summary>
    /// <remarks>
    /// 경합 재현은 확률적이지만(등록보다 정리가 먼저 실행되는 창), 수정 전 코드는
    /// 이 반복에서 죽은 항목을 누적시켜 <c>ServerConnectionCount</c> 가 0 으로
    /// 돌아오지 못한다. 수정 후에는 구조적으로 불가능하다.
    /// </remarks>
    [Fact]
    public async Task RapidConnectDispose_Tcp_LeavesNoZombieEntries()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(ProbeMessageId), Echo()),
            kind: TransportKind.Tcp,
            tcpOptions: new TcpTransportOptions { MaxConnections = 64 });

        for (int i = 0; i < 100; i++)
        {
            // 에코를 기다리지 않고 즉시 끊는다 — 핸들러가 기동 직후 종료되는 창을 만든다.
            IConnection connection = await harness.ConnectAsync();
            await connection.DisposeAsync();
        }

        await WaitForZeroConnectionsAsync(() => harness.ServerConnectionCount, timeout.Token);

        // 죽은 항목이 상한을 잠식하지 않았다면 정상 왕복이 그대로 동작해야 한다.
        await using IConnection alive = await harness.ConnectAsync();
        await harness.SendAsync(alive, ProbeMessageId, [1, 2, 3]);
        (_, byte[] echoed) = await harness.ReceiveAsync(alive, timeout.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, echoed);
    }

    /// <summary>
    /// [감사 보강] 서버 생명주기 — 이중 시작이 거부되고, <c>StopAsync</c> 가 실행 모델까지
    /// 정리한다("전송 먼저, 실행 모델 나중" 순서의 관측 가능한 결과).
    /// </summary>
    /// <remarks>
    /// 2026-08-04 감사에서 <c>ChServerMServer</c> 생명주기가 무테스트로 지적됐다 —
    /// 종료 순서 성질이 주석으로만 존재했다.
    /// </remarks>
    [Fact]
    public async Task ServerLifecycle_StopDisposesExecutionModel_AndDoubleStartThrows()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });
        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"lifecycle-{Guid.NewGuid():N}");
        InMemoryTransportOptions transportOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, transportOptions))
            .UseFraming(decoder, encoder)
            .UseExecutionModel(model)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(ProbeMessageId), Echo()))
            .Build();

        await server.StartAsync(timeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(timeout.Token).AsTask());

        // 왕복 한 번으로 서버가 실제로 동작함을 확인한 뒤 멈춘다 —
        // "시작됐다"가 아니라 "메시지를 처리한다"를 확인해야 생명주기 검증이다.
        await using (InMemoryClientTransport client = new(hub, null, transportOptions))
        {
            await using IConnection connection = await client.ConnectAsync(endPoint, timeout.Token);
            await connection.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [7], FrameFlags.None, sequence: 0);
            byte[] echoed = await ReceiveFrameAsync(connection, decoder, timeout.Token);
            Assert.Equal(new byte[] { 7 }, echoed);
        }

        await server.StopAsync(timeout.Token);

        // StopAsync 는 실행 모델을 소유하므로 함께 정리해야 한다. 정리된 파티션은
        // 새 작업을 받지 않는다 — 받는다면 갈 곳 없는 작업이 조용히 쌓인다.
        Assert.False(model.GetPartition(0).TryPost(new NoopWork()));
    }

    private readonly struct NoopWork : IPartitionWork
    {
        public void Execute()
        {
            // 게시 가능 여부만 본다.
        }
    }

    private static MessageDelegate Echo() => async context =>
    {
        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            new FixedHeaderFrameEncoder(4096),
            context.Header.MessageId,
            context.Payload,
            FrameFlags.None,
            context.Header.Sequence,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    };

    /// <summary>커넥션 수가 0 이 될 때까지 폴링한다. 시간 안에 안 되면 실패.</summary>
    private static async Task WaitForZeroConnectionsAsync(Func<int> count, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (count() != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(10),
                $"커넥션 정리가 10초 안에 끝나지 않았다. 잔여: {count()}");

            await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>기록된 이벤트 ID 를 검사할 수 있는 테스트 로거.</summary>
    private sealed class RecordingLogger : IServerLogger
    {
        public ConcurrentBag<int> EventIds { get; } = [];

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => EventIds.Add(eventId.Id);
    }

    /// <summary>프레임 하나가 도착할 때까지 읽는다. (TestHarness 와 같은 요령)</summary>
    private static async Task<byte[]> ReceiveFrameAsync(
        IConnection connection,
        FixedHeaderFrameDecoder decoder,
        CancellationToken cancellationToken)
    {
        PipeReader reader = connection.Input;

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = read.Buffer;

            FrameDecodeResult decoded = decoder.Decode(buffer);

            if (decoded.IsDecoded)
            {
                byte[] payload = decoded.Payload.ToArray();
                reader.AdvanceTo(decoded.Consumed, decoded.Examined);
                return payload;
            }

            reader.AdvanceTo(decoded.Consumed, decoded.Examined);

            if (decoded.IsFatal)
            {
                throw new InvalidOperationException($"응답 프레임 디코딩 실패: {decoded.Status}");
            }

            if (read.IsCompleted)
            {
                throw new InvalidOperationException("프레임이 도착하기 전에 스트림이 끝났다.");
            }
        }
    }
}
