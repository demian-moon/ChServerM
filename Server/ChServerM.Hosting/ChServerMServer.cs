using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Execution;
using ChServerM.Framing;
using ChServerM.Transports;
using ChServerM.Hosting.Sessions;

namespace ChServerM.Hosting;

/// <summary>
/// 조립이 끝난 서버. 생명주기만 노출한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="ServerBuilder"/> 가 만든 결과물을 담고, 시작·정지의
/// 순서를 강제한다. 특히 <b>종료 순서</b>는 손으로 하면 반드시 틀리는 부분이다 —
/// 전송을 먼저 멈춰야 하고, 그 다음이 실행 모델이다. 반대로 하면 아직 처리 중인
/// 커넥션의 연속이 갈 곳을 잃는다.
/// </para>
/// <para>
/// <b>소유권.</b> 전송과 실행 모델의 소유권을 가져간다. 이 객체를 정리하면
/// 그것들도 함께 정리된다 — 소유권이 나뉘어 있으면 "누가 먼저 정리하는가"가
/// 애매해지고, 그 애매함이 종료 시 경합으로 나타난다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// </remarks>
public sealed class ChServerMServer : IAsyncDisposable
{
    private readonly IServerTransport _transport;
    private readonly IConnectionHandler _handler;
    private readonly IExecutionModel? _executionModel;
    private readonly ServerLifecycleState _lifecycle;

    private int _started;
    private int _disposed;

    internal ChServerMServer(
        IServerTransport transport,
        IConnectionHandler handler,
        IFrameEncoder encoder,
        IExecutionModel? executionModel,
        ServerLifecycleState lifecycle,
        HealthCheckService health,
        DiagnosticsService diagnostics,
        SessionResumeService? sessions,
        Sessions.SessionResumeDispatch? sessionDispatch)
    {
        Sessions = sessions;
        SessionDispatch = sessionDispatch;
        _transport = transport;
        _handler = handler;
        _executionModel = executionModel;
        _lifecycle = lifecycle;
        Encoder = encoder;
        Health = health;
        Diagnostics = diagnostics;
    }

    /// <summary>이 서버가 쓰는 프레임 인코더.</summary>
    /// <remarks>
    /// 핸들러가 응답을 쓸 때 필요하다. 조립 시점에 이것을 꺼내 핸들러에 넘긴다 —
    /// 그러면 핸들러가 프레이밍 구현을 직접 만들 이유가 사라진다.
    /// </remarks>
    public IFrameEncoder Encoder { get; }

    /// <summary>실제로 바인드된 주소. 시작 전에는 <see langword="null"/>.</summary>
    public EndPoint? LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>실행 모델. 지정하지 않았으면 <see langword="null"/>.</summary>
    public IExecutionModel? ExecutionModel => _executionModel;

    /// <summary>헬스 체크 서비스 (Phase 11 관측).</summary>
    /// <remarks>
    /// <para>
    /// 프로브별로 헬스를 조회한다 — <c>Health.CheckHealthAsync(HealthProbe.Readiness)</c> 는
    /// 드레이닝 여부를(무중단 배포), <c>HealthProbe.Liveness</c> 는 실행 모델 스레드 생존을 본다.
    /// 내장 체크(수용 상태 readiness·실행 모델 liveness)는 조립 시점에 자동 등록되고,
    /// 사용자 체크는 <c>ServerBuilder.AddHealthCheck</c> 로 더한다.
    /// </para>
    /// <para>
    /// <b>HTTP 노출은 별도 어댑터 몫이다(후속).</b> 이 프로퍼티는 프로그래밍 접점이다 —
    /// k8s 프로브용 <c>/healthz</c>·<c>/readyz</c> 엔드포인트는 이 서비스를 감싸는 관리
    /// 서버가 담당한다(HTTP 호스팅은 별개 축).
    /// </para>
    /// </remarks>
    public HealthCheckService Health { get; }

    /// <summary>런타임 진단 서비스 (Phase 11 관측).</summary>
    /// <remarks>
    /// <para>
    /// <c>Diagnostics.Collect()</c> 가 커넥션·스레드·풀 상태의 <b>지금 이 순간 스냅샷</b>을
    /// 평문으로 돌려준다. 메트릭이 카디널리티 때문에 담을 수 없는 상세
    /// (어느 커넥션이 오래 조용한가, 어느 파티션이 멈췄는가)가 여기 있다.
    /// </para>
    /// <para>
    /// 전송·실행 모델이 <see cref="ChServerM.Diagnostics.IDiagnosticsSource"/> 를 구현하면 자동으로 포함되고,
    /// 그 밖의 소스는 <c>ServerBuilder.AddDiagnosticsSource</c> 로 더한다.
    /// </para>
    /// </remarks>
    public DiagnosticsService Diagnostics { get; }

    /// <summary>
    /// 세션 재개 서비스. <c>UseSessions</c> 로 얹지 않았으면 <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// 앱의 인증 핸들러가 세션을 <b>수립</b>할 때 쓴다 — 재개는 프레임워크가 이미 배선했다.
    /// </remarks>
    public SessionResumeService? Sessions { get; }

    /// <summary>
    /// 세션 재개 배선. <c>UseSessions</c> 로 얹지 않았으면 <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// 수립 통지(<c>WriteEstablishedAsync</c>)를 보낼 때 쓴다. 재개 처리는 이미
    /// 예약 메시지에 매핑돼 있으므로 앱이 직접 부를 일이 없다.
    /// </remarks>
    public Sessions.SessionResumeDispatch? SessionDispatch { get; }

    /// <summary>수용을 시작한다.</summary>
    /// <param name="cancellationToken">시작 작업의 취소 토큰.</param>
    /// <exception cref="InvalidOperationException">이미 시작했을 때.</exception>
    /// <exception cref="ObjectDisposedException">이미 정리됐을 때.</exception>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("서버가 이미 시작됐다.");
        }

        await _transport.BindAsync(_handler, cancellationToken).ConfigureAwait(false);

        // 바인드 성공 = 수용 중. readiness 가 이제 Healthy 를 보고한다.
        _lifecycle.Set(ServerState.Accepting);
    }

    /// <summary>신규 수용을 멈춘다. 기존 커넥션은 유지한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <remarks>
    /// 무중단 배포의 첫 단계다. 로드밸런서가 트래픽을 돌리는 동안 이미 붙어 있는
    /// 클라이언트는 하던 일을 끝낸다.
    /// </remarks>
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        // 드레이닝으로 먼저 전이한다 — 언바인드가 진행되는 동안에도 readiness 프로브가
        // 즉시 not-ready 를 보고해 로드밸런서가 트래픽을 뺀다(디레지스터 신호를 지연시키지 않는다).
        _lifecycle.Set(ServerState.Draining);
        return _transport.UnbindAsync(cancellationToken);
    }

    /// <summary>남은 커넥션을 드레인하고 멈춘다.</summary>
    /// <param name="cancellationToken">드레인 제한 시간.</param>
    /// <remarks>
    /// <b>전송을 먼저, 실행 모델을 나중에 멈춘다.</b> 순서가 반대면 아직 처리 중인
    /// 커넥션의 <c>await</c> 연속이 이미 멈춘 파티션으로 가서 갈 곳을 잃는다.
    /// </remarks>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _transport.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_executionModel is not null)
        {
            await _executionModel.DisposeAsync().ConfigureAwait(false);
        }

        // 멈춤. liveness·readiness 모두 이제 not-healthy 다(실행 모델 스레드도 종료됐다).
        _lifecycle.Set(ServerState.Stopped);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Dispose 는 기다려주는 자리가 아니다. 즉시 끊는다.
        using CancellationTokenSource immediate = new();
        await immediate.CancelAsync().ConfigureAwait(false);

        await StopAsync(immediate.Token).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
