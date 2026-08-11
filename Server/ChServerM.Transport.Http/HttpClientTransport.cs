using System;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Identity;
using ChServerM.Transports;

namespace ChServerM.Transport.Http;

/// <summary>
/// HTTP/2 양방향 스트림으로 나가는 커넥션을 만드는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="HttpServerTransport"/> 의 짝이다. 커넥션 하나가 곧
/// <c>POST {path}</c> 스트림 하나이며(ADR-0057), 요청 본문으로 프레임을 보내고 응답
/// 본문에서 프레임을 받는다. 여러 커넥션은 같은 HTTP/2 연결 위에 다중화된다 —
/// 커넥션마다 TCP 연결·TLS 핸드셰이크를 새로 내지 않는다.
/// </para>
/// <para>
/// <b>평문 HTTP/2(사전 지식) 전용이다.</b> 서버가 h2c 로만 듣기 때문이다
/// (<see cref="HttpServerTransport"/> 문서 참조).
/// </para>
/// <para>
/// <b>재접속은 이 계층의 책임이 아니다</b>(<see cref="IClientTransport"/> 계약).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다. 여러 스레드가 동시에 연결해도 된다.
/// </para>
/// </remarks>
public sealed class HttpClientTransport : IClientTransport
{
    private readonly HttpMessageInvoker _invoker;
    private readonly string _path;
    private readonly TimeSpan _shutdownTimeout;
    private int _nextSlot;

    /// <summary>클라이언트 전송을 만든다.</summary>
    /// <param name="options">전송 설정. <see langword="null"/>이면 기본값. 서버와 같은 <see cref="HttpTransportOptions.Path"/> 를 써야 한다.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public HttpClientTransport(HttpTransportOptions? options = null)
    {
        options ??= new HttpTransportOptions();
        options.Validate();

        _path = options.Path;
        _shutdownTimeout = options.ShutdownTimeout;

        // CA2000 억제 근거: 소유권이 HttpMessageInvoker(disposeHandler: true)로 넘어간다.
#pragma warning disable CA2000
        SocketsHttpHandler handler = new()
        {
            // HTTP/2 연결 하나의 동시 스트림 상한(서버 기본 100)에 닿으면 연결을 늘린다.
            // 이것이 없으면 101번째 커넥션이 앞선 스트림이 끝나기를 조용히 기다린다.
            EnableMultipleHttp2Connections = true,

            // 수신 흐름 제어 윈도. 서버 쪽 옵션과 같은 역할이다 — 응답(서버→클라이언트)
            // 방향의 백프레셔 임계값. SocketsHttpHandler 의 유효 범위(64KiB~16MiB)로 자른다.
            InitialHttp2StreamWindowSize =
                Math.Clamp(options.StreamReceiveWindowSize, 65_535, 16 * 1024 * 1024),
        };

        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
#pragma warning restore CA2000
    }

    /// <inheritdoc />
    /// <remarks>
    /// 반환 시점에 서버가 스트림을 수용(200)했음이 확인돼 있다 — 거부(503 드레인,
    /// 상한 초과)는 여기서 예외로 드러난다. 조용히 만든 커넥션이 첫 쓰기에서 죽는 것보다
    /// 연결 시점 실패가 낫다.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endPoint"/>가 IP·DNS 종단이 아닐 때.</exception>
    /// <exception cref="HttpRequestException">연결이 수립되지 못했을 때.</exception>
    /// <exception cref="InvalidOperationException">서버가 스트림을 거부했을 때(비 200).</exception>
    public async ValueTask<IConnection> ConnectAsync(
        EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        Uri uri = BuildUri(endPoint);

        // 커넥션의 송신 경로. FrameWriter → 이 파이프 → 펌프(RequestStreamContent) → 요청 본문.
        Pipe output = new(new PipeOptions(useSynchronizationContext: false));

        // CA2000 억제 근거: 성공 경로에서 요청을 Dispose 하면 안 된다 — Dispose 는 Content
        // 를 함께 폐기하는데, 그 Content(펌프)가 커넥션이 사는 동안 계속 돌아야 한다.
        // 실패 경로는 아래 catch 가 정리한다.
#pragma warning disable CA2000
        HttpRequestMessage request = new(HttpMethod.Post, uri)
        {
            // 양방향 스트리밍은 HTTP/2 전용 기능이다. 다운그레이드되면 요청 본문이 끝나야
            // 응답이 시작되므로, 정확히 2.0 이 아니면 실패시킨다.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new RequestStreamContent(output.Reader),
        };
#pragma warning restore CA2000

        HttpResponseMessage response;
        try
        {
            response = await _invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            await output.Reader.CompleteAsync().ConfigureAwait(false);
            throw;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            int status = (int)response.StatusCode;
            response.Dispose();
            await output.Reader.CompleteAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"서버가 스트림을 거부했다: HTTP {status.ToString(CultureInfo.InvariantCulture)} ({uri})");
        }

        Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HttpClientConnection(
            NextConnectionId(),
            PipeReader.Create(responseStream),
            output.Writer,
            response,
            endPoint,
            _shutdownTimeout);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // 살아 있는 스트림은 각 커넥션이 소유한다 — 여기서는 연결 풀만 닫는다.
        _invoker.Dispose();
        return ValueTask.CompletedTask;
    }

    private ConnectionId NextConnectionId() =>
        new((uint)Interlocked.Increment(ref _nextSlot), generation: 1);

    /// <summary>연결 대상 종단을 요청 URI 로 바꾼다.</summary>
    private Uri BuildUri(EndPoint endPoint)
    {
        UriBuilder builder = endPoint switch
        {
            IPEndPoint ip => new UriBuilder("http", ip.Address.ToString(), ip.Port, _path),
            DnsEndPoint dns => new UriBuilder("http", dns.Host, dns.Port, _path),
            _ => throw new ArgumentException(
                $"HTTP 전송은 IP·DNS 종단만 받는다: {endPoint.GetType().Name}", nameof(endPoint)),
        };

        return builder.Uri;
    }

    /// <summary>송신 파이프를 요청 본문으로 퍼올리는 콘텐츠.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="SerializeToStreamAsync(Stream, System.Net.TransportContext?)"/> 가 파이프가
    /// 완료될 때까지 돌아가는 것이 <b>양방향의 성립 조건</b>이다 — 응답은 이 직렬화가 끝나기
    /// 전에 이미 도착해 있다(HTTP/2 duplex). 길이를 계산할 수 없다고 답해야
    /// (<see cref="TryComputeLength"/>) 스트리밍 본문이 된다.
    /// </para>
    /// <para>
    /// 읽기 배치마다 플러시한다 — HTTP/2 쓰기 버퍼에 프레임이 고이면 상대의 응답을
    /// 기다리는 왕복 워크로드가 교착한다.
    /// </para>
    /// </remarks>
    private sealed class RequestStreamContent(PipeReader reader) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context) =>
            PumpAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            PumpAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private async Task PumpAsync(Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                // ⚠ 시작하자마자 빈 플러시로 HEADERS 를 밀어낸다. 이것이 없으면 **같은 HTTP/2
                // 연결의 두 번째 스트림부터** HEADERS 가 본문 첫 쓰기까지 클라이언트 버퍼에
                // 갇힌다 — 첫 스트림은 연결 수립 플러시에 편승해 통과하므로, 커넥션 하나짜리
                // 테스트는 전부 통과하고 두 개짜리에서만 연결 수립이 교착하는 형태로
                // 나타났다(2026-08-11 실측, ADR-0057). gRPC 클라이언트가 쓰는 것과 같은 해법이다.
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                while (true)
                {
                    ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    System.Buffers.ReadOnlySequence<byte> buffer = result.Buffer;

                    try
                    {
                        foreach (System.ReadOnlyMemory<byte> segment in buffer)
                        {
                            await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                        }

                        if (buffer.Length > 0)
                        {
                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        reader.AdvanceTo(buffer.End);
                    }

                    if (result.IsCompleted || result.IsCanceled)
                    {
                        return;
                    }
                }
            }
            finally
            {
                // 어떤 종료 경로에서도 읽기 끝을 닫는다 — 남겨두면 쓰기 측(FrameWriter)이
                // 백프레셔로 영원히 대기한다.
                await reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}
