using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 상태별 메시지 화이트리스트(T-19)의 종단 검증 — <b>Phase 9 게이트 조건</b>
/// "인증 전 패킷이 차단됨이 테스트로 확인될 때"를 고정하는 테스트다.
/// </summary>
/// <remarks>
/// 시나리오는 레거시 결함의 역이다: <c>AllowedPkState</c>는 기본 전부 허용이라
/// 미인증 커넥션이 특권 패킷을 보낼 수 있었다. 여기서는 (1) 인증 전 특권 메시지 =
/// 커넥션 종료, (2) 등록되지 않은 메시지 = 기본 거부, (3) 핸들러의 상태 전이 후에만
/// 특권 메시지가 통과함을 두 전송 모두에서 확인한다.
/// </remarks>
public sealed class StateFilterTests
{
    private const ushort LoginId = 200;
    private const ushort PrivilegedId = 201;
    private const ushort UnregisteredId = 999;

    /// <summary>연결 직후 상태(비트0)·인증됨 상태(비트1) — 의미는 이 테스트(앱 역할)가 정의한다.</summary>
    private const uint StateNew = 1;
    private const uint StateAuthenticated = 2;

    private static MessageStateFilterOptions CreateRules() => new MessageStateFilterOptions
    {
        InitialStates = StateNew,
    }
        .Allow(new MessageId(LoginId), StateNew)
        .Allow(new MessageId(PrivilegedId), StateAuthenticated);

    /// <summary>화이트리스트를 가장 바깥 미들웨어로 + 로그인(상태 전이)·특권 에코 핸들러.</summary>
    private static void ConfigureDispatcher(MessageDispatcherBuilder dispatcher)
    {
        FixedHeaderFrameEncoder encoder = new(new FramingOptions { MaxPayloadLength = 4096 });

        dispatcher
            .Use(new MessageStateFilterMiddleware(CreateRules()))
            .MapRaw(new MessageId(LoginId), async context =>
            {
                // 인증 성공의 상태 전이 — 미들웨어가 이 메시지를 통과시켰으므로 feature 는 반드시 있다.
                context.Connection.Features.Get<IConnectionStateFeature>()!.States = StateAuthenticated;

                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, encoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            })
            .MapRaw(new MessageId(PrivilegedId), async context =>
            {
                await FrameWriter.WriteFrameAsync(
                    context.Connection.Output, encoder, context.Envelope.MessageId, context.Payload,
                    FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
                return DispatchStatus.Handled;
            });
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Privileged_message_before_login_closes_connection(TransportKind kind)
    {
        await using TestHarness harness = await TestHarness.StartAsync(ConfigureDispatcher, kind);
        IConnection connection = await harness.ConnectAsync();

        await harness.SendAsync(connection, PrivilegedId, new byte[] { 1, 2, 3 });

        // 응답 프레임 없이 커넥션이 닫혀야 한다(RejectedByState → MessageNotAllowedInState 종료).
        // 종료의 표현은 전송·플랫폼에 따라 "스트림 끝"(InvalidOperationException) 또는
        // 소켓 예외로 갈리므로 예외 종류는 고정하지 않는다 — 고정하는 것은 "응답이 오지 않는다"다.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(connection, timeout.Token));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Unregistered_message_is_denied_by_default(TransportKind kind)
    {
        await using TestHarness harness = await TestHarness.StartAsync(ConfigureDispatcher, kind);
        IConnection connection = await harness.ConnectAsync();

        // 라우팅에도 화이트리스트에도 없는 식별자 — HandlerNotFound 이전에 기본 거부가 잡는다.
        await harness.SendAsync(connection, UnregisteredId, ReadOnlySpan<byte>.Empty);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(connection, timeout.Token));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Login_transition_unlocks_privileged_messages(TransportKind kind)
    {
        await using TestHarness harness = await TestHarness.StartAsync(ConfigureDispatcher, kind);
        IConnection connection = await harness.ConnectAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // 1. 시작 상태에서 로그인은 허용된다 — 핸들러가 Authenticated 로 전이한다.
        byte[] loginPayload = [10, 11];
        await harness.SendAsync(connection, LoginId, loginPayload);
        (MessageEnvelope loginEnvelope, byte[] loginEcho) = await harness.ReceiveAsync(connection, timeout.Token);
        Assert.Equal(LoginId, loginEnvelope.MessageId.Value);
        Assert.Equal(loginPayload, loginEcho);

        // 2. 전이 후에는 특권 메시지가 통과한다 — 상태가 프레임을 넘어 유지됨의 증명이기도 하다.
        byte[] privilegedPayload = [20, 21, 22];
        await harness.SendAsync(connection, PrivilegedId, privilegedPayload);
        (MessageEnvelope privilegedEnvelope, byte[] privilegedEcho) =
            await harness.ReceiveAsync(connection, timeout.Token);
        Assert.Equal(PrivilegedId, privilegedEnvelope.MessageId.Value);
        Assert.Equal(privilegedPayload, privilegedEcho);

        // 3. 전이 후 시작 상태 전용 메시지(로그인 재시도)는 거부된다 — 화이트리스트는 양방향이다.
        await harness.SendAsync(connection, LoginId, loginPayload);
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(connection, timeout.Token));
    }

    // ── 조립 시점 검증 ────────────────────────────────────────

    [Fact]
    public void Empty_initial_states_is_a_dead_assembly()
    {
        MessageStateFilterOptions options = new() { InitialStates = 0 };
        options.Allow(new MessageId(LoginId), StateNew);

        Assert.Throws<InvalidOperationException>(() => new MessageStateFilterMiddleware(options));
    }

    [Fact]
    public void Zero_state_mask_rule_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            static () => new MessageStateFilterOptions().Allow(new MessageId(LoginId), states: 0));
    }

    [Fact]
    public void Sentinel_message_id_rule_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            static () => new MessageStateFilterOptions().Allow(MessageId.None, StateNew));
    }

    [Fact]
    public void Duplicate_rule_is_rejected()
    {
        MessageStateFilterOptions options = new MessageStateFilterOptions()
            .Allow(new MessageId(LoginId), StateNew);

        Assert.Throws<ArgumentException>(() => options.Allow(new MessageId(LoginId), StateAuthenticated));
    }
}
