using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Security;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 인증 미들웨어(T-20)와 리플레이 가드(T-05)의 종단 검증.
/// </summary>
/// <remarks>
/// <para>고정하는 것:</para>
/// <list type="bullet">
///   <item><description>오답 자격 = <b>응답 없이 커넥션 종료</b>(6000) — 레거시 "검증 결과
///   무시"(WRONG_PW return 주석)의 역. 실패 후 서버는 다음 커넥션을 정상 수용한다</description></item>
///   <item><description>정답 자격 = 상태 전이(GrantedStates 대체) → 특권 메시지 통과.
///   전이는 미들웨어가 하고 핸들러는 응답만 한다 — 핸들러가 전이를 빠뜨릴 수 없다</description></item>
///   <item><description>같은 토큰의 <b>두 번째 커넥션</b> = 거부 — 커넥션 내 리플레이는 TLS 몫,
///   크로스 커넥션은 이 계층 몫이라는 ADR-0017 결정 4의 분담을 실증</description></item>
///   <item><description>조립 순서 오류(인증이 상태 필터보다 바깥) = 조립 시점 예외</description></item>
/// </list>
/// <para>이 파일의 조립(필터 → 인증 → 핸들러)이 인증 조립의 참조 구현이다.</para>
/// </remarks>
public sealed class AuthenticationTests
{
    private const ushort LoginId = 300;
    private const ushort PrivilegedId = 301;

    private const uint StateNew = 1;
    private const uint StateAuthenticated = 2;

    private static readonly byte[] ValidToken = [0xAA, 0xBB, 0xCC, 0xDD];

    /// <summary>화이트리스트(바깥) → 인증(안) → 에코 핸들러 — 인증 조립의 정석 순서.</summary>
    private static Action<MessageDispatcherBuilder> CreateDispatcher(ITokenReplayGuard guard) => dispatcher =>
    {
        FixedHeaderFrameEncoder encoder = new(new FramingOptions { MaxPayloadLength = 4096 });

        dispatcher
            .Use(new MessageStateFilterMiddleware(new MessageStateFilterOptions
            {
                InitialStates = StateNew,
            }
                .Allow(new MessageId(LoginId), StateNew)
                .Allow(new MessageId(PrivilegedId), StateAuthenticated)))
            .Use(new AuthenticationMiddleware(
                new AuthenticationOptions { CredentialMessageId = new MessageId(LoginId) },
                new TokenAuthenticator(guard)))
            .MapRaw(new MessageId(LoginId), async context =>
            {
                // 상태 전이는 미들웨어가 이미 끝냈다 — 핸들러는 성공 응답만 담당한다.
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
    };

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Wrong_credential_closes_connection_and_server_survives(TransportKind kind)
    {
        InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());
        await using TestHarness harness = await TestHarness.StartAsync(CreateDispatcher(guard), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // 1. 오답 자격 — 응답 없이 커넥션이 닫혀야 한다(RejectedByAuthentication → 6000).
        IConnection wrong = await harness.ConnectAsync();
        await harness.SendAsync(wrong, LoginId, new byte[] { 0x00, 0x00, 0x00, 0x00 });
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(wrong, timeout.Token));

        // 2. 실패 이후에도 서버는 정답 자격을 정상 수용한다 — 실패 격리.
        IConnection right = await harness.ConnectAsync();
        await harness.SendAsync(right, LoginId, ValidToken);
        (MessageEnvelope envelope, byte[] echo) = await harness.ReceiveAsync(right, timeout.Token);
        Assert.Equal(LoginId, envelope.MessageId.Value);
        Assert.Equal(ValidToken, echo);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Valid_login_transitions_state_and_unlocks_privileged(TransportKind kind)
    {
        InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());
        await using TestHarness harness = await TestHarness.StartAsync(CreateDispatcher(guard), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        IConnection connection = await harness.ConnectAsync();

        // 1. 인증 전 특권 메시지는 필터가 막는다 — 별도 커넥션으로 확인(이 커넥션은 닫힌다).
        await harness.SendAsync(connection, PrivilegedId, new byte[] { 1 });
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(connection, timeout.Token));

        // 2. 정답 로그인 → 미들웨어가 전이 → 특권 메시지 통과.
        IConnection authed = await harness.ConnectAsync();
        await harness.SendAsync(authed, LoginId, ValidToken);
        (MessageEnvelope loginEnvelope, _) = await harness.ReceiveAsync(authed, timeout.Token);
        Assert.Equal(LoginId, loginEnvelope.MessageId.Value);

        byte[] payload = [7, 8, 9];
        await harness.SendAsync(authed, PrivilegedId, payload);
        (MessageEnvelope privilegedEnvelope, byte[] echo) = await harness.ReceiveAsync(authed, timeout.Token);
        Assert.Equal(PrivilegedId, privilegedEnvelope.MessageId.Value);
        Assert.Equal(payload, echo);

        // 3. 전이 후 로그인 재시도는 필터가 거부한다(StateAuthenticated 에 로그인 비트 없음).
        await harness.SendAsync(authed, LoginId, ValidToken);
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(authed, timeout.Token));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Replayed_token_on_second_connection_is_rejected(TransportKind kind)
    {
        // T-05 의 실증 — 커넥션 안의 리플레이는 TLS 레코드 계층이 막지만(ADR-0017 결정 4),
        // 캡처한 토큰을 새 커넥션에서 재사용하는 경로는 이 계층이 막아야 한다.
        InMemoryTokenReplayGuard guard = new(new TokenReplayGuardOptions());
        await using TestHarness harness = await TestHarness.StartAsync(CreateDispatcher(guard), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // 1. 첫 커넥션 — 정상 로그인.
        IConnection first = await harness.ConnectAsync();
        await harness.SendAsync(first, LoginId, ValidToken);
        (MessageEnvelope envelope, _) = await harness.ReceiveAsync(first, timeout.Token);
        Assert.Equal(LoginId, envelope.MessageId.Value);

        // 2. 같은 토큰으로 두 번째 커넥션 — 응답 없이 닫혀야 한다.
        IConnection replay = await harness.ConnectAsync();
        await harness.SendAsync(replay, LoginId, ValidToken);
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(replay, timeout.Token));
    }

    // ── 조립 시점 검증 ────────────────────────────────────────

    [Fact]
    public void Authentication_registered_before_state_filter_is_rejected_at_build()
    {
        // 순서가 뒤집히면 인증 성공 직후 전이된 상태에서 필터가 자격 메시지를 거부한다 —
        // "성공했는데 닫히는" 런타임 미스터리 대신 조립 시점 예외로 잡는다.
        MessageDispatcherBuilder dispatcher = new MessageDispatcherBuilder()
            .Use(new AuthenticationMiddleware(
                new AuthenticationOptions { CredentialMessageId = new MessageId(LoginId) },
                new TokenAuthenticator(new InMemoryTokenReplayGuard(new TokenReplayGuardOptions()))))
            .Use(new MessageStateFilterMiddleware(new MessageStateFilterOptions
            {
                InitialStates = StateNew,
            }.Allow(new MessageId(LoginId), StateNew)));

        Assert.Throws<InvalidOperationException>(() => dispatcher.Build());
    }

    [Fact]
    public void Authentication_without_state_filter_is_a_valid_assembly()
    {
        // 화이트리스트 없는 선택 인증(게스트 플레이 등)은 정당한 조립이다(ADR-0004).
        MessageDispatcher dispatcher = new MessageDispatcherBuilder()
            .Use(new AuthenticationMiddleware(
                new AuthenticationOptions { CredentialMessageId = new MessageId(LoginId) },
                new TokenAuthenticator(new InMemoryTokenReplayGuard(new TokenReplayGuardOptions()))))
            .Build();

        Assert.NotNull(dispatcher);
    }

    [Fact]
    public void Sentinel_credential_message_id_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            static () => new AuthenticationMiddleware(
                new AuthenticationOptions(),
                new TokenAuthenticator(new InMemoryTokenReplayGuard(new TokenReplayGuardOptions()))));
    }

    /// <summary>
    /// 테스트용 인증기 — 고정 토큰 일치 + 리플레이 가드. 계약의 순서 규약
    /// (<b>검증 먼저, 클레임은 마지막</b>)을 지키는 참조 구현이기도 하다.
    /// </summary>
    private sealed class TokenAuthenticator : IAuthenticator
    {
        private readonly ITokenReplayGuard _guard;

        public TokenAuthenticator(ITokenReplayGuard guard) => _guard = guard;

        public ValueTask<AuthenticationResult> AuthenticateAsync(MessageContext context)
        {
            // 페이로드는 반환 시점에 무효가 된다 — await 를 넘기 전에 복사한다(계약).
            byte[] token = context.Payload.ToArray();

            if (!token.AsSpan().SequenceEqual(ValidToken))
            {
                return ValueTask.FromResult(AuthenticationResult.Failure("토큰 불일치"));
            }

            // 검증이 전부 통과한 뒤에만 클레임한다 — 순서를 뒤집으면 쓰레기 토큰으로
            // 유계 가드를 포화시킬 수 있다(ITokenReplayGuard 계약).
            if (!_guard.TryClaim(token))
            {
                return ValueTask.FromResult(AuthenticationResult.Failure("토큰 재사용(T-05)"));
            }

            return ValueTask.FromResult(AuthenticationResult.Success(StateAuthenticated));
        }
    }
}
