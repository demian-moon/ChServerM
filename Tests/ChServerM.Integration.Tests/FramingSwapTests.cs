using System;
using System.Text;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 프레이밍 축 교체 검증 — <b>같은 핸들러 코드</b>가 고정 헤더와 varint 프레이밍
/// 양쪽에서 동작해야 한다 (완료 기준 DoD-5: 두 번째 구현체 또는 교체 테스트).
/// </summary>
/// <remarks>
/// <para>
/// 핸들러·디스패치·전송 코드는 하네스의 것을 그대로 쓰고, 주입하는 코덱 쌍만 바꾼다.
/// 여기에 프레이밍별 분기가 생긴다면 ADR-0010 의 분리가 새고 있는 것이다.
/// </para>
/// <para>
/// 두 프레이밍의 성질이 정반대라는 점이 이 테스트의 가치다 — 고정 16바이트(버전·플래그·
/// 일련번호 있음) vs 가변 2~8바이트(전부 없음). 한쪽만 도는 추상화는 추상화가 아니다(ADR-0004).
/// </para>
/// </remarks>
public sealed class FramingSwapTests
{
    private const ushort EchoMessageId = 10;
    private const int MaxPayload = 4096;

    /// <summary>프레이밍과 무관하게 동일해야 하는 에코 핸들러.</summary>
    private static MessageDelegate Echo(IFrameEncoder encoder) =>
        async context =>
        {
            await FrameWriter.WriteFrameAsync(
                context.Connection.Output,
                encoder,
                context.Envelope.MessageId,
                context.Payload,
                context.Envelope.Flags,
                context.Envelope.Sequence,
                context.Connection.ConnectionClosed).ConfigureAwait(false);

            return DispatchStatus.Handled;
        };

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task SameHandler_RunsOnVarintFraming(TransportKind kind)
    {
        VarintFrameDecoder decoder = new(MaxPayload);
        VarintFrameEncoder encoder = new(MaxPayload);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(encoder)),
            kind: kind,
            maxPayloadLength: MaxPayload,
            decoder: decoder,
            encoder: encoder);

        await using var connection = await harness.ConnectAsync();

        byte[] payload = Encoding.UTF8.GetBytes("varint 프레이밍 위의 동일 핸들러");
        await harness.SendAsync(connection, EchoMessageId, payload);

        (MessageEnvelope envelope, byte[] echoed) = await harness.ReceiveAsync(connection);

        Assert.Equal(new MessageId(EchoMessageId), envelope.MessageId);
        Assert.Equal(payload, echoed);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task VarintFraming_MultipleRoundTrips_AcrossVarintBoundaries(TransportKind kind)
    {
        // 페이로드 크기가 varint 경계(127/128)를 넘나들어도 경계 복원이 유지되는지.
        VarintFrameDecoder decoder = new(MaxPayload);
        VarintFrameEncoder encoder = new(MaxPayload);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo(encoder)),
            kind: kind,
            maxPayloadLength: MaxPayload,
            decoder: decoder,
            encoder: encoder);

        await using var connection = await harness.ConnectAsync();

        foreach (int size in new[] { 0, 1, 127, 128, 300, 2048 })
        {
            byte[] payload = new byte[size];
            Random.Shared.NextBytes(payload);

            await harness.SendAsync(connection, EchoMessageId, payload);
            (MessageEnvelope envelope, byte[] echoed) = await harness.ReceiveAsync(connection);

            Assert.Equal(new MessageId(EchoMessageId), envelope.MessageId);
            Assert.Equal(payload, echoed);
        }
    }
}
