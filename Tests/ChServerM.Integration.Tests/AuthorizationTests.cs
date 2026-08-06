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
/// 인가 미들웨어(T-21)의 종단 검증 — 자원 수준 판정(소유자 검사)이 핸들러 도달 전에
/// 일어나고, 거부 처리가 인증과 의도적으로 다름(옵션 게이트)을 고정한다.
/// </summary>
/// <remarks>
/// <para>고정하는 것:</para>
/// <list type="bullet">
///   <item><description>소유자 불일치 = 거부. 기본(<c>CloseOnPolicyRejection=false</c>)에서는
///   커넥션이 살아 있고 응답만 없다 — 이후 정당한 요청은 정상 처리(속도 제한류 거부가
///   재접속 폭풍을 만들지 않는 설계). 옵션을 켜면 6001 종료</description></item>
///   <item><description>보호 목록 밖 메시지는 정책이 호출조차 되지 않는다 —
///   기본 거부 경계는 T-19 필터 단일 유지</description></item>
///   <item><description>조립 순서(필터 → 인증 → 인가) 위반과 죽은 조립(빈 보호 목록)은
///   조립 시점 예외</description></item>
/// </list>
/// <para>이 파일의 조립이 인가 조립의 참조 구현이다: 신원은 인증기가 커넥션 피처로
/// 남기고, 정책은 그 피처와 페이로드를 함께 본다.</para>
/// </remarks>
public sealed class AuthorizationTests
{
    private const ushort LoginId = 400;
    private const ushort ModifyObjectId = 401;  // 보호 대상 — 페이로드[0] = 대상 소유자
    private const ushort ChatId = 402;          // 비보호 — 정책을 거치지 않는다

    private const uint StateNew = 1;
    private const uint StateAuthenticated = 2;

    /// <summary>토큰 첫 바이트가 곧 소유자 ID 인 테스트 자격 체계.</summary>
    private static readonly byte[] OwnerToken = [0xA1, 0x01, 0x02, 0x03];

    private static Action<MessageDispatcherBuilder> CreateDispatcher(
        IAuthorizationPolicy policy)
        => dispatcher =>
    {
        FixedHeaderFrameEncoder encoder = new(new FramingOptions { MaxPayloadLength = 4096 });

        dispatcher
            .Use(new MessageStateFilterMiddleware(new MessageStateFilterOptions
            {
                InitialStates = StateNew,
            }
                .Allow(new MessageId(LoginId), StateNew)
                .Allow(new MessageId(ModifyObjectId), StateAuthenticated)
                .Allow(new MessageId(ChatId), StateAuthenticated)))
            .Use(new AuthenticationMiddleware(
                new AuthenticationOptions { CredentialMessageId = new MessageId(LoginId) },
                new OwnerAuthenticator()))
            .Use(new AuthorizationMiddleware(
                new AuthorizationOptions().Protect(new MessageId(ModifyObjectId)),
                policy))
            .MapRaw(new MessageId(LoginId), Echo(encoder))
            .MapRaw(new MessageId(ModifyObjectId), Echo(encoder))
            .MapRaw(new MessageId(ChatId), Echo(encoder));
    };

    private static MessageDelegate Echo(FixedHeaderFrameEncoder encoder) => async context =>
    {
        await FrameWriter.WriteFrameAsync(
            context.Connection.Output, encoder, context.Envelope.MessageId, context.Payload,
            FrameFlags.None, context.Envelope.Sequence, context.CancellationToken).ConfigureAwait(false);
        return DispatchStatus.Handled;
    };

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Owner_mismatch_is_denied_without_closing_by_default(TransportKind kind)
    {
        OwnerPolicy policy = new();
        await using TestHarness harness = await TestHarness.StartAsync(CreateDispatcher(policy), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, LoginId, OwnerToken);
        _ = await harness.ReceiveAsync(connection, timeout.Token);

        // 1. 남의 오브젝트(소유자 0x77) 수정 시도 — 거부. 기본값에서는 커넥션이 살아 있다.
        await harness.SendAsync(connection, ModifyObjectId, new byte[] { 0x77, 9, 9 });

        // 2. 곧바로 자기 오브젝트(0xA1) 수정 — 정상 처리돼야 한다. 받은 응답이 이 요청의
        //    에코라는 것이 "거부된 요청에는 응답이 없었고 커넥션은 살아 있다"의 증명이다.
        byte[] mine = [0xA1, 1, 2];
        await harness.SendAsync(connection, ModifyObjectId, mine);
        (MessageEnvelope envelope, byte[] echo) = await harness.ReceiveAsync(connection, timeout.Token);

        Assert.Equal(ModifyObjectId, envelope.MessageId.Value);
        Assert.Equal(mine, echo);
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Owner_mismatch_closes_connection_when_option_enabled(TransportKind kind)
    {
        OwnerPolicy policy = new();
        FramedConnectionOptions strict = new() { CloseOnPolicyRejection = true };
        await using TestHarness harness =
            await TestHarness.StartAsync(CreateDispatcher(policy), kind, connectionOptions: strict);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, LoginId, OwnerToken);
        _ = await harness.ReceiveAsync(connection, timeout.Token);

        await harness.SendAsync(connection, ModifyObjectId, new byte[] { 0x77 });

        // 엄격 조립에서는 거부가 곧 종료(RejectedByPolicy → 6001)다.
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.ReceiveAsync(connection, timeout.Token));
    }

    [Theory]
    [InlineData(TransportKind.InMemory)]
    [InlineData(TransportKind.Tcp)]
    public async Task Unprotected_message_never_consults_policy(TransportKind kind)
    {
        OwnerPolicy policy = new();
        await using TestHarness harness = await TestHarness.StartAsync(CreateDispatcher(policy), kind);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, LoginId, OwnerToken);
        _ = await harness.ReceiveAsync(connection, timeout.Token);

        // 비보호 메시지 — 소유자 불일치 페이로드라도 정책 없이 통과해야 한다.
        byte[] chat = [0x77, 7, 7];
        await harness.SendAsync(connection, ChatId, chat);
        (MessageEnvelope envelope, byte[] echo) = await harness.ReceiveAsync(connection, timeout.Token);

        Assert.Equal(ChatId, envelope.MessageId.Value);
        Assert.Equal(chat, echo);
        Assert.Equal(0, policy.CallCount);
    }

    // ── 조립 시점 검증 ────────────────────────────────────────

    [Fact]
    public void Empty_protect_list_is_a_dead_assembly()
    {
        Assert.Throws<InvalidOperationException>(
            static () => new AuthorizationMiddleware(new AuthorizationOptions(), new OwnerPolicy()));
    }

    [Fact]
    public void Sentinel_and_duplicate_protect_entries_are_rejected()
    {
        Assert.Throws<ArgumentException>(
            static () => new AuthorizationOptions().Protect(MessageId.None));

        AuthorizationOptions options = new AuthorizationOptions().Protect(new MessageId(ModifyObjectId));
        Assert.Throws<ArgumentException>(() => options.Protect(new MessageId(ModifyObjectId)));
    }

    [Fact]
    public void Authorization_before_authentication_is_rejected_at_build()
    {
        // 인가는 인증기가 등록한 신원 피처를 읽는다 — 인증보다 바깥이면 신원 없이 판정된다.
        MessageDispatcherBuilder dispatcher = new MessageDispatcherBuilder()
            .Use(new AuthorizationMiddleware(
                new AuthorizationOptions().Protect(new MessageId(ModifyObjectId)), new OwnerPolicy()))
            .Use(new AuthenticationMiddleware(
                new AuthenticationOptions { CredentialMessageId = new MessageId(LoginId) },
                new OwnerAuthenticator()));

        Assert.Throws<InvalidOperationException>(() => dispatcher.Build());
    }

    [Fact]
    public void Authorization_before_state_filter_is_rejected_at_build()
    {
        MessageDispatcherBuilder dispatcher = new MessageDispatcherBuilder()
            .Use(new AuthorizationMiddleware(
                new AuthorizationOptions().Protect(new MessageId(ModifyObjectId)), new OwnerPolicy()))
            .Use(new MessageStateFilterMiddleware(new MessageStateFilterOptions
            {
                InitialStates = StateNew,
            }.Allow(new MessageId(LoginId), StateNew)));

        Assert.Throws<InvalidOperationException>(() => dispatcher.Build());
    }

    [Fact]
    public void Canonical_order_builds()
    {
        MessageDispatcher dispatcher = new MessageDispatcherBuilder()
            .Use(new MessageStateFilterMiddleware(new MessageStateFilterOptions
            {
                InitialStates = StateNew,
            }.Allow(new MessageId(LoginId), StateNew)))
            .Use(new AuthenticationMiddleware(
                new AuthenticationOptions { CredentialMessageId = new MessageId(LoginId) },
                new OwnerAuthenticator()))
            .Use(new AuthorizationMiddleware(
                new AuthorizationOptions().Protect(new MessageId(ModifyObjectId)), new OwnerPolicy()))
            .Build();

        Assert.NotNull(dispatcher);
    }

    /// <summary>인증된 소유자 ID 를 커넥션에 남기는 앱 정의 신원 피처.</summary>
    private sealed class OwnerFeature
    {
        public byte OwnerId { get; init; }
    }

    /// <summary>토큰 첫 바이트를 소유자 ID 로 삼는 테스트 인증기.</summary>
    private sealed class OwnerAuthenticator : IAuthenticator
    {
        public ValueTask<AuthenticationResult> AuthenticateAsync(MessageContext context)
        {
            byte[] token = context.Payload.ToArray();
            if (token.Length == 0)
            {
                return ValueTask.FromResult(AuthenticationResult.Failure("빈 자격"));
            }

            // 신원은 인증기가 커넥션 피처로 남긴다 — 인가 정책의 입력이 된다.
            context.Connection.Features.Set(new OwnerFeature { OwnerId = token[0] });
            return ValueTask.FromResult(AuthenticationResult.Success(StateAuthenticated));
        }
    }

    /// <summary>페이로드 첫 바이트(대상 소유자)와 신원의 소유자 ID 를 대조하는 자원 수준 정책.</summary>
    private sealed class OwnerPolicy : IAuthorizationPolicy
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<AuthorizationDecision> AuthorizeAsync(MessageContext context)
        {
            Interlocked.Increment(ref _callCount);

            OwnerFeature? owner = context.Connection.Features.Get<OwnerFeature>();
            if (owner is null)
            {
                return ValueTask.FromResult(AuthorizationDecision.Deny("신원 없음"));
            }

            ReadOnlySequence<byte> payload = context.Payload;
            if (payload.Length == 0 || payload.FirstSpan[0] != owner.OwnerId)
            {
                return ValueTask.FromResult(AuthorizationDecision.Deny("소유자 불일치"));
            }

            return ValueTask.FromResult(AuthorizationDecision.Allow());
        }
    }
}
