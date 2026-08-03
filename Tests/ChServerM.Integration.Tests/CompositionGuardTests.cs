using System;
using System.Net;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 축 하나하나가 유효해도 조합이 성립하지 않을 수 있다. 그 경우를 조립 시점에 막는지 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 검사가 없을 때의 증상이 최악이다.</b> 작은 프레임은 멀쩡히 오가다가 큰 메시지
/// 하나에서만 멈추고, 예외도 로그도 없다. 커넥션이 그냥 응답하지 않는다.
/// </para>
/// <para>
/// 실제로 이 프로젝트에서 발견된 결함이다 — 200KB 페이로드 테스트가 인메모리 전송에서
/// 교착했고, TCP 에서는 커널 소켓 버퍼가 여유분을 흡수해 <b>우연히</b> 통과했다.
/// </para>
/// </remarks>
public sealed class CompositionGuardTests
{
    private static MessageDelegate NoOp => _ => ValueTask.FromResult(DispatchStatus.Handled);

    private static ServerBuilder BuilderFor(
        ChServerM.Transports.IServerTransport transport,
        int maxPayloadLength)
    {
        FramingOptions framing = new() { MaxPayloadLength = maxPayloadLength };

        return new ServerBuilder()
            .UseTransport(transport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(dispatcher => dispatcher.MapRaw(new MessageId(1), NoOp));
    }

    [Fact]
    public async Task Build_Throws_WhenFrameCannotFitInTheInMemoryBuffer()
    {
        InMemoryTransportOptions transport = new()
        {
            PauseWriterThreshold = 64 * 1024,
            ResumeWriterThreshold = 32 * 1024,
        };

        await using InMemoryServerTransport server =
            new(new InMemoryTransportHub(), new InMemoryEndPoint("guard"), transport);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => BuilderFor(server, maxPayloadLength: 256 * 1024).Build());

        // 메시지가 원인과 해결책을 모두 담아야 한다. "잘못된 설정"만으로는 쓸모없다.
        Assert.Contains("PauseWriterThreshold", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxPayloadLength", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_Throws_WhenFrameCannotFitInTheTcpBuffer()
    {
        // TCP 는 커널 버퍼 덕분에 우연히 통과할 수 있다. 그 우연에 기대지 않는다.
        TcpTransportOptions transport = new()
        {
            PauseWriterThreshold = 64 * 1024,
            ResumeWriterThreshold = 32 * 1024,
        };

        await using TcpServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), transport);

        Assert.Throws<InvalidOperationException>(
            () => BuilderFor(server, maxPayloadLength: 256 * 1024).Build());
    }

    [Fact]
    public async Task Build_Throws_OnTheDefaultCombination()
    {
        // 프레이밍 기본값(1 MiB)과 전송 기본값(64 KiB)은 서로 맞지 않는다.
        // 기본값을 그대로 쓰면 조립 시점에 걸려야 한다 — 조용히 넘어가면
        // 64KB 넘는 첫 메시지에서 교착한다.
        await using InMemoryServerTransport server =
            new(new InMemoryTransportHub(), new InMemoryEndPoint("guard-default"));

        Assert.Throws<InvalidOperationException>(
            () => BuilderFor(server, FramingOptions.DefaultMaxPayloadLength).Build());
    }

    [Fact]
    public async Task Build_Succeeds_WhenBuffersAreLargeEnough()
    {
        const int MaxPayload = 256 * 1024;

        InMemoryTransportOptions transport = new()
        {
            PauseWriterThreshold = 2 * (MaxPayload + FrameHeader.Size),
            ResumeWriterThreshold = MaxPayload + FrameHeader.Size,
        };

        await using InMemoryServerTransport server =
            new(new InMemoryTransportHub(), new InMemoryEndPoint("guard-ok"), transport);

        await using ChServerMServer built = BuilderFor(server, MaxPayload).Build();

        Assert.NotNull(built);
    }

    [Fact]
    public async Task Build_Succeeds_AtTheExactBoundary()
    {
        // 경계에서 off-by-one 으로 막으면 정당한 조합이 거부된다.
        const int MaxPayload = 4096;
        int exact = MaxPayload + FrameHeader.Size;

        InMemoryTransportOptions transport = new()
        {
            PauseWriterThreshold = exact,
            ResumeWriterThreshold = exact / 2,
        };

        await using InMemoryServerTransport server =
            new(new InMemoryTransportHub(), new InMemoryEndPoint("guard-boundary"), transport);

        await using ChServerMServer built = BuilderFor(server, MaxPayload).Build();

        Assert.NotNull(built);
    }

    [Fact]
    public async Task ClientBuilder_AppliesTheSameGuard()
    {
        // 클라이언트에서도 같은 교착이 난다. 한쪽만 막으면 절반만 안전하다.
        TcpTransportOptions transport = new()
        {
            PauseWriterThreshold = 64 * 1024,
            ResumeWriterThreshold = 32 * 1024,
        };

        await using TcpClientTransport client = new(transport);
        FramingOptions framing = new() { MaxPayloadLength = 256 * 1024 };

        Assert.Throws<InvalidOperationException>(
            () => new ClientBuilder()
                .UseTransport(client)
                .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
                .Build());
    }
}
