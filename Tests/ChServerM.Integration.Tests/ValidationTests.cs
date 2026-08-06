using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Serialization;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 입력 검증 훅(T-22 잔여)의 종단 검증 — <b>역직렬화 성공 ≠ 유효한 값</b>이 구조로
/// 강제됨을 고정한다: 범위 밖 값은 핸들러에 닿지 못하고, 기본 정책으로 커넥션이 닫힌다.
/// </summary>
public sealed class ValidationTests
{
    private const ushort MoveId = 600;

    /// <summary>테스트용 이동 메시지 — 좌표는 [-1000, 1000] 이 유효 범위(앱 규칙 역할).</summary>
    private readonly record struct Move(int X, int Y);

    /// <summary>4바이트 LE 좌표 2개 — 직렬화 축 없이 파이프라인을 검증하는 수제 직렬화기.</summary>
    private sealed class MoveSerializer : IMessageSerializer<Move>
    {
        public void Serialize(IBufferWriter<byte> writer, in Move message)
        {
            Span<byte> span = writer.GetSpan(8);
            BinaryPrimitives.WriteInt32LittleEndian(span, message.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], message.Y);
            writer.Advance(8);
        }

        public bool TryDeserialize(in ReadOnlySequence<byte> payload, out Move message)
        {
            message = default;
            if (payload.Length != 8)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[8];
            payload.CopyTo(buffer);
            message = new Move(
                BinaryPrimitives.ReadInt32LittleEndian(buffer),
                BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]));
            return true;
        }
    }

    /// <summary>좌표 범위 검증 — 스키마는 맞지만 의미가 틀린 값을 잡는 앱 규칙의 표본.</summary>
    private sealed class MoveValidator : IMessageValidator<Move>
    {
        public bool Validate(in Move message) =>
            message.X is >= -1000 and <= 1000 && message.Y is >= -1000 and <= 1000;
    }

    private static void ConfigureDispatcher(MessageDispatcherBuilder dispatcher, Action<Move> onHandled)
    {
        dispatcher.Map(
            new MessageId(MoveId),
            new MoveSerializer(),
            new MoveValidator(),
            new DelegateHandler(onHandled));
    }

    private sealed class DelegateHandler(Action<Move> onHandled) : IMessageHandler<Move>
    {
        public ValueTask HandleAsync(MessageContext context, Move message)
        {
            onHandled(message);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Valid_message_reaches_handler(TransportKind kind)
    {
        TaskCompletionSource<Move> handled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestHarness harness = await TestHarness.StartAsync(
            dispatcher => ConfigureDispatcher(dispatcher, move => handled.TrySetResult(move)), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, MoveId, SerializeMove(new Move(10, -20)));

        Move received = await handled.Task.WaitAsync(timeout.Token);
        Assert.Equal(new Move(10, -20), received);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Out_of_range_message_never_reaches_handler_and_closes_connection(TransportKind kind)
    {
        bool handlerRan = false;
        await using TestHarness harness = await TestHarness.StartAsync(
            dispatcher => ConfigureDispatcher(dispatcher, _ => handlerRan = true), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        IConnection connection = await harness.ConnectAsync();

        // 스키마는 완벽하다(8바이트 정수 둘) — 값만 범위 밖이다. 역직렬화는 통과하고
        // 검증이 잡아야 한다. 기본 정책(CloseOnDeserializationFailure=true)으로 종료된다.
        await harness.SendAsync(connection, MoveId, SerializeMove(new Move(999_999, 0)));

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(connection, timeout.Token));
        Assert.False(handlerRan, "범위 밖 값이 핸들러에 도달했다 — 검증이 구조를 강제하지 못한다.");
    }

    private static byte[] SerializeMove(Move move)
    {
        ArrayBufferWriter<byte> writer = new(8);
        new MoveSerializer().Serialize(writer, in move);
        return writer.WrittenSpan.ToArray();
    }
}
