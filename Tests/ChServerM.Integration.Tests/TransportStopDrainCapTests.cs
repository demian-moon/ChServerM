using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Transport.InMemory;
using ChServerM.Transport.Tcp;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// <c>StopAsync</c> 1차 드레인 상한의 회귀 방지 — 감사 2026-08-18 T-2.
/// </summary>
/// <remarks>
/// 취소 불가 토큰(기본 인자)으로 <c>StopAsync()</c> 를 부르면 예전에는 1차 드레인 대기에
/// 상한이 없었다 — 취소 토큰을 무시하는 핸들러 하나가 상시 연결 워크로드의 종료를 영원히
/// 막았다. 이제 그 경우 <c>ShutdownTimeout</c> 이 1차 대기에도 적용된다. 다섯 전송이 같은
/// 골격을 쓰므로 결정적인 인메모리와 실소켓 TCP 로 고정한다.
/// </remarks>
public sealed class TransportStopDrainCapTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Fact]
    public async Task InMemory_StopAsync_with_default_token_is_bounded_by_shutdown_timeout()
    {
        InMemoryTransportHub hub = new();
        InMemoryEndPoint endPoint = new($"drain-cap-{Guid.NewGuid():N}");
        InMemoryTransportOptions options = new() { ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
        HangingHandler handler = new();

        await using InMemoryServerTransport server = new(hub, endPoint, options);
        await server.BindAsync(handler);

        await using InMemoryClientTransport client = new(hub, null, options);
        IConnection connection = await client.ConnectAsync(endPoint, _timeout.Token);

        try
        {
            // 기본 인자 호출 — 토큰이 취소 불가이므로 ShutdownTimeout 상한이 1차 드레인에
            // 걸려야 한다. 회귀하면 이 대기는 영원히 끝나지 않고 아래 WaitAsync 가 던진다.
            await server.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), _timeout.Token);
        }
        finally
        {
            handler.Release();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tcp_StopAsync_with_default_token_is_bounded_by_shutdown_timeout()
    {
        TcpTransportOptions options = new() { ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
        HangingHandler handler = new();

        await using TcpServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        await server.BindAsync(handler);

        EndPoint endPoint = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        await using TcpClientTransport client = new(options);
        IConnection connection = await client.ConnectAsync(endPoint, _timeout.Token);

        try
        {
            await server.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), _timeout.Token);
        }
        finally
        {
            handler.Release();
            await connection.DisposeAsync();
        }
    }

    /// <summary>취소·Abort 를 무시하고 놓아줄 때까지 끝나지 않는 핸들러 — T-2 의 공격 시나리오.</summary>
    private sealed class HangingHandler : IConnectionHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(IConnection connection) => _release.Task;

        public void Release() => _release.TrySetResult();
    }
}
