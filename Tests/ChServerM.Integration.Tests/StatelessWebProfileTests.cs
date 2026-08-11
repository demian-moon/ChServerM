using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks;
using ChServerM.Concurrency;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// <b>Phase 16 의 합격 기준 — <c>stateless-web</c> 참조 프로필이 <c>realtime-stateful</c> 과
/// 같은 핸들러 코드로 동작하는가(ADR-0004).</b>
/// </summary>
/// <remarks>
/// <para>
/// 두 프로필의 축 조합(ROADMAP "검증용 참조 프로필"):
/// </para>
/// <list type="bullet">
///   <item><c>realtime-stateful</c> — TCP + 고정 헤더 프레이밍 + 파티션 실행 모델(유저별
///   순서 보장) + 노드 로컬 세션 저장소</item>
///   <item><c>stateless-web</c> — HTTP/Kestrel + 병렬 실행(실행 모델 없음) + <b>외부화된</b>
///   세션 저장소(여러 노드가 같은 저장소를 본다)</item>
/// </list>
/// <para>
/// 핸들러(<see cref="CounterHandler"/>)는 <b>단 하나의 타입</b>이고 전송·실행 모델·저장소
/// 배치를 알지 못한다. 세션 접근이 <see cref="ISessionStore"/> 계약(CAS)만 쓰기 때문에,
/// 저장소가 노드 로컬인지 외부 공유인지는 조립이 정한다 — 그것이 "무상태 모드"의 실체다.
/// </para>
/// <para>
/// <c>stateless-web</c> 쪽은 <b>서버 노드 두 개</b>를 세우고 같은 세션을 번갈아 친다.
/// 카운터가 노드를 건너 이어지면 상태가 커넥션이 아니라 저장소에 있음이 증명된다 —
/// 수평 확장의 전제다.
/// </para>
/// </remarks>
public sealed class StatelessWebProfileTests
{
    private const ushort CounterMessageId = 700;

    /// <summary>세션 카운터를 1 올리고 새 값을 돌려주는 핸들러. 전송·실행 모델·저장소 배치를 알지 못한다.</summary>
    /// <remarks>
    /// 요청 페이로드: 세션 키(8B LE). 응답 페이로드: 증가 후 카운터(8B LE).
    /// 쓰기는 CAS 재시도 루프다 — 같은 세션을 여러 노드가 동시에 치면 충돌이 나고,
    /// 충돌은 다시 읽어 해소한다(CONSISTENCY.md 5절의 표준 경로).
    /// </remarks>
    private sealed class CounterHandler(IFrameEncoder encoder, ISessionStore store)
    {
        public async ValueTask<DispatchStatus> HandleAsync(MessageContext context)
        {
            // 페이로드는 핸들러 반환 후 무효가 된다 — await 전에 값으로 읽는다.
            Span<byte> key = stackalloc byte[sizeof(long)];
            context.Payload.Slice(0, sizeof(long)).CopyTo(key);
            SessionId sessionId = new(new ObjectId(BinaryPrimitives.ReadInt64LittleEndian(key)));

            byte[] state = new byte[sizeof(long)];
            long counter;

            while (true)
            {
                ArrayBufferWriter<byte> readBuffer = new(sizeof(long));
                SessionReadResult read = await store.TryReadAsync(
                    sessionId, readBuffer, context.CancellationToken).ConfigureAwait(false);

                counter = read.Found
                    ? BinaryPrimitives.ReadInt64LittleEndian(readBuffer.WrittenSpan)
                    : 0;

                counter++;
                BinaryPrimitives.WriteInt64LittleEndian(state, counter);

                SessionWriteResult write = await store.TryWriteAsync(
                    sessionId, state, read.Version, TimeSpan.FromMinutes(1),
                    context.CancellationToken).ConfigureAwait(false);

                if (write.Succeeded)
                {
                    break;
                }

                // 충돌 = 다른 커넥션(노드)이 먼저 썼다. 다시 읽어 그 위에 올린다.
            }

            await FrameWriter.WriteFrameAsync(
                context.Connection.Output, encoder, context.Envelope.MessageId, state,
                FrameFlags.None, context.Envelope.Sequence, context.CancellationToken)
                .ConfigureAwait(false);

            return DispatchStatus.Handled;
        }
    }

    private static async Task<long> IncrementAsync(
        TestHarness harness, IConnection connection, long sessionKey)
    {
        byte[] request = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(request, sessionKey);

        await harness.SendAsync(connection, CounterMessageId, request);
        (_, byte[] response) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        return BinaryPrimitives.ReadInt64LittleEndian(response);
    }

    [Fact]
    public async Task RealtimeStatefulProfile_SameHandler_CountsPerSession()
    {
        // realtime-stateful: TCP + 파티션 실행 모델 + 노드 로컬 저장소.
        FixedHeaderFrameEncoder encoder = new(4096);
        InMemorySessionStore store = new();
        CounterHandler handler = new(encoder, store);

        await using PartitionedExecutionModel executionModel = new();

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(CounterMessageId), handler.HandleAsync),
            TransportKind.Tcp,
            executionModel: executionModel);

        await using IConnection connection = await harness.ConnectAsync();

        Assert.Equal(1, await IncrementAsync(harness, connection, sessionKey: 42));
        Assert.Equal(2, await IncrementAsync(harness, connection, sessionKey: 42));
        Assert.Equal(1, await IncrementAsync(harness, connection, sessionKey: 43));
        Assert.Equal(3, await IncrementAsync(harness, connection, sessionKey: 42));
    }

    [Fact]
    public async Task StatelessWebProfile_SameHandler_CounterSurvivesAcrossNodes()
    {
        // stateless-web: HTTP + 병렬 실행(실행 모델 없음) + 외부 공유 저장소.
        // 저장소 인스턴스 "하나"를 서버 노드 "둘"이 본다 — Redis 를 대신하는 자리다.
        FixedHeaderFrameEncoder encoder = new(4096);
        InMemorySessionStore sharedStore = new();

        // 같은 핸들러 타입이다. 위 realtime-stateful 테스트와 코드가 완전히 같다.
        CounterHandler handler = new(encoder, sharedStore);

        await using TestHarness node1 = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(CounterMessageId), handler.HandleAsync),
            TransportKind.Http);

        await using TestHarness node2 = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(CounterMessageId), handler.HandleAsync),
            TransportKind.Http);

        // 노드 1 에서 두 번.
        await using (IConnection connection = await node1.ConnectAsync())
        {
            Assert.Equal(1, await IncrementAsync(node1, connection, sessionKey: 7));
            Assert.Equal(2, await IncrementAsync(node1, connection, sessionKey: 7));
        }

        // 노드 2 로 옮겨도 카운터가 이어진다 — 상태는 커넥션이 아니라 저장소에 있다.
        await using (IConnection connection = await node2.ConnectAsync())
        {
            Assert.Equal(3, await IncrementAsync(node2, connection, sessionKey: 7));
            Assert.Equal(4, await IncrementAsync(node2, connection, sessionKey: 7));
        }

        // 다시 노드 1. 세션이 다르면 독립이다.
        await using (IConnection connection = await node1.ConnectAsync())
        {
            Assert.Equal(5, await IncrementAsync(node1, connection, sessionKey: 7));
            Assert.Equal(1, await IncrementAsync(node1, connection, sessionKey: 8));
        }
    }

    [Fact]
    public async Task StatelessWebProfile_ConcurrentSessions_DoNotInterfere()
    {
        // 병렬 실행 모델에서 세션이 다르면 완전히 독립으로 진행돼야 한다.
        FixedHeaderFrameEncoder encoder = new(4096);
        InMemorySessionStore sharedStore = new();
        CounterHandler handler = new(encoder, sharedStore);

        await using TestHarness node = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(CounterMessageId), handler.HandleAsync),
            TransportKind.Http);

        const int SessionCount = 8;
        const int IncrementsPerSession = 20;

        Task[] clients = new Task[SessionCount];
        for (int i = 0; i < SessionCount; i++)
        {
            long sessionKey = 1000 + i;
            clients[i] = Task.Run(async () =>
            {
                await using IConnection connection = await node.ConnectAsync();
                for (int round = 1; round <= IncrementsPerSession; round++)
                {
                    Assert.Equal(round, await IncrementAsync(node, connection, sessionKey));
                }
            });
        }

        await Task.WhenAll(clients);
    }
}
