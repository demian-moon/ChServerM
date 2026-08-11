using System;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Identity;
using ChServerM.Transports;

namespace ChServerM.Transport.WebSocket;

/// <summary>
/// WebSocket 으로 나가는 커넥션을 만드는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="WebSocketServerTransport"/> 의 짝이다. 연결이 수립되면
/// 서버와 같은 <see cref="WebSocketDuplexConnection"/> 어댑터를 쓰므로 이후 경로는 완전히
/// 대칭이다. BCL <see cref="ClientWebSocket"/> 이 핸드셰이크를 수행한다 — 클라이언트 쪽
/// 핸드셰이크는 규격 검증(accept 키 대조)이 BCL 에 이미 있어 직접 만들 이유가 없다.
/// </para>
/// <para><b>재접속은 이 계층의 책임이 아니다</b>(<see cref="IClientTransport"/> 계약).</para>
/// <para><b>스레드 규약.</b> 스레드 안전하다. 여러 스레드가 동시에 연결해도 된다.</para>
/// </remarks>
public sealed class WebSocketClientTransport : IClientTransport
{
    private readonly WebSocketTransportOptions _options;
    private int _nextSlot;

    /// <summary>클라이언트 전송을 만든다.</summary>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값. 서버와 같은 <see cref="WebSocketTransportOptions.Path"/> 를 써야 한다.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public WebSocketClientTransport(WebSocketTransportOptions? options = null)
    {
        options ??= new WebSocketTransportOptions();
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 반환 시점에 업그레이드(101)가 확인돼 있다 — 드레인(503)·상한 초과 거부는 여기서
    /// 예외로 드러난다. 조용히 만든 커넥션이 첫 쓰기에서 죽는 것보다 연결 시점 실패가 낫다.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endPoint"/>가 IP·DNS 종단이 아닐 때.</exception>
    /// <exception cref="WebSocketException">업그레이드가 수립되지 못했을 때.</exception>
    public async ValueTask<IConnection> ConnectAsync(
        EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        Uri uri = BuildUri(endPoint);

        // CA2000 억제: 소유권이 성공 시 커넥션으로, 실패 시 아래 catch 로 넘어간다.
#pragma warning disable CA2000
        ClientWebSocket webSocket = new();
#pragma warning restore CA2000

        // keep-alive ping 은 켜지 않는다 — 하트비트는 애플리케이션 레벨의 몫이다
        // (서버 쪽과 같은 판단. 기본값 30초를 그대로 두면 서버만 끈 조합에서
        //  클라이언트 ping 이 정체불명의 바이너리가 아니라 제어 프레임이라 무해하지만,
        //  대칭이 아닌 설정은 진단을 어렵게 한다).
        webSocket.Options.KeepAliveInterval = TimeSpan.Zero;

        try
        {
            await webSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }

        return new WebSocketDuplexConnection(
            NextConnectionId(), webSocket, localEndPoint: null, endPoint, _options);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // 살아 있는 커넥션은 각자 소켓을 소유한다. 전송이 들고 있는 공유 자원이 없다.
        return ValueTask.CompletedTask;
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    /// <summary>연결 대상 종단을 업그레이드 URI 로 바꾼다.</summary>
    private Uri BuildUri(EndPoint endPoint)
    {
        string authority = endPoint switch
        {
            // IPv6 리터럴은 URI 에서 대괄호가 필요하다.
            IPEndPoint { AddressFamily: System.Net.Sockets.AddressFamily.InterNetworkV6 } ip6 =>
                $"[{ip6.Address}]:{ip6.Port.ToString(CultureInfo.InvariantCulture)}",
            IPEndPoint ip => $"{ip.Address}:{ip.Port.ToString(CultureInfo.InvariantCulture)}",
            DnsEndPoint dns => $"{dns.Host}:{dns.Port.ToString(CultureInfo.InvariantCulture)}",
            _ => throw new ArgumentException(
                $"WebSocket 전송은 IP·DNS 종단만 받는다: {endPoint.GetType().Name}", nameof(endPoint)),
        };

        return new Uri($"ws://{authority}{_options.Path}");
    }
}
