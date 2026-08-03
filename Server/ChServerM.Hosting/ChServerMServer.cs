using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Execution;
using ChServerM.Framing;
using ChServerM.Transports;

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

    private int _started;
    private int _disposed;

    internal ChServerMServer(
        IServerTransport transport,
        IConnectionHandler handler,
        IFrameEncoder encoder,
        IExecutionModel? executionModel)
    {
        _transport = transport;
        _handler = handler;
        _executionModel = executionModel;
        Encoder = encoder;
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
    }

    /// <summary>신규 수용을 멈춘다. 기존 커넥션은 유지한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <remarks>
    /// 무중단 배포의 첫 단계다. 로드밸런서가 트래픽을 돌리는 동안 이미 붙어 있는
    /// 클라이언트는 하던 일을 끝낸다.
    /// </remarks>
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) =>
        _transport.UnbindAsync(cancellationToken);

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
