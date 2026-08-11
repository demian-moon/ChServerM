using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Identity;
using ChServerM.Transports;

namespace ChServerM.Transport.Quic;

/// <summary>
/// QUIC 양방향 스트림으로 나가는 커넥션을 만드는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="QuicServerTransport"/> 의 짝이다. 커넥션 하나가 곧 양방향
/// 스트림 하나이고(ADR-0060), 같은 종단으로의 여러 커넥션은 <b>QUIC 연결 하나에
/// 다중화</b>된다 — 커넥션마다 UDP 소켓·TLS 핸드셰이크를 새로 내지 않는다
/// (HTTP 전송의 연결 공유와 같은 모양).
/// </para>
/// <para>
/// <b>재접속은 이 계층의 책임이 아니다</b>(<see cref="IClientTransport"/> 계약). 단,
/// 공유 QUIC 연결이 죽은 것을 발견하면 <b>다음 <see cref="ConnectAsync"/> 가 새로
/// 수립한다</b> — 이것은 재접속 정책이 아니라 연결 풀의 자기 치유다(죽은 연결을 계속
/// 돌려주는 풀은 풀이 아니다).
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다. 여러 스레드가 동시에 연결해도 된다.</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class QuicClientTransport : IClientTransport
{
    private readonly QuicTransportOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Dictionary<EndPoint, QuicConnection> _sharedConnections = new();
    private int _nextSlot;
    private int _disposed;

    /// <summary>클라이언트 전송을 만든다.</summary>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값. 서버와 같은 <see cref="QuicTransportOptions.AlpnProtocol"/> 을 써야 한다.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public QuicClientTransport(QuicTransportOptions? options = null)
    {
        options ??= new QuicTransportOptions();
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 반환 시점에 스트림이 열려 있다. 드레인·상한 초과 거부는 서버가 스트림을 즉시
    /// 중단하므로 <b>첫 입출력에서</b> 드러난다 — QUIC 스트림 열기는 로컬 연산이라
    /// (흐름 제어 크레딧 안에서) 서버 왕복 없이 성공하기 때문이다. 연결 자체의 거부
    /// (리스너 닫힘)는 여기서 예외로 드러난다.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endPoint"/>가 IP·DNS 종단이 아닐 때.</exception>
    /// <exception cref="PlatformNotSupportedException">이 환경에 QUIC 지원이 없을 때.</exception>
    /// <exception cref="QuicException">연결이 수립되지 못했을 때.</exception>
    public async ValueTask<IConnection> ConnectAsync(
        EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "이 환경은 QUIC 을 지원하지 않는다(msquic/TLS 스택). 조용한 폴백은 없다 — 전송 선택은 조립의 결정이다.");
        }

        QuicConnection connection = await GetOrCreateConnectionAsync(endPoint, cancellationToken)
            .ConfigureAwait(false);

        QuicStream stream;
        try
        {
            stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // 공유 연결이 죽어 있었다 — 한 번만 새로 수립해 다시 연다(풀의 자기 치유).
            // 두 번째도 실패하면 그것은 진짜 실패이고 호출자의 정책이다.
            await InvalidateConnectionAsync(endPoint, connection).ConfigureAwait(false);
            connection = await GetOrCreateConnectionAsync(endPoint, cancellationToken).ConfigureAwait(false);
            stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken)
                .ConfigureAwait(false);
        }

        return new QuicStreamConnection(
            NextConnectionId(), stream, connection.LocalEndPoint, endPoint, _options);
    }

    /// <summary>종단별 공유 QUIC 연결을 얻거나 새로 수립한다.</summary>
    private async Task<QuicConnection> GetOrCreateConnectionAsync(
        EndPoint endPoint, CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sharedConnections.TryGetValue(endPoint, out QuicConnection? existing))
            {
                return existing;
            }

            (EndPoint remote, string sniHost) = ResolveTarget(endPoint);
            SslApplicationProtocol alpn = new(_options.AlpnProtocol);

            QuicConnection created = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = remote,
                DefaultStreamErrorCode = QuicStreamConnection.AbortErrorCode,
                DefaultCloseErrorCode = 0x0B,

                // 서버가 여는 스트림은 이 대응에 없다.
                MaxInboundBidirectionalStreams = 0,
                MaxInboundUnidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [alpn],
                    TargetHost = _options.TargetHost ?? sniHost,
                    RemoteCertificateValidationCallback = _options.RemoteCertificateValidation,
                },
            }, cancellationToken).ConfigureAwait(false);

            _sharedConnections[endPoint] = created;
            return created;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>죽은 공유 연결을 풀에서 빼고 정리한다.</summary>
    private async Task InvalidateConnectionAsync(EndPoint endPoint, QuicConnection dead)
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // 다른 스레드가 이미 교체했을 수 있다 — 정확히 그 인스턴스일 때만 뺀다.
            if (_sharedConnections.TryGetValue(endPoint, out QuicConnection? current)
                && ReferenceEquals(current, dead))
            {
                _sharedConnections.Remove(endPoint);
            }
        }
        finally
        {
            _connectLock.Release();
        }

#pragma warning disable CA1031 // 죽은 연결의 정리 실패는 무시한다.
        try
        {
            await dead.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 이미 닫힌 연결.
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (QuicConnection connection in _sharedConnections.Values)
            {
#pragma warning disable CA1031 // 종료 경로다. 개별 연결의 정리 실패로 전체를 멈추지 않는다.
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 이미 닫힌 연결.
                }
#pragma warning restore CA1031
            }

            _sharedConnections.Clear();
        }
        finally
        {
            _connectLock.Release();
        }

        _connectLock.Dispose();
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    /// <summary>연결 대상과 TLS 검증용 호스트 이름을 정한다.</summary>
    private static (EndPoint Remote, string SniHost) ResolveTarget(EndPoint endPoint) => endPoint switch
    {
        IPEndPoint ip => (ip, ip.Address.ToString()),
        DnsEndPoint dns => (dns, dns.Host),
        _ => throw new ArgumentException(
            $"QUIC 전송은 IP·DNS 종단만 받는다: {endPoint.GetType().Name}", nameof(endPoint)),
    };
}
