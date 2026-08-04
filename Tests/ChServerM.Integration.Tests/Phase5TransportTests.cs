using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
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
/// Phase 5 잔여 항목의 계약 검증 — idle timeout, 거부 이유 통지, 종료 레이스.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 셋 다 "정상 경로에서는 절대 안 보이는" 동작이다 — half-open 방치,
/// 상한 거부의 무언 RST, 디스패치 중 상대 소멸. 레거시는 셋 다 실전에서 맞고 나서야
/// 땜질했다(1초 지연 타이머 등). 여기서는 발생 조건을 재현해 계약으로 고정한다.
/// </para>
/// <para>타이밍 의존을 줄이기 위해 판정은 전부 "결과 관측"(스트림 종료·프레임 수신·
/// 커넥션 수 0)이고, sleep 후 부재 단언은 쓰지 않는다.</para>
/// </remarks>
public sealed class Phase5TransportTests
{
    private const ushort EchoMessageId = 100;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// idle 타임아웃을 넘긴 무활동 커넥션은 서버가 끊는다 — half-open 이 영원히
    /// 목록에 남는 것을 막는 장치다.
    /// </summary>
    [Fact]
    public async Task IdleConnection_IsAbortedAfterIdleTimeout()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo()),
            kind: TransportKind.Tcp,
            tcpOptions: new TcpTransportOptions
            {
                // 스윕 주기의 하한이 1초이므로 판정은 1~2.5초 사이에 난다.
                IdleTimeout = TimeSpan.FromSeconds(1),
            });

        await using IConnection connection = await harness.ConnectAsync();

        // 활동이 있는 동안은 끊기지 않아야 한다 — 왕복 한 번으로 확인.
        await harness.SendAsync(connection, EchoMessageId, [1]);
        _ = await harness.ReceiveAsync(connection, timeout.Token);

        // 이후 아무것도 보내지 않는다. 서버의 idle 스윕이 끊으면
        // 클라이언트는 스트림 종료를 관측한다.
        Stopwatch waited = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, timeout.Token));

        // 판정이 타임아웃(1초)보다 빨리 나면 활동 중인 커넥션도 끊는다는 뜻이다.
        Assert.True(
            waited.Elapsed >= TimeSpan.FromSeconds(0.9),
            $"idle 판정이 너무 빨랐다: {waited.Elapsed}");

        await WaitForZeroAsync(harness, timeout.Token);
    }

    /// <summary>
    /// 동시 접속 상한 거부 시 <see cref="FrameworkMessageIds.ConnectionRejected"/> 통지가
    /// 먼저 온다 — 클라이언트가 "서버가 꽉 찼다"와 "네트워크 단절"을 구분할 수 있어야
    /// 재시도 정책이 성립한다.
    /// </summary>
    [Fact]
    public async Task RejectedConnection_ReceivesRejectionNotice_BeforeClose()
    {
        using CancellationTokenSource timeout = new(TestTimeout);

        // 조립하는 쪽이 자기 인코더로 통지 프레임을 만든다 — 전송은 프레이밍을 모른다(축 독립).
        FixedHeaderFrameEncoder encoder = new(4096);
        ArrayBufferWriter<byte> notice = new(FrameHeader.Size);
        encoder.WriteHeader(notice, encoder.CreateHeader(
            FrameworkMessageIds.ConnectionRejected, payloadLength: 0, FrameFlags.None, sequence: 0));

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), Echo()),
            kind: TransportKind.Tcp,
            tcpOptions: new TcpTransportOptions
            {
                MaxConnections = 1,
                RejectionNotice = notice.WrittenSpan.ToArray(),
            });

        // 첫 커넥션이 유일한 자리를 차지한다. 왕복으로 등록 완료까지 확인한다.
        await using IConnection first = await harness.ConnectAsync();
        await harness.SendAsync(first, EchoMessageId, [1]);
        _ = await harness.ReceiveAsync(first, timeout.Token);

        // 두 번째는 TCP 수락 후 통지를 받고 닫힌다.
        await using IConnection second = await harness.ConnectAsync();
        (FrameHeader header, _) = await harness.ReceiveAsync(second, timeout.Token);

        Assert.Equal(FrameworkMessageIds.ConnectionRejected, header.MessageId);

        // 통지 뒤에는 스트림이 끝난다 — 통지는 예외가 아니라 종료의 서곡이다.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(second, timeout.Token));
    }

    /// <summary>
    /// 디스패치가 진행 중일 때 클라이언트가 소멸해도(레거시의 "로그인 완료 전 끊김" 레이스)
    /// 서버는 예외 없이 정리를 완료한다 — 레거시는 이것을 1초 지연 타이머로 때웠다.
    /// </summary>
    [Fact]
    public async Task ClientVanishing_MidDispatch_CleansUpStructurally()
    {
        using CancellationTokenSource timeout = new(TestTimeout);
        using SemaphoreSlim handlerEntered = new(0);

        // 핸들러가 일부러 느리다 — 처리 도중 상대가 사라지는 창을 만든다.
        MessageDelegate slow = async context =>
        {
            handlerEntered.Release();
            await Task.Delay(300).ConfigureAwait(false);

            // 이 시점에 상대는 이미 없다. 쓰기는 조용히 실패해야 하고(FlushResult),
            // 예외로 서버를 흔들면 안 된다.
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

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoMessageId), slow),
            kind: TransportKind.Tcp,
            tcpOptions: new TcpTransportOptions
            {
                // 즉시 RST — FIN 드레인 없이 "전원이 뽑힌" 소멸을 재현한다.
                LingerSeconds = 0,
            });

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);

        // 핸들러가 확실히 프레임을 물고 있을 때 끊는다 — 이것이 레이스의 본질이다.
        Assert.True(await handlerEntered.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token));
        connection.Abort(new ConnectionCloseInfo(CloseReason.ClientClosed));
        await connection.DisposeAsync();

        // 구조적 보장 — 지연 타이머 없이 정리가 완결된다.
        await WaitForZeroAsync(harness, timeout.Token);
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

    private static async Task WaitForZeroAsync(TestHarness harness, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (harness.ServerConnectionCount != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(10),
                $"커넥션 정리가 끝나지 않았다. 잔여: {harness.ServerConnectionCount}");

            await Task.Delay(20, cancellationToken);
        }
    }
}
