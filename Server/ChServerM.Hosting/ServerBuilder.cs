using System;
using ChServerM.Compression;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Execution;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Security;
using ChServerM.Transports;

namespace ChServerM.Hosting;

/// <summary>
/// 축을 골라 서버를 조립한다. 프레임워크의 정면 출입구.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "직렬화 모듈을 protobuf 로 할 수도 있고 FlatBuffers 로 할 수도 있고,
/// TCP 커넥션 서버로 할 수도 있고 무상태 웹서버로 할 수도 있다" — 그 선택이 실제로
/// 이뤄지는 지점이다.
/// </para>
/// <para>
/// <b>조립 비용은 여기서 전부 지불한다</b>(ADR-0000). 미들웨어 체인을 델리게이트로 접고,
/// 라우팅 테이블을 배열로 굳히고, 옵션을 검증한다. <see cref="Build"/> 가 돌아간 뒤에는
/// 핫패스에 동적 결정이 하나도 남지 않는다.
/// </para>
/// <para>
/// <b>전송을 인스턴스로 받는다.</b> <c>.UseTcp(port)</c> 같은 확장 메서드가 더 읽기 좋지만,
/// 그러려면 전송 어셈블리가 이 어셈블리를 참조해야 하고 그것은 CLAUDE.md 의 의존 방향
/// (<c>Hosting → 어댑터 → Core</c>)을 뒤집는다. 편의 문법을 어느 어셈블리에 둘지는
/// 별도로 정한다 — 지금은 방향을 지키는 쪽을 택했다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 빌더는 스레드 안전하지 않다. 조립은 단일 스레드로 끝낸다.
/// </para>
/// </remarks>
public sealed class ServerBuilder
{
    private readonly MessageDispatcherBuilder _dispatcher = new();
    private readonly FramedConnectionOptions _connectionOptions = new();

    private IServerTransport? _transport;
    private IFrameDecoder? _decoder;
    private IFrameEncoder? _encoder;
    private IExecutionModel? _executionModel;
    private ITransportSecurity? _transportSecurity;
    private VersionNegotiationOptions? _versionNegotiation;
    private IPayloadCodec? _payloadCodec;
    private IMetricsSink? _metricsSink;
    private IServerLogger _logger = NullServerLogger.Instance;
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>수용 전송을 지정한다.</summary>
    /// <param name="transport">전송 인스턴스. 서버가 소유권을 가져간다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/>가 <see langword="null"/>일 때.</exception>
    public ServerBuilder UseTransport(IServerTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        return this;
    }

    /// <summary>프레이밍 축을 지정한다.</summary>
    /// <param name="decoder">프레임 디코더.</param>
    /// <param name="encoder">프레임 인코더. 핸들러가 응답을 쓸 때 쓴다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    public ServerBuilder UseFraming(IFrameDecoder decoder, IFrameEncoder encoder)
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
    /// <para>
    /// 수락 직후·프레이밍 시작 전에 핸드셰이크가 수행된다(ADR-0017) — 적용 순서는
    /// 조립이 강제하므로 호출자가 틀릴 수 없다. 핸드셰이크 실패는
    /// <see cref="ErrorCode.SecureChannelFailed"/>로 커넥션이 닫힌다.
    /// </para>
    /// <para>
    /// TLS 를 내장한 전송(QUIC 등)에는 지정하지 않는다 — 이중 암호화가 된다.
    /// </para>
    /// </remarks>
    public ServerBuilder UseTransportSecurity(ITransportSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        _transportSecurity = security;
        return this;
    }

    /// <summary>압축 축을 지정한다 (ADR-0019).</summary>
    /// <param name="codec">압축 코덱. 지정하지 않으면 압축 프레임 수신 = 커넥션 종료.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/>이 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// <para>
    /// 수신: <see cref="Framing.FrameFlags.Compressed"/> 프레임이 (조각이면 재조립 후)
    /// 해제되어 핸들러에 평문으로 전달된다. 해제 상한은
    /// <see cref="FramedConnectionOptions.MaxDecompressedMessageLength"/>(T-18).
    /// 송신: 핸들러가 <see cref="FrameWriter.WriteCompressedFrameAsync(System.IO.Pipelines.PipeWriter, IFrameEncoder, IPayloadCodec, PayloadCompressionOptions, Identity.MessageId, ReadOnlySpan{byte}, uint, System.Threading.CancellationToken)"/> 를 쓴다.
    /// </para>
    /// <para>양쪽이 <b>같은 코덱 구현</b>을 조립해야 한다 — 알고리즘은 와이어에 실리지
    /// 않는 조립 수준 합의다(프레이밍 축 선택과 같은 성격). 불일치 = 해제 실패 = 종료.</para>
    /// <para>varint 프레이밍과는 조립할 수 없다 — 그 와이어에는 플래그 필드가 없어
    /// 인코더가 <see cref="Framing.FrameFlags.Compressed"/> 를 거부한다.</para>
    /// </remarks>
    public ServerBuilder UsePayloadCodec(IPayloadCodec codec)
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
    /// 커넥션의 첫 왕복이 <c>ClientHello[Min,Max]</c> → <c>ServerHello</c>(교집합 최고 버전
    /// 확정) 또는 <c>ConnectionRejected</c>(지원 구간 포함) 후 종료가 된다. 보안 축이 있으면
    /// 협상은 그 채널 <b>안</b>에서 일어난다 — 순서는 조립이 강제하므로 호출자가 틀릴 수
    /// 없다. 지정하지 않으면 협상 없이 바로 프레이밍이 시작된다(기존 동작).
    /// </para>
    /// <para>
    /// 클라이언트도 <see cref="ClientBuilder.UseVersionNegotiation"/> 으로 짝을 맞춰야 한다 —
    /// 서버만 켜면 클라이언트의 첫 앱 프레임이 <c>ClientHello</c> 형식 위반으로 거부된다.
    /// </para>
    /// <para>협상 결과는 커넥션의 <see cref="ChServerM.Features.IProtocolVersionFeature"/> 로 조회한다.</para>
    /// </remarks>
    public ServerBuilder UseVersionNegotiation(VersionNegotiationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _versionNegotiation = options;
        return this;
    }

    /// <summary>실행 모델을 지정한다.</summary>
    /// <param name="executionModel">실행 모델. 서버가 소유권을 가져간다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="executionModel"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 지정하지 않으면 커넥션 처리가 스레드풀에서 돈다. 순서 보장이 필요 없는
    /// 무상태 프로필은 그것이 맞는 선택이다(ADR-0004).
    /// </remarks>
    public ServerBuilder UseExecutionModel(IExecutionModel executionModel)
    {
        ArgumentNullException.ThrowIfNull(executionModel);
        _executionModel = executionModel;
        return this;
    }

    /// <summary>관측 축(메트릭)을 켠다 (Phase 11).</summary>
    /// <param name="sink">메트릭 싱크. 지정하지 않으면 메트릭을 수집하지 않는다(<see cref="NullMetricsSink"/>).</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// <para>
    /// 한 번의 호출로 커넥션 생명주기(수립·활성 수)와 디스패치(지연·처리량·실패)를 모두
    /// 배선한다 — 커넥션 데코레이터(<see cref="MetricsConnectionHandler"/>)와 디스패치
    /// 미들웨어(<see cref="Dispatch.MetricsMiddleware"/>)를 프레임워크가 올바른 순서로 끼운다.
    /// 사용자가 계측 코드를 핸들러에 넣을 필요가 없다(횡단 관심사는 데코레이터, CLAUDE.md 4).
    /// </para>
    /// <para>
    /// 메트릭 이름은 <see cref="MetricNames"/> 계약을 따른다. 첫 어댑터로
    /// <c>ChServerM.Observability.MeterMetricsSink</c>(BCL <c>Meter</c>)를 넘기면
    /// <c>dotnet-counters</c> 가 즉시 읽는다(ADR-0020).
    /// </para>
    /// </remarks>
    public ServerBuilder UseMetrics(IMetricsSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _metricsSink = sink;
        return this;
    }

    /// <summary>진단 로거를 지정한다.</summary>
    /// <param name="logger">로거.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ServerBuilder UseLogger(IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _dispatcher.UseLogger(logger);
        return this;
    }

    /// <summary>시간 원본을 지정한다.</summary>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ServerBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        return this;
    }

    /// <summary>읽기 루프의 종료 정책을 설정한다.</summary>
    /// <param name="configure">설정 함수.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ServerBuilder ConfigureConnection(Action<FramedConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_connectionOptions);
        return this;
    }

    /// <summary>미들웨어와 라우팅을 설정한다.</summary>
    /// <param name="configure">설정 함수.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public ServerBuilder ConfigureDispatcher(Action<MessageDispatcherBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_dispatcher);
        return this;
    }

    /// <summary>조립을 끝내고 서버를 만든다.</summary>
    /// <returns>시작할 준비가 된 서버.</returns>
    /// <exception cref="InvalidOperationException">필수 축이 지정되지 않았을 때.</exception>
    /// <remarks>
    /// <b>누락된 축은 예외로 즉시 알린다.</b> 기본값을 몰래 채우면 "왜 프레임이
    /// 안 잘리는가"를 런타임에 디버깅하게 된다. 조립 시점 실패는 예외가 옳다.
    /// </remarks>
    public ChServerMServer Build()
    {
        IServerTransport transport = _transport
            ?? throw new InvalidOperationException(
                $"전송이 지정되지 않았다. {nameof(UseTransport)} 를 호출한다.");

        IFrameDecoder decoder = _decoder
            ?? throw new InvalidOperationException(
                $"프레이밍이 지정되지 않았다. {nameof(UseFraming)} 를 호출한다.");

        IFrameEncoder encoder = _encoder
            ?? throw new InvalidOperationException(
                $"프레이밍이 지정되지 않았다. {nameof(UseFraming)} 를 호출한다.");

        _connectionOptions.Validate();

        // 축 하나하나가 유효해도 조합이 성립하지 않을 수 있다.
        CompositionGuard.EnsureFrameFitsInTransportBuffer(transport, decoder, encoder);

        // 관측 축이 있으면 디스패치 지연·처리량·실패 미들웨어를 파이프라인 가장 바깥에
        // 끼운다(빌드 전에 — 지연이 파이프라인 전체를 감싸야 의미가 있다, Phase 11).
        if (_metricsSink is not null)
        {
            _dispatcher.PrependMiddleware(new MetricsMiddleware(_metricsSink, _timeProvider));
        }

        // 실행 모델이 있으면 프레임 디스패치가 파티션 배타 구간에서 실행된다(ADR-0008).
        IConnectionHandler handler = new FramedConnectionHandler(
            decoder, _dispatcher.Build(), _connectionOptions, _timeProvider, _logger, _executionModel,
            _payloadCodec);

        // 버전 협상이 있으면 프레이밍 전에 1왕복 핸드셰이크가 끼어든다(ADR-0017 결정 3).
        if (_versionNegotiation is not null)
        {
            _versionNegotiation.Validate();
            handler = new VersionNegotiatingConnectionHandler(_versionNegotiation, handler, _timeProvider, _logger);
        }

        // 보안 축이 있으면 수락 직후·프레이밍 전에 핸드셰이크가 끼어든다(ADR-0017).
        // 협상보다 바깥에 감싼다 — 협상은 보안 채널 안에서 일어나야 R-4 가 충족된다.
        if (_transportSecurity is not null)
        {
            handler = new SecuredConnectionHandler(_transportSecurity, handler, _logger);
        }

        // 관측 커넥션 데코레이터는 가장 바깥이다 — TLS·협상에 실패한 커넥션도 "수락됐다"는
        // 사실은 세야 하고, 활성 게이지도 그 전 생애를 덮어야 한다(Phase 11).
        if (_metricsSink is not null)
        {
            handler = new MetricsConnectionHandler(handler, _metricsSink);
        }

        return new ChServerMServer(transport, handler, encoder, _executionModel);
    }
}
