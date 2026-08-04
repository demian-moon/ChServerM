using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// <b>ADR-0008 계약 테스트</b> — 파티션의 배타성+FIFO 보장을 프로덕션과 동일한
/// 조립 경로(<c>ServerBuilder</c> + 실제 <see cref="Pipe"/>)에서 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 이 파일의 전신은 2026-08-04 감사의 <b>반증 고정 테스트</b>였다 —
/// 스레드 어피니티 기반 파티션 고정(구 ADR-0005 주 경로)이 실전 경로에서 성립하지 않음을
/// 같은 테스트 구조로 증명했다(핸들러가 스레드풀로 이탈, 같은 파티션 두 커넥션의 핸들러가
/// 병렬 실행). ADR-0008 이 배타성을 완료 대기로 재정의한 뒤 단언을 계약 방향으로 뒤집었다.
/// 이 테스트들이 깨지면 <b>그 반증이 재발한 것</b>이다.
/// </para>
/// <para>
/// <b>기존 검증이 결함을 놓쳤던 이유를 그대로 막는다.</b> 단위 테스트는
/// <c>Task.Yield()</c>(스케줄러 캡처)로만 고정을 검증했고, 통합 하네스는
/// <c>ServerBuilder</c> 를 우회했다. 그래서 이 파일은 <c>ServerBuilder</c> 조립과
/// 실제 파이프의 연속 스케줄링 경로("매달린 <c>ReadAsync</c> 를 깨우는" 경로)만 쓴다.
/// </para>
/// <para>
/// 전송은 InMemory 를 쓴다 — TCP 와 동일하게 실제 <see cref="Pipe"/> 를 쓰므로 검증
/// 메커니즘이 같고, 소켓 타이밍 변수가 없어 결정적이다.
/// </para>
/// </remarks>
public sealed class PartitionExclusivityContractTests
{
    private const ushort ProbeMessageId = 100;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 핸들러의 동기 구간은 항상 파티션 전용 스레드에서 실행된다.
    /// </summary>
    /// <remarks>
    /// 배타 작업(<c>PartitionDispatchGate</c>)의 <c>ExecuteAsync</c> 는 파티션 스레드에서
    /// 호출되므로, 핸들러 진입(첫 <c>await</c> 이전)은 프레임 도착 타이밍과 무관하게
    /// 파티션 스레드다. 구 설계에서는 이것이 "매달린 ReadAsync 를 깨우는" 경로에서
    /// 스레드풀로 이탈했다.
    /// </remarks>
    [Fact]
    public async Task HandlerSyncSegment_AlwaysRunsOnPartitionThread()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });

        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        ConcurrentQueue<int> handlerThreads = new();

        MessageDelegate probe = async context =>
        {
            handlerThreads.Enqueue(Environment.CurrentManagedThreadId);

            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Header.MessageId,
                context.Payload,
                FrameFlags.None,
                context.Header.Sequence,
                context.CancellationToken).ConfigureAwait(false);

            return DispatchStatus.Handled;
        };

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"exclusivity-thread-{Guid.NewGuid():N}");
        InMemoryTransportOptions transportOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, transportOptions))
            .UseFraming(decoder, encoder)
            .UseExecutionModel(model)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(ProbeMessageId), probe))
            .Build();

        await server.StartAsync(timeout.Token);

        int partitionThreadId = await Task.Factory.StartNew(
            static () => Environment.CurrentManagedThreadId,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            model.GetPartition(0).Scheduler);

        await using (InMemoryClientTransport client = new(hub, null, transportOptions))
        {
            await using IConnection connection = await client.ConnectAsync(endPoint, timeout.Token);

            // 에코를 기다렸다 다음을 보낸다 — 매 프레임이 "매달린 ReadAsync 를 깨우는"
            // 연속 스케줄링 경로(구 설계가 이탈했던 바로 그 경로)를 타게 한다.
            for (int i = 0; i < 5; i++)
            {
                await connection.WriteFrameAsync(
                    encoder, new MessageId(ProbeMessageId), [1, 2, 3], FrameFlags.None, sequence: 0);
                _ = await ReceiveFrameAsync(connection, decoder, timeout.Token);
            }
        }

        Assert.Equal(5, handlerThreads.Count);
        Assert.All(handlerThreads, threadId => Assert.Equal(partitionThreadId, threadId));
    }

    /// <summary>
    /// 같은 파티션에 배정된 두 커넥션의 핸들러 동기 구간은 절대 겹치지 않는다.
    /// </summary>
    /// <remarks>
    /// 첫 핸들러가 동기 대기하는 동안 두 번째 핸들러는 진입할 수 없어야 한다.
    /// 대기 장치(<see cref="CountdownEvent"/>)는 겹침이 생기면 즉시 통과하고,
    /// 배타적이면 타임아웃까지 기다린다 — 반증 시절에는 즉시 통과(겹침)를 관측했다.
    /// </remarks>
    [Fact]
    public async Task TwoConnections_SamePartition_SyncHandlersNeverOverlap()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });

        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        using CountdownEvent bothInside = new(2);
        OverlapObserver observer = new();

        MessageDelegate blocker = async context =>
        {
            observer.Enter();
            bothInside.Signal();

            // 동기 대기 — 배타적이라면 상대는 이 동안 진입할 수 없고 반드시 타임아웃된다.
            bothInside.Wait(TimeSpan.FromSeconds(1));

            observer.Exit();

            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Header.MessageId,
                context.Payload,
                FrameFlags.None,
                context.Header.Sequence,
                context.CancellationToken).ConfigureAwait(false);

            return DispatchStatus.Handled;
        };

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"exclusivity-sync-{Guid.NewGuid():N}");
        InMemoryTransportOptions transportOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, transportOptions))
            .UseFraming(decoder, encoder)
            .UseExecutionModel(model)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(ProbeMessageId), blocker))
            .Build();

        await server.StartAsync(timeout.Token);

        await using (InMemoryClientTransport client = new(hub, null, transportOptions))
        {
            await using IConnection first = await client.ConnectAsync(endPoint, timeout.Token);
            await using IConnection second = await client.ConnectAsync(endPoint, timeout.Token);

            // 두 읽기 루프가 모두 매달린 뒤 응답을 기다리지 않고 동시에 밀어 넣는다 —
            // 겹칠 수 있는 조건을 최대로 만든 상태에서 겹치지 않음을 확인해야 계약 검증이다.
            await Task.Delay(100, timeout.Token);

            await first.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [1], FrameFlags.None, sequence: 0);
            await second.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [2], FrameFlags.None, sequence: 0);

            _ = await ReceiveFrameAsync(first, decoder, timeout.Token);
            _ = await ReceiveFrameAsync(second, decoder, timeout.Token);
        }

        Assert.Equal(1, observer.MaxInside);
    }

    /// <summary>
    /// <c>await</c> 를 걸치는 비동기 핸들러에서도 배타성이 유지된다 — 이것이 ADR-0008 이
    /// 스레드 어피니티 대신 완료 대기를 선택한 이유다.
    /// </summary>
    /// <remarks>
    /// 핸들러가 <c>Task.Delay</c> 로 스레드풀에 연속을 넘겨도, 파티션은 그 완료까지
    /// 다음 작업을 시작하지 않는다. 스레드 어피니티 방식(구 설계)은 어떤 수단으로도
    /// 이 경우를 보장할 수 없었다 — 연속이 파티션 밖에서 돌기 때문이다.
    /// </remarks>
    [Fact]
    public async Task AsyncHandler_ExclusivityHoldsAcrossAwaits()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        PartitionedExecutionModel model = new(new PartitionedExecutionOptions { PartitionCount = 1 });

        FramingOptions framing = new() { MaxPayloadLength = 1024 };
        FixedHeaderFrameDecoder decoder = new(framing);
        FixedHeaderFrameEncoder encoder = new(framing);

        OverlapObserver observer = new();

        MessageDelegate asyncProbe = async context =>
        {
            observer.Enter();

            // 진짜 비동기 지점 — 연속은 스레드풀에서 이어진다. 배타 구간은 유지돼야 한다.
            await Task.Delay(100).ConfigureAwait(false);

            observer.Exit();

            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Header.MessageId,
                context.Payload,
                FrameFlags.None,
                context.Header.Sequence,
                context.CancellationToken).ConfigureAwait(false);

            return DispatchStatus.Handled;
        };

        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"exclusivity-async-{Guid.NewGuid():N}");
        InMemoryTransportOptions transportOptions = new();

        await using ChServerMServer server = new ServerBuilder()
            .UseTransport(new InMemoryServerTransport(hub, endPoint, transportOptions))
            .UseFraming(decoder, encoder)
            .UseExecutionModel(model)
            .ConfigureDispatcher(d => d.MapRaw(new MessageId(ProbeMessageId), asyncProbe))
            .Build();

        await server.StartAsync(timeout.Token);

        await using (InMemoryClientTransport client = new(hub, null, transportOptions))
        {
            await using IConnection first = await client.ConnectAsync(endPoint, timeout.Token);
            await using IConnection second = await client.ConnectAsync(endPoint, timeout.Token);

            await Task.Delay(100, timeout.Token);

            await first.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [1], FrameFlags.None, sequence: 0);
            await second.WriteFrameAsync(
                encoder, new MessageId(ProbeMessageId), [2], FrameFlags.None, sequence: 0);

            _ = await ReceiveFrameAsync(first, decoder, timeout.Token);
            _ = await ReceiveFrameAsync(second, decoder, timeout.Token);
        }

        Assert.Equal(1, observer.MaxInside);
    }

    /// <summary>핸들러 동시 진입 깊이를 기록하는 관측자.</summary>
    /// <remarks>람다가 <c>ref</c> 지역을 캡처할 수 없어 필드를 가진 객체로 둔다.</remarks>
    private sealed class OverlapObserver
    {
        private int _inside;
        private int _maxInside;

        /// <summary>관측된 최대 동시 진입 깊이.</summary>
        public int MaxInside => Volatile.Read(ref _maxInside);

        /// <summary>핸들러 진입을 기록하고 최대 깊이를 CAS 로 갱신한다.</summary>
        public void Enter()
        {
            int observed = Interlocked.Increment(ref _inside);

            int current;
            while (observed > (current = Volatile.Read(ref _maxInside))
                && Interlocked.CompareExchange(ref _maxInside, observed, current) != current)
            {
                // 경합 시 재시도.
            }
        }

        /// <summary>핸들러 이탈을 기록한다.</summary>
        public void Exit() => Interlocked.Decrement(ref _inside);
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
