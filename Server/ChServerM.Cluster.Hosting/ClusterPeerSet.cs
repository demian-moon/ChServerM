using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;

namespace ChServerM.Cluster.Hosting;

/// <summary>피어로 보내기의 결과.</summary>
/// <remarks>
/// <b>⚠ <see cref="Sent"/> 는 "상대가 받았다" 가 아니다.</b> 보낼 큐에 들어가 파이프에
/// 기록됐다는 뜻이며, 그 뒤에 링크가 끊기면 그 프레임은 사라진다. 전달 보장이 필요하면
/// 그것은 <b>응용 계층의 확인 응답</b>으로 만든다 — 프레임워크가 흉내 내면
/// "보냈으니 도착했겠지" 라는 잘못된 믿음만 생긴다.
/// </remarks>
public enum PeerSendStatus
{
    /// <summary>쓰이지 않는 값. 0 을 성공으로 오독하지 않기 위해 비워 둔다.</summary>
    Unspecified = 0,

    /// <summary>보낼 큐에 들어갔다. <b>도착 보장이 아니다.</b></summary>
    Sent = 1,

    /// <summary>그 노드가 지금 구성원이 아니다.</summary>
    NotAMember = 2,

    /// <summary>자기 자신에게 보내려 했다. <b>로컬 단락을 빠뜨린 것</b>이다.</summary>
    /// <remarks>
    /// 조용히 성공시키면 자기에게 연결하는 커넥션이 생겨 접속 한도와 통계를 오염시킨다
    /// (ADR-0049). 결과로 돌려주어 호출자가 <see cref="ClusterRoute"/> 를 쓰게 만든다.
    /// </remarks>
    Loopback = 3,

    /// <summary>보낼 큐가 가득 찼다. <b>거부가 붕괴보다 낫다.</b></summary>
    QueueFull = 4,

    /// <summary>집합이 이미 종료됐다.</summary>
    Closed = 5,
}

/// <summary>피어 링크 설정.</summary>
public sealed class ClusterPeerOptions
{
    /// <summary>피어당 보낼 큐 깊이. 기본 1024.</summary>
    /// <remarks>
    /// <b>무제한 큐를 두지 않는다</b>(CLAUDE.md 9.6). 상대가 느리면 메모리가 무한히 늘어
    /// OOM 으로 죽는다 — 깊이를 정하고 넘치면 <see cref="PeerSendStatus.QueueFull"/> 로
    /// 거절한다.
    /// </remarks>
    public int SendQueueDepth { get; set; } = 1024;

    /// <summary>한 프레임 페이로드의 상한. 기본 1 MiB.</summary>
    /// <remarks>큐에 넣기 전에 복사하므로, 상한이 없으면 큐 깊이만으로 메모리를 묶을 수 없다.</remarks>
    public int MaxPayloadLength { get; set; } = 1024 * 1024;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 성립하지 않는다.</exception>
    public void Validate()
    {
        if (SendQueueDepth <= 0)
        {
            throw new InvalidOperationException($"{nameof(SendQueueDepth)} 는 1 이상이어야 한다.");
        }

        if (MaxPayloadLength <= 0)
        {
            throw new InvalidOperationException($"{nameof(MaxPayloadLength)} 는 1 이상이어야 한다.");
        }
    }
}

/// <summary>
/// 구성원 뷰를 따라 <b>피어별 아웃바운드 링크</b>를 유지하고 프레임을 보낸다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 새 전송을 만들지 않기 위해서다</b>(ADR-0049). 노드가 노드에 접속하는 것은
/// 그냥 클라이언트 접속이므로, 전송·프레이밍·TLS 축은 앱이 <see cref="ClientBuilder"/> 로
/// 이미 고른 것을 그대로 쓴다. 여기가 더하는 것은 <b>피어별 링크의 생애</b>뿐이다.
/// </para>
///
/// <para>
/// <b>⚠ 재연결은 한다. 재전송은 하지 않는다.</b> 링크가 끊기면 <b>다음 전송</b>에서 다시
/// 연결한다 — 피어 링크에는 다시 세울 세션 상태가 없으므로 <see cref="ClientBuilder"/> 가
/// 재접속을 감추지 않기로 한 이유(상위 계층이 세션 재수립을 건너뛴다)가 여기엔 적용되지
/// 않는다. 그러나 <b>끊길 때 큐에 있던 프레임은 사라지고 다시 보내지 않는다</b> —
/// 재전송은 중복을 만들고, 중복을 다루는 것은 응용의 몫이다.
/// </para>
///
/// <para>
/// <b>⚠ 피어당 큐 하나 + 소비자 하나.</b> <see cref="PipeWriter"/> 는 동시 기록을 허용하지
/// 않으므로 어떻게든 직렬화가 필요한데, 락 대신 <b>유계 채널</b>을 쓴다 — 직렬화·백프레셔·
/// 거절을 한 구조로 얻는다(9.1: 공유를 보호하지 말고 없앤다).
/// </para>
/// <para>
/// <b>⚠⚠ 가득 찬 큐에서 <see cref="ChannelWriter{T}.TryWrite"/> 의 <c>false</c> 를
/// 반드시 본다.</b> 레거시는 <c>Wait</c> 모드에 <c>TryWrite</c> 를 써 놓고 반환값을 버려
/// <b>부하 시 패킷을 조용히 유실</b>했다(9.6). 여기서는 그 <c>false</c> 가 곧
/// <see cref="PeerSendStatus.QueueFull"/> 이고, 호출자에게 그대로 나간다 —
/// <b>조용하지 않은 거절</b>이다.
/// </para>
///
/// <para>
/// <b>⚠ 자기 자신에게 보내면 <see cref="PeerSendStatus.Loopback"/> 이다.</b> 조용히
/// 성공시키지 않는다 — 로컬 단락(<see cref="ClusterRoute"/>)을 빠뜨린 코드를 드러내야 한다.
/// </para>
///
/// <para>
/// <b>소유권.</b> <see cref="ChServerMClient"/> 는 <b>앱이 소유한다</b> — 이 타입은 빌려 쓸
/// 뿐 <see cref="DisposeAsync"/> 에서 정리하지 않는다. 커넥션(링크)은 이 타입이 소유하고 닫는다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> <see cref="SendAsync"/> 는 여러 스레드에서 동시에 호출해도 안전하다.
/// 피어별 쓰기는 채널이 직렬화하므로 <see cref="PipeWriter"/> 를 두 스레드가 만지지 않는다.
/// </para>
/// </remarks>
public sealed class ClusterPeerSet : IAsyncDisposable
{
    private static readonly EventId LinkOpenedEvent = new(2010, "PeerLinkOpened");
    private static readonly EventId LinkClosedEvent = new(2011, "PeerLinkClosed");
    private static readonly EventId LinkFailedEvent = new(2012, "PeerLinkFailed");

    private readonly ConcurrentDictionary<NodeId, PeerLink> _links = new();
    private readonly IClusterMembership _membership;
    private readonly ChServerMClient _client;
    private readonly ClusterPeerOptions _options;
    private readonly IServerLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>정리를 이미 돈 뷰 세대. 전송마다 링크를 훑지 않기 위한 것이다.</summary>
    private int _evictedGeneration;

    /// <summary>피어 집합을 만든다.</summary>
    /// <param name="membership">구성원 원천.</param>
    /// <param name="client">피어 접속에 쓸 클라이언트. <b>앱이 소유한다</b>.</param>
    /// <param name="options">큐 깊이와 페이로드 상한.</param>
    /// <param name="logger">로거.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">설정이 성립하지 않는다.</exception>
    public ClusterPeerSet(
        IClusterMembership membership,
        ChServerMClient client,
        ClusterPeerOptions options,
        IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _membership = membership;
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <summary>지금 링크가 열려 있는 피어 수. 진단용이다.</summary>
    public int OpenLinkCount => _links.Count;

    /// <summary>피어에게 프레임 하나를 보낸다.</summary>
    /// <param name="target">받을 노드.</param>
    /// <param name="messageId">메시지 식별자.</param>
    /// <param name="payload">페이로드. 큐에 넣기 전에 <b>복사</b>하므로 반환 후 재사용해도 된다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>보내기 결과. <see cref="PeerSendStatus.Sent"/> 도 <b>도착 보장이 아니다</b>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">페이로드가 상한을 넘었다.</exception>
    /// <remarks>
    /// <b>큐에 넣기까지만 기다린다.</b> 실제 기록은 피어별 소비자가 한다 — 느린 피어 하나가
    /// 호출자를 붙잡지 않아야 다른 파티션이 함께 멈추지 않는다.
    /// </remarks>
    public ValueTask<PeerSendStatus> SendAsync(
        NodeId target,
        MessageId messageId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, _options.MaxPayloadLength);

        if (Volatile.Read(ref _disposed) == 1)
        {
            return new ValueTask<PeerSendStatus>(PeerSendStatus.Closed);
        }

        if (target == _membership.Self.Id)
        {
            // 조용히 성공시키면 자기에게 연결하는 커넥션이 생긴다. 드러낸다.
            return new ValueTask<PeerSendStatus>(PeerSendStatus.Loopback);
        }

        ClusterView view = _membership.Current;

        // ⚠ 정리를 **구성원 확인보다 먼저** 한다. 뒤에 두면 떠난 노드에게 보낼 때
        //   NotAMember 로 먼저 빠져나가 정리에 도달하지 못하고, 클러스터가 자기 혼자로
        //   줄면 링크가 영영 닫히지 않는다(테스트가 잡은 실제 버그다).
        EvictDepartedLinksOnce(view);

        if (!view.TryGetNode(target, out ClusterNode? node))
        {
            return new ValueTask<PeerSendStatus>(PeerSendStatus.NotAMember);
        }

        PeerLink link = _links.GetOrAdd(target, _ => CreateLink(node!));

        // ⚠ TryWrite 의 false 를 반드시 본다 — 레거시는 이것을 버려 부하 시 조용히 유실했다.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.Span.CopyTo(buffer);

        if (!link.Queue.Writer.TryWrite(new PeerFrame(messageId, buffer, payload.Length)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return new ValueTask<PeerSendStatus>(PeerSendStatus.QueueFull);
        }

        _ = cancellationToken;
        return new ValueTask<PeerSendStatus>(PeerSendStatus.Sent);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 모든 링크를 닫고 소비자가 끝날 때까지 기다린다. <b>클라이언트는 정리하지 않는다</b> —
    /// 앱이 소유한다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (NodeId id in _links.Keys)
        {
            if (_links.TryRemove(id, out PeerLink? link))
            {
                await link.CloseAsync().ConfigureAwait(false);
            }
        }

        _shutdown.Dispose();
    }

    /// <summary>구성이 바뀌었을 때만 정리를 돈다.</summary>
    /// <remarks>
    /// <b>전송마다 링크 사전을 훑지 않기 위한 것이다.</b> 세대가 그대로면 정리할 것도
    /// 없으므로, 핫패스에는 <c>Volatile</c> 읽기와 비교 하나만 남는다.
    /// CAS 에서 진 쪽은 이긴 쪽이 정리한다는 것을 알고 그냥 지나간다.
    /// </remarks>
    private void EvictDepartedLinksOnce(ClusterView view)
    {
        int seen = Volatile.Read(ref _evictedGeneration);
        if (seen == view.Generation)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _evictedGeneration, view.Generation, seen) == seen)
        {
            EvictDepartedLinks(view);
        }
    }

    /// <summary>구성원에서 빠진 노드의 링크를 닫는다.</summary>
    /// <remarks>
    /// 떠난 노드로 가는 커넥션을 남겨 두면 소켓과 큐가 그대로 살아 있고, 그 노드가 다시
    /// 다른 역할로 들어올 때 <b>옛 링크로 보내게</b> 된다.
    /// </remarks>
    private void EvictDepartedLinks(ClusterView view)
    {
        foreach (NodeId id in _links.Keys)
        {
            if (!view.Contains(id) && _links.TryRemove(id, out PeerLink? link))
            {
                // 닫기를 기다리지 않는다 — 보내는 경로가 정리 때문에 느려지면 안 된다.
                _ = link.CloseAsync().AsTask();
                Log(LinkClosedEvent, LogLevel.Information, id, "구성원에서 빠져 링크를 닫는다");
            }
        }
    }

    private PeerLink CreateLink(ClusterNode node)
    {
        // ⚠ 유계 채널이다. 무제한이면 느린 피어 하나가 프로세스를 OOM 으로 끌고 간다.
        Channel<PeerFrame> queue = Channel.CreateBounded<PeerFrame>(
            new BoundedChannelOptions(_options.SendQueueDepth)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        PeerLink link = new(node, queue);
        link.Consumer = RunLinkAsync(link);

        Log(LinkOpenedEvent, LogLevel.Information, node.Id, "피어 링크를 연다");
        return link;
    }

    /// <summary>피어 하나의 소비 루프. 큐에서 꺼내 파이프에 쓴다.</summary>
    /// <remarks>
    /// <b>⚠ 대여 버퍼는 반드시 <c>finally</c> 로 반납한다.</b> 예외 하나로 풀이 새면
    /// 증상이 한참 뒤 할당 폭증으로 나타나 원인을 찾기 어렵다(CLAUDE.md 9.2·9.7).
    /// </remarks>
    private async Task RunLinkAsync(PeerLink link)
    {
        ChannelReader<PeerFrame> reader = link.Queue.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out PeerFrame frame))
                {
                    try
                    {
                        await WriteAsync(link, frame).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        // ⚠ 항목별로 잡는다. 나쁜 프레임 하나가 이 피어의 큐 전체를 죽이면
                        //   그 노드로 가는 모든 트래픽이 함께 멈춘다(9.2).
                        link.Drop();
                        Log(LinkFailedEvent, LogLevel.Warning, link.Node.Id, error.Message);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame.Buffer);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 경로다.
        }
        finally
        {
            // 큐에 남은 대여 버퍼를 전부 반납한다 — 종료가 곧 누수가 되지 않게.
            while (reader.TryRead(out PeerFrame leftover))
            {
                ArrayPool<byte>.Shared.Return(leftover.Buffer);
            }
        }
    }

    private async ValueTask WriteAsync(PeerLink link, PeerFrame frame)
    {
        IConnection connection = link.Connection
            ?? await OpenAsync(link).ConfigureAwait(false);

        await FrameWriter.WriteFrameAsync(
            connection.Output,
            _client.Encoder,
            frame.MessageId,
            frame.Buffer.AsSpan(0, frame.Length),
            FrameFlags.None,
            link.NextSequence(),
            connection.ConnectionClosed).ConfigureAwait(false);
    }

    /// <summary>링크를 (다시) 연다. 끊긴 뒤 첫 전송에서 불린다.</summary>
    private async ValueTask<IConnection> OpenAsync(PeerLink link)
    {
        ClientSession session = await _client
            .ConnectAsync(link.Node.EndPoint, _shutdown.Token)
            .ConfigureAwait(false);

        link.Attach(session);
        return session.Connection;
    }

    private void Log(EventId eventId, LogLevel level, NodeId node, string reason)
    {
        if (_logger.IsEnabled(level))
        {
            _logger.Log(
                level, eventId, (node, reason), null,
                static (state, _) => $"피어 {state.node.Value}: {state.reason}");
        }
    }

    /// <summary>큐에 실리는 한 프레임. 버퍼는 <b>풀 대여물</b>이며 소비자가 반납한다.</summary>
    private readonly record struct PeerFrame(MessageId MessageId, byte[] Buffer, int Length);

    /// <summary>피어 하나의 링크 — 큐, 커넥션, 일련번호.</summary>
    private sealed class PeerLink(ClusterNode node, Channel<PeerFrame> queue)
    {
        private uint _sequence;

        public ClusterNode Node { get; } = node;

        public Channel<PeerFrame> Queue { get; } = queue;

        public IConnection? Connection { get; private set; }

        public Task? Consumer { get; set; }

        /// <summary>커넥션 소유권을 가져온다.</summary>
        public void Attach(ClientSession session) => Connection = session.Connection;

        /// <summary>끊긴 링크를 잊는다. 다음 전송이 다시 연다.</summary>
        public void Drop() => Connection = null;

        /// <summary>다음 프레임 일련번호. 소비자가 하나뿐이라 동기화가 필요 없다.</summary>
        public uint NextSequence() => ++_sequence;

        public async ValueTask CloseAsync()
        {
            Queue.Writer.TryComplete();

            if (Consumer is { } consumer)
            {
                try
                {
                    await consumer.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 종료 경로다.
                }
            }

            if (Connection is { } connection)
            {
                Connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
