using System;
using System.Collections.Generic;
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
    private bool _tracingEnabled;
    private readonly List<HealthCheckRegistration> _healthChecks = [];
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

    /// <summary>분산 추적(<c>ActivitySource</c>)을 켠다 (Phase 11, ADR-0022).</summary>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <remarks>
    /// <para>
    /// 디스패치 파이프라인에 <see cref="Dispatch.TracingMiddleware"/> 를 끼워 프레임마다
    /// <see cref="ActivityNames.Dispatch"/> span 을 남긴다. 싱크 인자가 없는 것은
    /// <see cref="UseMetrics"/> 와의 핵심 차이다 — 추적의 교체 지점은 방출자가 아니라
    /// <b>구독자</b>다. 익스포터(OpenTelemetry·Jaeger 등)는 <see cref="System.Diagnostics.ActivitySource"/>
    /// 이름(<see cref="DiagnosticNames.ActivitySourceName"/>)으로 <c>ActivityListener</c> 를
    /// 걸어 프로세스 바깥에서 구독한다(ADR-0022).
    /// </para>
    /// <para>
    /// <b>리스너가 없으면 거의 무비용이다.</b> <see cref="Dispatch.TracingMiddleware"/> 는
    /// 구독자가 없을 때 <c>next</c> 를 async 래퍼 없이 그대로 통과시킨다 — 추적을 켜되
    /// 익스포터를 붙이지 않은 조립의 오버헤드가 near-zero 다.
    /// </para>
    /// </remarks>
    public ServerBuilder UseTracing()
    {
        _tracingEnabled = true;
        return this;
    }

    /// <summary>헬스 체크를 등록한다 (Phase 11 관측).</summary>
    /// <param name="name">체크 이름(보고서 키). 비어 있을 수 없다.</param>
    /// <param name="check">헬스 체크.</param>
    /// <param name="probes">이 체크가 기여하는 프로브. 기본은 <see cref="HealthProbe.All"/>.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/>이 비어 있을 때.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="check"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// <para>
    /// 세션 저장소 연결·의존성 준비 같은 앱·어댑터 고유의 헬스를 더한다. 내장 체크(수용 상태
    /// readiness·실행 모델 liveness)는 조립 시점에 자동 등록되므로 여기서 다시 넣지 않는다.
    /// </para>
    /// <para>
    /// 결과는 <see cref="ChServerMServer.Health"/> 로 조회한다. HTTP 노출(<c>/healthz</c>·
    /// <c>/readyz</c>)은 이 서비스를 감싸는 별도 관리 서버 몫이다(후속 어댑터).
    /// </para>
    /// </remarks>
    public ServerBuilder AddHealthCheck(string name, IHealthCheck check, HealthProbe probes = HealthProbe.All)
    {
        _healthChecks.Add(new HealthCheckRegistration(name, check, probes));
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

        // 추적을 켜면 디스패치 span 이 파이프라인 전체를 감싼다. 메트릭보다 바깥에 두어
        // (뒤에 Prepend) span 이 인증·인가·메트릭 미들웨어까지 포함하게 한다(Phase 11).
        if (_tracingEnabled)
        {
            _dispatcher.PrependMiddleware(new TracingMiddleware());
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

        // 추적을 켜면 커넥션 span 데코레이터가 가장 바깥이다 — 핸드셰이크·활성 계측까지 포함한
        // 커넥션 전 생애를 한 span 으로 덮고, 그 컨텍스트를 디스패치 span 의 부모로 실는다
        // (크로스 스레드 전파, ADR-0022). 미들웨어(디스패치 span)는 ConfigureDispatcher 에서 이미 배선됐다.
        if (_tracingEnabled)
        {
            handler = new TracingConnectionHandler(handler);
        }

        // 헬스 체크 조립 — 내장(수용 상태 readiness·실행 모델 liveness) + 사용자 등록.
        ServerLifecycleState lifecycle = new();
        HealthCheckService health = new(BuildHealthRegistrations(lifecycle));

        return new ChServerMServer(transport, handler, encoder, _executionModel, lifecycle, health);
    }

    /// <summary>내장 헬스 체크와 사용자 등록을 하나의 목록으로 모은다.</summary>
    /// <remarks>
    /// <b>실행 모델 liveness 는 <see cref="IHealthCheck"/> 구현 여부로 배선한다.</b> 호스팅은
    /// Concurrency 를 참조하지 않으므로 <c>PartitionedExecutionModel</c> 을 직접 알지 못한다 —
    /// 대신 실행 모델이 <see cref="IHealthCheck"/> 를 구현하면 liveness 프로브에 자동 등록한다.
    /// Core 실행 모델 계약에 진단 멤버를 얹지 않고 배선을 얻는 접점이다(ADR 근거).
    /// </remarks>
    private List<HealthCheckRegistration> BuildHealthRegistrations(ServerLifecycleState lifecycle)
    {
        List<HealthCheckRegistration> registrations =
        [
            // 수용 상태 → readiness. 드레이닝이면 트래픽에서 빠진다.
            new HealthCheckRegistration("acceptance", new AcceptanceReadinessCheck(lifecycle), HealthProbe.Readiness),
        ];

        // 실행 모델이 헬스를 낼 수 있으면 liveness 로 등록한다.
        if (_executionModel is IHealthCheck executionModelHealth)
        {
            registrations.Add(new HealthCheckRegistration("execution-model", executionModelHealth, HealthProbe.Liveness));
        }

        // 전송이 헬스를 낼 수 있으면 readiness 로 등록한다 — 수락 루프가 죽으면 신규 트래픽을
        // 받을 수 없으므로 로드밸런서에서 빠져야 한다(기존 커넥션은 계속 처리되므로 재시작
        // 대상은 아니다, ADR-0028).
        if (_transport is IHealthCheck transportHealth)
        {
            registrations.Add(new HealthCheckRegistration("transport", transportHealth, HealthProbe.Readiness));
        }

        // 사용자 등록은 뒤에 — 보고서 항목 순서가 내장 → 사용자다.
        registrations.AddRange(_healthChecks);
        return registrations;
    }
}
