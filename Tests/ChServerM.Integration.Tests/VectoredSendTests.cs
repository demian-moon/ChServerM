using System;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 벡터드 송신(<see cref="TcpTransportOptions.UseVectoredSend"/>) 경로의 정확성 검증.
/// </summary>
/// <remarks>
/// <para>
/// 성능 판정은 별개다 — 루프백 실측에서 이득이 없어 기본값이 꺼져 있다
/// (BENCHMARKS.md 송신 배칭 절). 이 테스트가 고정하는 것은 <b>정확성</b>이다:
/// 다중 세그먼트 gather 송신과 부분 전송 재개(머리 세그먼트 제거 + 걸친 세그먼트
/// 슬라이스)가 바이트를 하나도 잃거나 겹치지 않는다는 것. 옵션이 남아 있는 한
/// (Phase 12 NIC 재검 예정) 이 경로는 테스트로 보호돼야 한다.
/// </para>
/// <para>클라이언트 전송도 같은 옵션을 쓰므로 양방향 벡터드 경로가 함께 검증된다.</para>
/// </remarks>
public sealed class VectoredSendTests
{
    private const ushort EchoMessageId = 100;

    private static Task<TestHarness> StartVectoredEchoAsync(int maxPayloadLength)
    {
        FramingOptions framing = new() { MaxPayloadLength = maxPayloadLength };
        FixedHeaderFrameEncoder encoder = new(framing);

        return TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, encoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken)
                    .ConfigureAwait(false);
                return DispatchStatus.Handled;
            }),
            kind: TransportKind.Tcp,
            tcpOptions: new TcpTransportOptions { UseVectoredSend = true },
            maxPayloadLength: maxPayloadLength,
            decoder: new FixedHeaderFrameDecoder(framing),
            encoder: encoder);
    }

    [Fact]
    public async Task LargePayload_ManySegments_RoundTrips()
    {
        // 200KB 는 4KB 파이프 블록 ~50개 = gather 목록이 실제로 길어진다.
        // 소켓 송신 버퍼보다 크므로 부분 전송 재개 경로도 반드시 밟는다.
        await using TestHarness harness = await StartVectoredEchoAsync(maxPayloadLength: 256 * 1024);
        await using IConnection connection = await harness.ConnectAsync();

        byte[] payload = new byte[200_000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await harness.SendAsync(connection, EchoMessageId, payload);
        (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task PipelinedFrames_PreserveOrderAndContent()
    {
        // 응답이 송신 파이프에 뭉쳐 다중 세그먼트 배치가 되는 조건 — 벡터드 송신이
        // 프레임 경계를 흐트러뜨리면 여기서 순서·내용이 깨진다.
        await using TestHarness harness = await StartVectoredEchoAsync(maxPayloadLength: 4096);
        await using IConnection connection = await harness.ConnectAsync();

        const int FrameCount = 300;
        for (int i = 0; i < FrameCount; i++)
        {
            await harness.SendAsync(connection, EchoMessageId, BitConverter.GetBytes(i));
        }

        for (int i = 0; i < FrameCount; i++)
        {
            (_, byte[] echoed) = await harness.ReceiveAsync(connection, TestTimeout.Token);
            Assert.Equal(i, BitConverter.ToInt32(echoed));
        }
    }
}
