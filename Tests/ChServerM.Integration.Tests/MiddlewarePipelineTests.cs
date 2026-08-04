using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 미들웨어가 워크로드별 조립의 실제 수단인지 검증한다. 순서·거부·요청 스코프 상태가
/// 의도대로 동작하지 않으면 인증과 속도 제한을 이 계층에 올릴 수 없다.
/// </summary>
public sealed class MiddlewarePipelineTests
{
    private const ushort EchoMessageId = 100;

    private static MessageDelegate Echo(IFrameEncoder encoder) => async context =>
    {
        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            encoder,
            context.Envelope.MessageId,
            context.Payload,
            FrameFlags.None,
            context.Envelope.Sequence,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    };

    /// <summary>실행 순서를 기록하는 미들웨어.</summary>
    private sealed class RecordingMiddleware(string name, ConcurrentQueue<string> log) : IServerMiddleware
    {
        public async ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
        {
            log.Enqueue($"{name}:before");
            DispatchStatus status = await next(context).ConfigureAwait(false);
            log.Enqueue($"{name}:after");
            return status;
        }
    }

    [Fact]
    public async Task Middleware_RunsInRegistrationOrder_AndUnwindsInReverse()
    {
        // ASP.NET Core 와 같은 멘탈 모델이어야 한다. 다르면 인증→권한 순서 조립이 어긋난다.
        ConcurrentQueue<string> log = new();

        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            .Use(new RecordingMiddleware("outer", log))
            .Use(new RecordingMiddleware("inner", log))
            .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);
        await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(
            ["outer:before", "inner:before", "inner:after", "outer:after"],
            log.ToArray());
    }

    [Fact]
    public async Task Middleware_RunsBeforeRouting_EvenForUnknownMessages()
    {
        // 라우팅이 먼저였다면 모르는 ID 를 보내는 것만으로 인증을 우회할 수 있다.
        ConcurrentQueue<string> log = new();

        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            .Use(new RecordingMiddleware("guard", log))
            .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, 999, [1]);
        await harness.SendAsync(connection, EchoMessageId, [2]);
        await harness.ReceiveAsync(connection, TestTimeout.Token);

        // 등록되지 않은 999 에 대해서도 미들웨어가 돌았어야 한다.
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public async Task Middleware_ThatRejects_StopsTheHandler()
    {
        const ushort ProbeId = 200;
        int handlerCalls = 0;

        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            // EchoMessageId 만 거부한다 — 뒤따르는 프로브 왕복이 순서 동기화 장치가 된다.
            .Use(next => context =>
                context.Envelope.MessageId.Value == EchoMessageId
                    ? ValueTask.FromResult(DispatchStatus.RejectedByPolicy)
                    : next(context))
            .MapRaw(new MessageId(EchoMessageId), context =>
            {
                Interlocked.Increment(ref handlerCalls);
                return ValueTask.FromResult(DispatchStatus.Handled);
            })
            .MapRaw(new MessageId(ProbeId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);

        // "핸들러가 호출되지 않았다"는 부재 증명을 sleep 창에 걸지 않는다(2026-08-04 감사 —
        // 느린 CI 에서 51ms 에 실행돼도 통과하는 거짓 통과). 같은 커넥션의 프레임은
        // 순서대로 처리되므로, 뒤에 보낸 프로브의 응답이 오면 앞 프레임의 처리는
        // 이미 끝난 것이다 — 그 시점의 0 이 진짜 0 이다.
        await harness.SendAsync(connection, ProbeId, [2]);
        _ = await harness.ReceiveAsync(connection, TestTimeout.Token);

        Assert.Equal(0, Volatile.Read(ref handlerCalls));
        Assert.False(connection.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task PolicyRejection_ClosesConnection_WhenConfigured()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder
                .Use(next => _ => ValueTask.FromResult(DispatchStatus.RejectedByPolicy))
                .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))),
            connectionOptions: new FramedConnectionOptions { CloseOnPolicyRejection = true });

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    [Fact]
    public async Task PerMessageFeature_DoesNotLeakToTheNextFrame()
    {
        // 인증 결과가 다음 프레임으로 새면 권한 검사가 무의미해진다.
        ConcurrentQueue<bool> observedLeak = new();

        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            .Use(next => async context =>
            {
                observedLeak.Enqueue(context.Features.Get<IMarker>() is not null);
                context.Features.Set<IMarker>(new Marker());
                return await next(context).ConfigureAwait(false);
            })
            .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        for (int i = 0; i < 3; i++)
        {
            await harness.SendAsync(connection, EchoMessageId, [(byte)i]);
            await harness.ReceiveAsync(connection, TestTimeout.Token);
        }

        Assert.Equal([false, false, false], observedLeak.ToArray());
    }

    [Fact]
    public async Task ConnectionFeature_SurvivesAcrossFrames()
    {
        // 반대쪽 규약: 커넥션 스코프 상태(TLS 정보·인증 주체)는 프레임 경계를 넘어야 한다.
        ConcurrentQueue<bool> seenBefore = new();

        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            .Use(next => async context =>
            {
                seenBefore.Enqueue(context.Connection.Features.Get<IMarker>() is not null);
                context.Connection.Features.Set<IMarker>(new Marker());
                return await next(context).ConfigureAwait(false);
            })
            .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();

        for (int i = 0; i < 3; i++)
        {
            await harness.SendAsync(connection, EchoMessageId, [(byte)i]);
            await harness.ReceiveAsync(connection, TestTimeout.Token);
        }

        Assert.Equal([false, true, true], seenBefore.ToArray());
    }

    [Fact]
    public async Task Middleware_CanAbortTheConnectionDirectly()
    {
        // 인증 실패처럼 즉시 끊어야 하는 경우의 경로.
        await using TestHarness harness = await TestHarness.StartAsync(builder => builder
            .Use(next => context =>
            {
                context.Connection.Abort(new ConnectionCloseInfo(
                    CloseReason.ApplicationError, Diagnostics.ErrorCode.AuthenticationFailed));
                return ValueTask.FromResult(DispatchStatus.RejectedByPolicy);
            })
            .MapRaw(new MessageId(EchoMessageId), Echo(new FixedHeaderFrameEncoder(4096))));

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, EchoMessageId, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReceiveAsync(connection, TestTimeout.Token));
    }

    private interface IMarker
    {
    }

    private sealed class Marker : IMarker
    {
    }
}
