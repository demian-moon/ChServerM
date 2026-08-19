using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Transport.WebSocket;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// WebSocket 핸드셰이크 검증의 회귀 방지 — 감사 2026-08-18 T-6.
/// </summary>
/// <remarks>
/// (1) <c>AllowedOrigins</c> 화이트리스트: 불일치 Origin 은 <c>403</c>, 일치는 <c>101</c>,
/// Origin 없는 비브라우저 요청은 통과. (2) <c>Sec-WebSocket-Version</c> 은 콤마 분리 후
/// 정확 비교 — 예전 <c>Contains("13")</c> 은 "130" 도 통과시켰다. BCL 클라이언트는 이런
/// 요청을 만들 수 없으므로 원시 소켓으로 요청을 직접 쓴다.
/// </remarks>
public sealed class WebSocketHandshakeTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Theory]
    [InlineData("https://evil.example", "403")]      // 화이트리스트 불일치 — CSWSH 차단.
    [InlineData("https://app.example", "101")]       // 화이트리스트 일치.
    [InlineData("HTTPS://APP.EXAMPLE", "403")]       // Ordinal 비교 — 대소문자가 다르면 불일치다.
    [InlineData(null, "101")]                        // Origin 없음(비브라우저) — 통과.
    public async Task AllowedOrigins_whitelist_is_enforced_with_ordinal_comparison(
        string? origin, string expectedStatus)
    {
        WebSocketTransportOptions options = new() { AllowedOrigins = ["https://app.example"] };

        await using WebSocketServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        await server.BindAsync(new CompletedHandler());

        string statusLine = await SendHandshakeAsync(
            server.LocalEndPoint!, version: "13", origin);

        Assert.Contains($" {expectedStatus}", statusLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("13", "101")]     // 정확 일치.
    [InlineData("13, 8", "101")]  // 콤마 목록 안의 정확 일치.
    [InlineData("130", "426")]    // 예전 Contains("13") 이 잘못 통과시키던 값.
    [InlineData("8", "426")]      // 미지원 버전.
    public async Task SecWebSocketVersion_requires_exact_13(string version, string expectedStatus)
    {
        await using WebSocketServerTransport server = new(
            new IPEndPoint(IPAddress.Loopback, 0), new WebSocketTransportOptions());
        await server.BindAsync(new CompletedHandler());

        string statusLine = await SendHandshakeAsync(
            server.LocalEndPoint!, version, origin: null);

        Assert.Contains($" {expectedStatus}", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Origins_are_not_checked_when_option_is_null()
    {
        // 기본값(null) = 검사 안 함 — 비브라우저 배포에서 화이트리스트를 강제하지 않는다.
        await using WebSocketServerTransport server = new(
            new IPEndPoint(IPAddress.Loopback, 0), new WebSocketTransportOptions());
        await server.BindAsync(new CompletedHandler());

        string statusLine = await SendHandshakeAsync(
            server.LocalEndPoint!, version: "13", origin: "https://anything.example");

        Assert.Contains(" 101", statusLine, StringComparison.Ordinal);
    }

    /// <summary>원시 소켓으로 업그레이드 요청을 쓰고 응답 상태 줄을 돌려준다.</summary>
    private async Task<string> SendHandshakeAsync(EndPoint endPoint, string version, string? origin)
    {
        IPEndPoint target = (IPEndPoint)endPoint;

        StringBuilder request = new();
        request.Append("GET /chsm HTTP/1.1\r\n");
        request.Append("Host: localhost\r\n");
        request.Append("Connection: Upgrade\r\n");
        request.Append("Upgrade: websocket\r\n");
        request.Append("Sec-WebSocket-Key: AAAAAAAAAAAAAAAAAAAAAA==\r\n");
        request.Append("Sec-WebSocket-Version: ").Append(version).Append("\r\n");
        if (origin is not null)
        {
            request.Append("Origin: ").Append(origin).Append("\r\n");
        }

        request.Append("\r\n");

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(target.Address, target.Port, _timeout.Token);

        NetworkStream stream = tcpClient.GetStream();
        byte[] bytes = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(bytes, _timeout.Token);

        byte[] buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer, _timeout.Token);
        Assert.True(read > 0, "서버가 응답 없이 연결을 닫았다.");

        string response = Encoding.ASCII.GetString(buffer, 0, read);
        int lineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
        return lineEnd < 0 ? response : response[..lineEnd];
    }

    /// <summary>즉시 끝나는 핸들러 — 핸드셰이크 결과만 검증하는 테스트용.</summary>
    private sealed class CompletedHandler : IConnectionHandler
    {
        public Task RunAsync(IConnection connection) => Task.CompletedTask;
    }
}
