using System;
using System.Buffers;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Identity;
using ChServerM.Sessions;

namespace ChServerM.Hosting.Sessions;

/// <summary>커넥션에 붙는 <see cref="ISessionFeature"/> 의 기본 구현.</summary>
/// <remarks>
/// 단순한 값 보관소다. 스레드 규약은 인터페이스 문서를 따른다 —
/// <b>커넥션의 디스패치 순차 컨텍스트 전용</b>이므로 동기화하지 않는다(9.7: 그 사실을
/// 계약으로 드러내고, 계약이 있으면 안에서 락을 쓰지 않는다).
/// </remarks>
public sealed class SessionFeature : ISessionFeature
{
    /// <inheritdoc/>
    public SessionId SessionId { get; set; }

    /// <inheritdoc/>
    public SessionVersion Version { get; set; }
}

/// <summary>
/// 세션 재개 흐름의 서버 측 배선 — 예약 메시지를 <see cref="SessionResumeService"/> 에 잇는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0036 이 만든 것은 <b>메커니즘</b>이었고 프로토콜이 없었다.
/// 이 타입이 예약 메시지(<see cref="FrameworkMessageIds.SessionResume"/>)를 받아 자격을
/// 검증하고, 커넥션에 세션을 바인딩하고, 회전된 토큰으로 응답한다.
/// </para>
///
/// <para>
/// <b>⚠ 재개는 프레임워크가, 수립은 앱이 한다.</b> 재개는 <b>순수한 메커니즘</b>이다 —
/// 제시된 토큰이 저장된 해시와 맞는가라는 기계적 판정뿐이라 정책이 없다. 반면 <b>수립</b>은
/// "이 사람이 누구이고 세션을 줘도 되는가" 라는 <b>정책</b>이라 앱의 몫이다. 프레임워크는
/// 앱이 그 결정을 내린 뒤 결과를 전달할 수단
/// (<see cref="WriteEstablishedAsync"/>)만 제공한다. 이 경계를 흐리면 인증 정책이 프레임워크로
/// 새어 들어와 ADR-0004 가 깨진다.
/// </para>
///
/// <para>
/// <b>사용법</b> — 앱이 예약 ID 에 매핑한다.
/// </para>
/// <code>
///   var dispatch = new SessionResumeDispatch(resumeService, frameEncoder);
///   builder.ConfigureDispatcher(d =>
///       d.MapRaw(FrameworkMessageIds.SessionResume, dispatch.HandleResumeAsync));
/// </code>
///
/// <para>
/// <b>스레드 규약.</b> 상태가 없다. 커넥션별 상태는 전부
/// <see cref="ISessionFeature"/> 에 있다.
/// </para>
/// </remarks>
public sealed class SessionResumeDispatch
{
    private readonly SessionResumeService _service;
    private readonly IFrameEncoder _encoder;

    /// <summary>재개 배선을 만든다.</summary>
    /// <param name="service">재개 서비스.</param>
    /// <param name="encoder">응답 프레임 인코더.</param>
    /// <exception cref="ArgumentNullException">필수 인자가 <see langword="null"/> 이다.</exception>
    public SessionResumeDispatch(SessionResumeService service, IFrameEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(encoder);

        _service = service;
        _encoder = encoder;
    }

    /// <summary>
    /// <see cref="FrameworkMessageIds.SessionResume"/> 프레임을 처리한다.
    /// </summary>
    /// <param name="context">메시지 컨텍스트.</param>
    /// <returns>디스패치 결과.</returns>
    /// <remarks>
    /// <para>
    /// <b>어떤 경로로도 응답을 보낸다.</b> 형식 오류·자격 불일치·세션 없음이 모두 같은
    /// <see cref="SessionResumeStatus.Rejected"/> 응답이 된다 — 사유를 알려 주면 공격자가
    /// 실재하는 세션 식별자를 열거할 수 있다(ADR-0036).
    /// </para>
    /// <para>
    /// <b>응답하지 않고 끊지 않는 이유</b>: 클라이언트가 "거부됐다" 와 "네트워크가 끊겼다" 를
    /// 구분할 수 없으면 재시도 정책을 세울 수 없다(<see cref="FrameworkMessageIds.ConnectionRejected"/>
    /// 가 존재하는 이유와 같다).
    /// </para>
    /// </remarks>
    public async ValueTask<DispatchStatus> HandleResumeAsync(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long rawSessionId = 0;
        byte[] tokenBuffer = new byte[SessionHandshakeCodec.TokenLength];
        bool wellFormed = SessionHandshakeCodec.TryReadResumeRequest(
            context.Payload.IsSingleSegment ? context.Payload.FirstSpan : context.Payload.ToArray(),
            out rawSessionId,
            tokenBuffer);

        if (!wellFormed)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return DispatchStatus.Handled;
        }

        SessionId sessionId = new(new ObjectId(rawSessionId));
        SessionResumeToken presented = SessionResumeToken.FromBytes(tokenBuffer);

        ArrayBufferWriter<byte> state = new();
        SessionResumeResult result = await _service
            .TryResumeAsync(sessionId, presented, state, context.CancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return DispatchStatus.Handled;
        }

        // ⚠ 바인딩을 먼저 세운다 — 응답 전에 해야 이 프레임 이후의 핸들러가 올바른 세션을 본다.
        ISessionFeature feature = GetOrCreateFeature(context);
        feature.SessionId = sessionId;
        feature.Version = result.Version;

        byte[] payload = new byte[SessionHandshakeCodec.ResumeResponseSize];
        Span<byte> rotated = stackalloc byte[SessionHandshakeCodec.TokenLength];
        result.RotatedToken.CopyTo(rotated);
        SessionHandshakeCodec.WriteResumeResponse(payload, SessionResumeStatus.Resumed, rotated);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            _encoder,
            FrameworkMessageIds.SessionResumed,
            payload,
            FrameFlags.None,
            sequence: 0,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    }

    /// <summary>
    /// 앱이 세션을 수립한 뒤 클라이언트에 식별자와 최초 토큰을 알린다.
    /// </summary>
    /// <param name="context">메시지 컨텍스트.</param>
    /// <param name="sessionId">수립된 세션.</param>
    /// <param name="binding">수립 결과(버전·최초 토큰).</param>
    /// <returns>비동기 작업.</returns>
    /// <remarks>
    /// <b>커넥션 바인딩도 함께 세운다</b> — 앱이 그것을 빠뜨리면 이후 쓰기가 기대 버전을
    /// 알 수 없다.
    /// </remarks>
    public async ValueTask WriteEstablishedAsync(
        MessageContext context, SessionId sessionId, SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(context);

        ISessionFeature feature = GetOrCreateFeature(context);
        feature.SessionId = sessionId;
        feature.Version = binding.Version;

        byte[] payload = new byte[SessionHandshakeCodec.EstablishedSize];
        Span<byte> token = stackalloc byte[SessionHandshakeCodec.TokenLength];
        binding.ResumeToken.CopyTo(token);
        SessionHandshakeCodec.WriteEstablished(payload, sessionId.Value.Value, token);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            _encoder,
            FrameworkMessageIds.SessionEstablished,
            payload,
            FrameFlags.None,
            sequence: 0,
            context.CancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RejectAsync(MessageContext context)
    {
        byte[] payload = new byte[SessionHandshakeCodec.ResumeResponseSize];
        SessionHandshakeCodec.WriteResumeResponse(payload, SessionResumeStatus.Rejected, ReadOnlySpan<byte>.Empty);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            _encoder,
            FrameworkMessageIds.SessionResumed,
            payload,
            FrameFlags.None,
            sequence: 0,
            context.CancellationToken).ConfigureAwait(false);
    }

    private static ISessionFeature GetOrCreateFeature(MessageContext context)
    {
        ISessionFeature? existing = context.Connection.Features.Get<ISessionFeature>();
        if (existing is not null)
        {
            return existing;
        }

        SessionFeature created = new();
        context.Connection.Features.Set<ISessionFeature>(created);
        return created;
    }
}
