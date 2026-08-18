using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using ChServerM.Compression;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Content;
using ChServerM.Diagnostics;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Handshake;
using ChServerM.Hosting.Dispatch;
using ChServerM.Security;
using ChServerM.Transports;

namespace ChServerM.Hosting;

/// <summary>
/// 축을 골라 클라이언트를 조립한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 클라이언트도 서버와 <b>같은 프레이밍·디스패치·핸들러</b>를 쓴다.
/// 그래서 서버-투-서버 통신이 특별한 경로가 되지 않고, 서버 핸들러를 클라이언트에
/// 그대로 꽂을 수 있다.
/// </para>
/// <para>
/// <b>재접속 정책은 여기에 없다.</b> 백오프·재시도를 프레임워크가 감추면 상위 계층이
/// "연결이 살아 있다"고 오해해 세션 재수립(인증·상태 복원)을 건너뛴다.
/// 재접속은 이 위에서 조립한다.
/// </para>
/// <para><b>스레드 규약.</b> 빌더는 스레드 안전하지 않다.</para>
/// </remarks>
public sealed class ClientBuilder
{
    private readonly MessageDispatcherBuilder _dispatcher = new();
    private readonly FramedConnectionOptions _connectionOptions = new();

    private IClientTransport? _transport;
    private IFrameDecoder? _decoder;
    private IFrameEncoder? _encoder;
    private ITransportSecurity? _transportSecurity;
    private VersionNegotiationOptions? _versionNegotiation;
    private ContentFingerprint _contentFingerprint;
    private IPayloadCodec? _payloadCodec;
    private IServerLogger _logger = NullServerLogger.Instance;
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>연결 전송을 지정한다.</summary>
    /// <param name="transport">전송 인스턴스. 클라이언트가 소유권을 가져간다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ClientBuilder UseTransport(IClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        return this;
    }

    /// <summary>프레이밍 축을 지정한다.</summary>
    /// <param name="decoder">프레임 디코더.</param>
    /// <param name="encoder">프레임 인코더.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ClientBuilder UseFraming(IFrameDecoder decoder, IFrameEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(encoder);

        _decoder = decoder;
        _encoder = encoder;
        return this;
    }

    /// <summary>전송 보안 축을 지정한다.</summary>
    /// <param name="security">보안 구현. 지정하지 않으면 평문이다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="security"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 연결 직후·프레이밍 시작 전에 클라이언트 측 핸드셰이크가 수행된다(ADR-0017).
    /// 실패하면 <see cref="ChServerMClient.ConnectAsync"/>가
    /// <see cref="AuthenticationException"/>을 던진다 — 클라이언트의 연결 수립은
    /// 호출자 대면 경로라 상태 반환보다 예외가 자연스럽다(서버 수락 경로와 다른 점).
    /// </remarks>
    public ClientBuilder UseTransportSecurity(ITransportSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        _transportSecurity = security;
        return this;
    }

    /// <summary>압축 축을 지정한다 (ADR-0019).</summary>
    /// <param name="codec">압축 코덱. 서버와 같은 구현이어야 한다 — 알고리즘은 와이어에
    /// 실리지 않는 조립 수준 합의다. 불일치 = 해제 실패 = 종료.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/>이 <see langword="null"/>일 때.</exception>
    public ClientBuilder UsePayloadCodec(IPayloadCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _payloadCodec = codec;
        return this;
    }

    /// <summary>버전 협상 핸드셰이크를 켠다 (ADR-0017 결정 3).</summary>
    /// <param name="options">협상 설정 — 지원 버전 구간과 제한 시간.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// <para>
    /// <see cref="ChServerMClient.ConnectAsync"/> 가 연결(보안 축이 있으면 그 핸드셰이크까지)
    /// 직후·프레이밍 시작 전에 <c>ClientHello</c> 를 보내고 서버의 확정/거부를 기다린다.
    /// 거부되면 <see cref="VersionNegotiationException"/> 이 던져지고, 그 안에 서버의 지원
    /// 구간이 실려 있다 — "클라이언트 업데이트 필요"를 사용자에게 알릴 근거다(R-3).
    /// </para>
    /// <para>
    /// 서버도 <see cref="ServerBuilder.UseVersionNegotiation"/> 으로 짝을 맞춰야 한다 —
    /// 클라이언트만 켜면 서버는 <c>ClientHello</c> 를 앱 프레임으로 해석한다.
    /// </para>
    /// </remarks>
    public ClientBuilder UseVersionNegotiation(VersionNegotiationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _versionNegotiation = options;
        return this;
    }

    /// <summary>진단 로거를 지정한다.</summary>
    public ClientBuilder UseLogger(IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _dispatcher.UseLogger(logger);
        return this;
    }

    /// <summary>시간 원본을 지정한다.</summary>
    public ClientBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        return this;
    }

    /// <summary>읽기 루프의 종료 정책을 설정한다.</summary>
    public ClientBuilder ConfigureConnection(Action<FramedConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_connectionOptions);
        return this;
    }

    /// <summary>접속 시점에 이 클라이언트의 <b>콘텐츠 지문</b>을 서버에 제시한다 (ADR-0044).</summary>
    /// <param name="fingerprint">이 클라이언트가 들고 있는 콘텐츠의 지문.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException"><paramref name="fingerprint"/>가 설정되지 않은 센티넬일 때.</exception>
    /// <remarks>
    /// <para>
    /// 서버가 <see cref="ServerBuilder.RequireContentFingerprint"/> 로 켜 놓은 게이트의 짝이다.
    /// 불일치면 <see cref="ContentFingerprintMismatchException"/> 이 나온다 —
    /// <see cref="VersionNegotiationException"/> 과 <b>구분되는 타입</b>인 이유는 요구되는
    /// 조치가 다르기 때문이다(실행 파일 갱신 vs 데이터 갱신).
    /// </para>
    /// <para>
    /// <b>⚠ 왕복은 늘지 않는다.</b> <c>ClientHello</c> 와 함께 한 번에 플러시한다.
    /// </para>
    /// <para>
    /// <b>⚠ <see cref="UseVersionNegotiation"/> 이 켜져 있어야 한다</b> — 지문 교환은 협상
    /// 직후에 일어난다. 협상 없이 켜면 <see cref="Build"/> 가 실패한다.
    /// </para>
    /// </remarks>
    public ClientBuilder SendContentFingerprint(ContentFingerprint fingerprint)
    {
        if (!fingerprint.IsSet)
        {
            throw new ArgumentException(
                "설정되지 않은 지문이다. 0 은 '설정되지 않음' 센티넬이라 와이어에 실을 수 없다.",
                nameof(fingerprint));
        }

        _contentFingerprint = fingerprint;
        return this;
    }

    /// <summary>서버가 보내는 메시지를 받을 핸들러를 설정한다.</summary>
    /// <remarks>
    /// 클라이언트도 서버 푸시를 받는다. 요청-응답만 쓰는 조립이라면 비워둬도 된다.
    /// </remarks>
    public ClientBuilder ConfigureDispatcher(Action<MessageDispatcherBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_dispatcher);
        return this;
    }

    /// <summary>조립을 끝내고 클라이언트를 만든다.</summary>
    /// <exception cref="InvalidOperationException">필수 축이 지정되지 않았을 때.</exception>
    public ChServerMClient Build()
    {
        IClientTransport transport = _transport
            ?? throw new InvalidOperationException(
                $"전송이 지정되지 않았다. {nameof(UseTransport)} 를 호출한다.");

        IFrameDecoder decoder = _decoder
            ?? throw new InvalidOperationException(
                $"프레이밍이 지정되지 않았다. {nameof(UseFraming)} 를 호출한다.");

        IFrameEncoder encoder = _encoder
            ?? throw new InvalidOperationException(
                $"프레이밍이 지정되지 않았다. {nameof(UseFraming)} 를 호출한다.");

        _connectionOptions.Validate();
        _versionNegotiation?.Validate();

        if (_contentFingerprint.IsSet && _versionNegotiation is null)
        {
            throw new InvalidOperationException(
                $"{nameof(SendContentFingerprint)} 는 {nameof(UseVersionNegotiation)} 을 함께 요구한다. "
                + "지문 교환은 협상 직후에 일어난다.");
        }

        CompositionGuard.EnsureFrameFitsInTransportBuffer(transport, decoder, encoder);

        if (_payloadCodec is not null)
        {
            CompositionGuard.EnsureCodecSupportsCompression(encoder, decoder);
        }

        if (_versionNegotiation is not null)
        {
            CompositionGuard.EnsureCodecSupportsVersionNegotiation(encoder, decoder);
        }

        FramedConnectionHandler handler = new(
            decoder, _dispatcher.Build(), _connectionOptions, _timeProvider, _logger,
            payloadCodec: _payloadCodec);

        // ⚠ 옵션 계약("조립 시점 전용. Build() 가 값을 복사한다")을 지킨다 — 서버 측
        //   VersionNegotiatingConnectionHandler 는 생성자에서 복사하는데 클라이언트가 참조를
        //   보관하면 Build 후 옵션 변이가 검증 없이 접속마다 반영된다(감사 2026-08-18 H-2).
        VersionNegotiationOptions? negotiationSnapshot = _versionNegotiation is null
            ? null
            : new VersionNegotiationOptions
            {
                SupportedVersions = _versionNegotiation.SupportedVersions,
                HandshakeTimeout = _versionNegotiation.HandshakeTimeout,
            };

        return new ChServerMClient(
            transport, handler, encoder, _transportSecurity, negotiationSnapshot, _contentFingerprint, _timeProvider);
    }
}

/// <summary>
/// 조립이 끝난 클라이언트.
/// </summary>
/// <remarks>
/// 연결을 맺고, 그 커넥션의 읽기 루프를 돌린다. 읽기 루프가 없으면
/// 서버가 보낸 프레임을 아무도 처리하지 않는다 — 요청-응답만 쓰더라도
/// 응답을 받으려면 루프가 필요하다.
/// </remarks>
public sealed class ChServerMClient : IAsyncDisposable
{
    private readonly IClientTransport _transport;
    private readonly IConnectionHandler _handler;
    private readonly ITransportSecurity? _security;
    private readonly VersionNegotiationOptions? _negotiation;
    private readonly ContentFingerprint _contentFingerprint;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    internal ChServerMClient(
        IClientTransport transport,
        IConnectionHandler handler,
        IFrameEncoder encoder,
        ITransportSecurity? security,
        VersionNegotiationOptions? negotiation,
        ContentFingerprint contentFingerprint,
        TimeProvider timeProvider)
    {
        _transport = transport;
        _handler = handler;
        _security = security;
        _negotiation = negotiation;
        _contentFingerprint = contentFingerprint;
        _timeProvider = timeProvider;
        Encoder = encoder;
    }

    /// <summary>이 클라이언트가 쓰는 프레임 인코더.</summary>
    public IFrameEncoder Encoder { get; }

    /// <summary>연결을 맺고 읽기 루프를 시작한다.</summary>
    /// <param name="endPoint">연결할 주소.</param>
    /// <param name="cancellationToken">연결 시도의 취소 토큰.</param>
    /// <returns>수립된 커넥션과, 읽기 루프가 끝나면 완료되는 작업.</returns>
    /// <remarks>
    /// <para>
    /// 읽기 루프 작업을 <b>돌려준다.</b> 감추면 호출자가 루프의 실패를 관측할 수 없고,
    /// 그러면 "연결은 살아 있는데 아무 응답이 없는" 상태를 진단할 방법이 사라진다.
    /// </para>
    /// <para>
    /// 보안 축이 지정됐으면 연결 직후 핸드셰이크가 수행되고, 반환되는 커넥션의
    /// 바이트 경로는 평문 측 보안 채널이다(ADR-0017).
    /// </para>
    /// </remarks>
    /// <exception cref="AuthenticationException">보안 핸드셰이크가 실패했을 때.</exception>
    /// <exception cref="VersionNegotiationException">버전 협상이 거부·실패했을 때.</exception>
    public async ValueTask<ClientSession> ConnectAsync(
        EndPoint endPoint,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        IConnection connection = await _transport.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

        if (_security is not null)
        {
            connection = await SecureAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        if (_negotiation is not null)
        {
            // 협상은 보안 채널 확립 후·프레이밍 시작 전이다(ADR-0017 결정 3).
            await NegotiateAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        return new ClientSession(connection, _handler.RunAsync(connection));
    }

    /// <summary>클라이언트 측 핸드셰이크를 수행하고 보안 커넥션으로 감싼다.</summary>
    private async ValueTask<IConnection> SecureAsync(IConnection connection, CancellationToken cancellationToken)
    {
        SecureChannelResult result;
        using (CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed, cancellationToken))
        {
            result = await _security!
                .SecureAsClientAsync(new ConnectionDuplexPipe(connection), linked.Token)
                .ConfigureAwait(false);
        }

        if (!result.IsEstablished)
        {
            // 실패한 원시 커넥션을 반드시 정리한다 — 남겨두면 소켓이 샌다.
            await connection.DisposeAsync().ConfigureAwait(false);

            if (result.Status == SecureChannelStatus.Canceled)
            {
                throw new OperationCanceledException("보안 채널 확립이 취소됐다.");
            }

            throw new AuthenticationException(
                "보안 채널 확립에 실패했다. 서버 인증서 신뢰·프로토콜 버전 설정을 확인한다.");
        }

        return new SecuredConnection(connection, result.Channel!);
    }

    /// <summary>클라이언트 측 버전 협상 1왕복을 수행한다.</summary>
    /// <remarks>
    /// <para>
    /// <c>ClientHello</c> 는 <see cref="VersionHandshakeCodec"/> 의 동결 레이아웃으로 파이프에
    /// 직접 쓴다 — 프레이밍 축을 타지 않는다(R-2). 성공하면 정확히 응답 바이트까지만
    /// 소비하므로, 서버가 확정 직후 보낸 프레임은 읽기 루프의 첫 읽기로 넘어간다.
    /// </para>
    /// <para>
    /// 실패는 전부 커넥션 정리 후 예외다 — 연결 수립은 호출자 대면 경로라
    /// <see cref="SecureAsync"/> 와 같은 원칙을 따른다(취소만 <see cref="OperationCanceledException"/>).
    /// </para>
    /// </remarks>
    private async ValueTask NegotiateAsync(IConnection connection, CancellationToken cancellationToken)
    {
        VersionNegotiationOptions negotiation = _negotiation!;

        // 제한 시간은 CTS 가 센다 — 응답 없는 서버에 매달리지 않는다.
        //
        // ⚠ linked 에 connection.ConnectionClosed 를 넣지 않는다. 커넥션이 닫히면 수신
        // 펌프가 Input 파이프를 완료하므로 읽기는 IsCompleted 로 깨어난다(매달림 없음).
        // 토큰까지 걸면 종료 신호가 버퍼에 이미 도착한 서버 응답(거부 프레임)을 읽기 전에
        // read 를 취소하는 경합이 생긴다 — 응답과 FIN 이 붙어 도착하는 느린 CI 루프백에서
        // "거부에 서버 구간이 없다(null)"로 재현되던 실결함의 원인이다(2026-08-10~11).
        // 파이프 완료가 곧 종료 신호일 때 취소 토큰을 겹치면 원천이 둘이 된다(Phase 1 규약).
        using CancellationTokenSource timeoutCts = new(negotiation.HandshakeTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            PipeWriter output = connection.Output;
            Span<byte> hello = output.GetSpan(VersionHandshakeCodec.ClientHelloFrameSize);
            VersionHandshakeCodec.WriteClientHello(hello, negotiation.SupportedVersions);
            output.Advance(VersionHandshakeCodec.ClientHelloFrameSize);

            // ⚠ 지문을 같은 플러시에 실어 왕복을 늘리지 않는다. 서버는 ClientHello 만
            // 소비하고 넘기므로, 지문 프레임은 다음 단계의 버퍼에 이미 들어가 있다(ADR-0044).
            if (_contentFingerprint.IsSet)
            {
                Span<byte> offer = output.GetSpan(ContentFingerprintCodec.OfferFrameSize);
                ContentFingerprintCodec.WriteOffer(offer, _contentFingerprint);
                output.Advance(ContentFingerprintCodec.OfferFrameSize);
            }

            FlushResult flush = await output.FlushAsync(linked.Token).ConfigureAwait(false);
            if (flush.IsCanceled || flush.IsCompleted)
            {
                throw new VersionNegotiationException("서버가 버전 협상 중 연결을 닫았다.");
            }

            PipeReader input = connection.Input;
            while (true)
            {
                ReadResult read = await input.ReadAsync(linked.Token).ConfigureAwait(false);
                if (read.IsCanceled)
                {
                    throw new VersionNegotiationException("버전 협상 읽기가 중단됐다.");
                }

                ReadOnlySequence<byte> buffer = read.Buffer;
                VersionHandshakeStatus status =
                    VersionHandshakeCodec.TryReadServerResponse(buffer, out VersionHandshakeResponse response);

                if (status == VersionHandshakeStatus.Success)
                {
                    // 정확히 응답 바이트까지만 소비한다 — 뒤는 프레이밍의 몫이다.
                    SequencePosition consumed = buffer.GetPosition(response.FrameSize);
                    input.AdvanceTo(consumed, consumed);

                    if (!response.IsAccepted)
                    {
                        throw new VersionNegotiationException(
                            $"서버가 버전을 거부했다. 서버 지원 {response.ServerSupported}, " +
                            $"클라이언트 지원 {negotiation.SupportedVersions}. 클라이언트 업데이트가 필요할 수 있다.",
                            response.ServerSupported);
                    }

                    connection.Features.Set<IProtocolVersionFeature>(
                        new NegotiatedVersionFeature(response.SelectedVersion));

                    if (_contentFingerprint.IsSet)
                    {
                        await AwaitContentResponseAsync(input, linked.Token).ConfigureAwait(false);
                    }

                    return;
                }

                if (status == VersionHandshakeStatus.Malformed)
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                    throw new VersionNegotiationException(
                        "버전 협상 응답을 해석할 수 없다. 서버가 협상을 조립하지 않았거나 다른 사유로 거부했다.");
                }

                // NeedMoreData — examined 를 끝으로 둬야 파이프가 더 읽는다.
                input.AdvanceTo(buffer.Start, buffer.End);

                if (read.IsCompleted)
                {
                    throw new VersionNegotiationException("서버가 버전 협상 응답 없이 연결을 닫았다.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 호출자 취소가 아니다 — 제한 시간 초과 또는 커넥션 종료다.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new VersionNegotiationException(
                timeoutCts.IsCancellationRequested
                    ? $"버전 협상이 제한 시간({negotiation.HandshakeTimeout})을 넘겼다."
                    : "버전 협상 중 연결이 닫혔다.");
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (VersionNegotiationException)
        {
            // 실패한 커넥션을 반드시 정리한다 — 남겨두면 소켓이 샌다(SecureAsync 와 동일).
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (ContentFingerprintMismatchException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>서버의 콘텐츠 지문 응답(수락 또는 거부)을 읽는다.</summary>
    /// <remarks>
    /// <para>
    /// <b>수락도 프레임으로 온다.</b> 침묵을 수락으로 삼으면 "수락됐다" 와 "아직 안 왔다" 를
    /// 구분할 수 없어 매 접속이 제한 시간만큼 늘어진다(ADR-0044).
    /// </para>
    /// <para>
    /// 거부 사유가 지문 불일치가 아니면 <see cref="VersionNegotiationException"/> 으로 낸다 —
    /// 그 자리에 올 수 있는 다른 사유(동시 접속 상한 등)는 콘텐츠 문제가 아니고,
    /// 콘텐츠 예외로 포장하면 호출자가 <b>엉뚱한 안내</b>를 띄운다.
    /// </para>
    /// </remarks>
    private async ValueTask AwaitContentResponseAsync(PipeReader input, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read.IsCanceled)
            {
                throw new VersionNegotiationException("콘텐츠 지문 응답 읽기가 중단됐다.");
            }

            ReadOnlySequence<byte> buffer = read.Buffer;
            VersionHandshakeStatus status = ContentFingerprintCodec.TryReadServerResponse(
                buffer, out bool accepted, out ushort rejectReason, out int consumed);

            if (status == VersionHandshakeStatus.Success)
            {
                // 정확히 응답 바이트까지만 소비한다 — 뒤는 프레이밍의 몫이다.
                SequencePosition end = buffer.GetPosition(consumed);
                input.AdvanceTo(end, end);

                if (accepted)
                {
                    return;
                }

                if (rejectReason == VersionHandshakeCodec.RejectReasonContentMismatch)
                {
                    throw new ContentFingerprintMismatchException(
                        $"서버가 콘텐츠 지문 불일치로 거부했다(클라이언트 {_contentFingerprint}). "
                        + "데이터 갱신이 필요하다. 서버 로그에 양쪽 지문이 함께 남는다.",
                        _contentFingerprint);
                }

                throw new VersionNegotiationException(
                    $"서버가 콘텐츠 지문 단계에서 연결을 거부했다. 사유 코드 {rejectReason}.");
            }

            if (status == VersionHandshakeStatus.Malformed)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                throw new VersionNegotiationException(
                    "콘텐츠 지문 응답을 해석할 수 없다. 서버가 지문 게이트를 켜지 않았을 수 있다 — 게이트는 양쪽 스위치다.");
            }

            // NeedMoreData — examined 를 끝으로 둬야 파이프가 더 읽는다.
            input.AdvanceTo(buffer.Start, buffer.End);

            if (read.IsCompleted)
            {
                throw new VersionNegotiationException("서버가 콘텐츠 지문 응답 없이 연결을 닫았다.");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>연결 하나와 그 읽기 루프.</summary>
/// <param name="Connection">수립된 커넥션.</param>
/// <param name="Completion">읽기 루프가 끝나면 완료되는 작업.</param>
public readonly record struct ClientSession(IConnection Connection, Task Completion);
