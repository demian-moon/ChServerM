using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Core.Tests.Connections;

/// <summary>
/// 전송 없이 <see cref="IConnection"/> 계약만 만족하는 테스트용 커넥션.
/// </summary>
/// <remarks>
/// 실제 루프백 전송은 Stage 4 의 <c>ChServerM.Transport.InMemory</c> 다.
/// 여기서는 상위 계층 타입을 단독으로 검증하는 데만 쓴다.
/// </remarks>
internal sealed class StubConnection : IConnection, IDisposable
{
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly CancellationTokenSource _closed = new();

    public StubConnection(ConnectionId id = default)
    {
        Id = id.IsNone ? new ConnectionId(1, 1) : id;
    }

    public ConnectionId Id { get; }

    public PipeReader Input => _inbound.Reader;

    public PipeWriter Output => _outbound.Writer;

    public IFeatureCollection Features { get; } = new FeatureCollection();

    public CancellationToken ConnectionClosed => _closed.Token;

    /// <summary>마지막으로 기록된 중단 사유. 한 번도 중단되지 않았으면 기본값.</summary>
    public ConnectionCloseInfo LastAbort { get; private set; }

    /// <summary><see cref="Abort"/> 가 호출된 횟수.</summary>
    public int AbortCount { get; private set; }

    public void Abort(in ConnectionCloseInfo info)
    {
        AbortCount++;
        LastAbort = info;

        if (!_closed.IsCancellationRequested)
        {
            _closed.Cancel();
        }
    }

    public void Dispose()
    {
        if (!_closed.IsCancellationRequested)
        {
            _closed.Cancel();
        }

        _closed.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
