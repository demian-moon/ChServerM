using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;
using ChServerM.Security;

namespace ChServerM.Hosting;

/// <summary>
/// 보안 채널이 확립된 커넥션 — 바이트 경로만 채널로 바꿔치기한 <see cref="IConnection"/> 데코레이터.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 상위 계층(프레이밍·디스패치)이 평문을 계속 <see cref="IConnection"/>
/// 계약으로만 다루게 한다. 이 타입 덕분에 <c>FramedConnectionHandler</c> 는 TLS 가
/// 켜졌는지조차 모른다 — 그것이 축 독립(ADR-0017 결정 2)의 실체다.
/// </para>
/// <para>
/// <b>수명·소유권.</b> <see cref="Id"/>·<see cref="Features"/>·<see cref="ConnectionClosed"/>·
/// <see cref="Abort"/>는 원본에 위임한다 — 취소 단일 원천과 abortive 종료 경로가
/// 보안 계층 유무와 무관하게 유지된다. <see cref="DisposeAsync"/>는 <b>채널 먼저,
/// 원본 나중</b> 순서를 강제한다. 반대면 close_notify 와 남은 평문 flush 가 갈 곳을 잃는다.
/// </para>
/// </remarks>
internal sealed class SecuredConnection : IConnection
{
    private readonly IConnection _inner;
    private readonly ISecureChannel _channel;

    public SecuredConnection(IConnection inner, ISecureChannel channel)
    {
        _inner = inner;
        _channel = channel;
    }

    public ConnectionId Id => _inner.Id;

    public PipeReader Input => _channel.Input;

    public PipeWriter Output => _channel.Output;

    public IFeatureCollection Features => _inner.Features;

    public CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    public void Abort(in ConnectionCloseInfo info) => _inner.Abort(info);

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary><see cref="IConnection"/>의 바이트 경로를 <see cref="IDuplexPipe"/>로 보는 어댑터.</summary>
/// <remarks><see cref="ITransportSecurity"/>가 파이프 쌍을 받는 계약이라 필요하다. 상태가 없다.</remarks>
internal sealed class ConnectionDuplexPipe : IDuplexPipe
{
    private readonly IConnection _connection;

    public ConnectionDuplexPipe(IConnection connection) => _connection = connection;

    public PipeReader Input => _connection.Input;

    public PipeWriter Output => _connection.Output;
}
