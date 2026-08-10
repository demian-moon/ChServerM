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
/// <b>⚠⚠ 상대가 닫혀도 쓰기는 예외를 던지지 않는다 — <see cref="FlushResult"/> 를 봐야 안다.</b>
/// <see cref="PipeWriter.FlushAsync"/> 는 읽는 쪽이 완료됐을 때 <see cref="FlushResult.IsCompleted"/>
/// 만 <see langword="true"/> 로 돌려주고 <b>조용히 성공한 것처럼 반환</b>한다. 이 결과를 버리면
/// 링크는 살아 있는 척하며 그 뒤의 <b>모든</b> 프레임을 삼키고, 재연결은 영원히 트리거되지
/// 않는다 — 레거시의 조용한 유실(CLAUDE.md 9.6)과 정확히 같은 모양이다.
/// 그래서 여기서는 <see cref="FlushResult"/> 를 검사해 링크를 끊고 로그를 남긴다.
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
    private readonly ConcurrentDictionary<Task, byte> _pendingCloses = new();
    private readonly IClusterMembership _membership;
    private readonly ChServerMClient _client;
    private readonly ClusterPeerOptions _options;
    private readonly IServerLogger _logger;
    private readonly ArrayPool<byte> _pool;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>종료 토큰의 사본.</summary>
    /// <remarks>
    /// <b><see cref="CancellationTokenSource.Token"/> 을 해제 후에 읽으면 던진다.</b>
    /// 정리는 비동기라 <see cref="DisposeAsync"/> 가 <see cref="_shutdown"/> 을 해제한 뒤에도
    /// 소비자 루프가 살아 있을 수 있다 — 토큰을 미리 떠 두면 그 경합이 사라진다.
    /// 이미 취소된 토큰에 등록하는 것은 안전하다(콜백이 즉시 실행될 뿐이다).
    /// </remarks>
    private readonly CancellationToken _shutdownToken;

    private int _disposed;

    /// <summary>정리를 이미 돈 뷰 세대. 전송마다 링크를 훑지 않기 위한 것이다.</summary>
    private int _evictedGeneration;

    /// <summary>피어 집합을 만든다. <b>버퍼 풀은 필수다.</b></summary>
    /// <param name="membership">구성원 원천.</param>
    /// <param name="client">피어 접속에 쓸 클라이언트. <b>앱이 소유한다</b>.</param>
    /// <param name="options">큐 깊이와 페이로드 상한.</param>
    /// <param name="logger">로거.</param>
    /// <param name="bufferPool">
    /// 보낼 프레임을 담을 버퍼 풀. <b>기본값이 없다 — 아래 규약을 읽고 골라야 한다.</b>
    /// </param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">설정이 성립하지 않는다.</exception>
    /// <remarks>
    /// <para>
    /// <b>⚠ 왜 기본값이 없는가 — 프레임워크가 옳은 값을 계산할 수 없기 때문이다</b>(ADR-0051).
    /// 원래는 <see cref="ArrayPool{T}.Shared"/> 를 쓰는 4-인자 생성자가 있었고, 그것이
    /// 함정이었다: 큐를 깊게 잡으면 <b>경고도 오류도 없이</b> 프레임당 할당이 생기고
    /// 처리량이 <b>33% 떨어진다</b>(514.0 → 342.9 ns/프레임, BENCHMARKS 2026-08-10).
    /// 기본값을 전용 풀로 바꾸려 했으나 <b>필요한 보관 개수를 생성 시점에 알 수 없다</b>
    /// — 아래 규약 1 이 그 이유다. 옳은 기본값이 없으면 기본값을 두지 않는 것이 맞다
    /// (<c>FrameWriter</c> 의 옵션 매개변수를 전부 필수로 바꾼 것과 같은 자리, CLAUDE.md 8.1).
    /// </para>
    ///
    /// <para>
    /// <b>⚠⚠ 규약 1 — 최악 미처리 대여는 <c>SendQueueDepth × 피어 수</c> 다.</b>
    /// 보낼 큐는 <b>링크마다</b> 만들어지는데 이 풀은 <b>집합에 하나</b>이므로, 큐 깊이만
    /// 보고 보관 개수를 정하면 피어가 늘어난 만큼 작아진다 — 16 노드 클러스터라면
    /// <b>15 배 작고</b>, 그러면 위의 33% 손해가 그대로 돌아온다. 그리고 피어 수는
    /// 옵션이 아니라 <b>구성원 뷰가 런타임에 정하는 값</b>이라 생성자가 알 수 없다.
    /// </para>
    ///
    /// <para>
    /// <b>⚠⚠ 규약 2 — 전용 풀은 트리밍하지 않는다. 최고 수위를 영구 점유한다.</b>
    /// <see cref="ArrayPool{T}.Create(int, int)"/> 가 주는 구현에는
    /// <see cref="ArrayPool{T}.Shared"/> 가 가진 Gen2 트리밍이 <b>없다</b> — 실측에서
    /// 256 MiB 피크 후 10 분 유휴에 1 바이트도 반납하지 않았고, 같은 조건의
    /// <see cref="ArrayPool{T}.Shared"/> 는 9 분에 99.6% 를 반납했다. 버스트가 드문
    /// 서버라면 이 차이가 상주 메모리 전부다.
    /// </para>
    ///
    /// <para>
    /// <b>규약 3 — 전용 풀의 보유 상한은 다음과 같다</b>(실측 오차 ≤ 0.42%).
    /// <c>maxArrayLength</c> 는 2 의 거듭제곱으로 <b>올림</b>되며, 그보다 큰 대여는 풀에
    /// 담기지 않으므로 이것은 어림이 아니라 상한이다.
    /// <code>
    /// 상한 ≈ 2 × maxArrayLength × maxArraysPerBucket
    /// </code>
    /// 예: <c>MaxPayloadLength</c> 1 MiB · 깊이 1,024 · 피어 15 를 전부 덮으려면
    /// 최악 <b>30 GiB</b> 다. 그 값이 받아들일 수 없다면 줄여야 하는 것은 풀이 아니라
    /// <see cref="ClusterPeerOptions.SendQueueDepth"/> 나
    /// <see cref="ClusterPeerOptions.MaxPayloadLength"/> 다 — <b>상한은 풀의 성질이 아니라
    /// 그 두 옵션이 이미 약속한 최대 수요</b>이기 때문이다.
    /// </para>
    ///
    /// <para>
    /// <b>무엇을 넘길 것인가.</b> 얕은 큐(수백)와 작은 페이로드라면
    /// <see cref="ArrayPool{T}.Shared"/> 로 충분하고 그것이 메모리를 돌려주므로 낫다.
    /// 깊은 큐나 큰 페이로드라면 <see cref="ArrayPool{T}.Create(int, int)"/> 로 전용 풀을
    /// 만들되 위 세 규약으로 크기를 정한다.
    /// </para>
    ///
    /// <para>
    /// 부수 효과로 <b>반납 누수를 관측 가능</b>하게 만든다. 대여와 반납이 맞는지는
    /// 주석으로 보장할 수 없고(9.7), 세는 풀을 꽂아야만 테스트가 답할 수 있다.
    /// </para>
    /// </remarks>
    public ClusterPeerSet(
        IClusterMembership membership,
        ChServerMClient client,
        ClusterPeerOptions options,
        IServerLogger logger,
        ArrayPool<byte> bufferPool)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(bufferPool);

        options.Validate();

        _membership = membership;
        _client = client;
        _options = options;
        _logger = logger;
        _pool = bufferPool;
        _shutdownToken = _shutdown.Token;
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

        if (!TryGetLink(node!, out PeerLink link))
        {
            return new ValueTask<PeerSendStatus>(PeerSendStatus.Closed);
        }

        // ⚠ TryWrite 의 false 를 반드시 본다 — 레거시는 이것을 버려 부하 시 조용히 유실했다.
        byte[] buffer = _pool.Rent(payload.Length);
        payload.Span.CopyTo(buffer);

        if (!link.Queue.Writer.TryWrite(new PeerFrame(messageId, buffer, payload.Length)))
        {
            // 거절이 곧 누수면 안 된다. 큐에 못 들어간 대여물은 그 자리에서 반납한다.
            _pool.Return(buffer);
            return new ValueTask<PeerSendStatus>(
                link.IsClosing ? PeerSendStatus.Closed : PeerSendStatus.QueueFull);
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

        // 구성원 변화로 쫓겨난 링크의 정리는 기다리지 않고 시작했다(보내는 경로를 느리게
        // 하지 않으려고). 그 뒷정리를 여기서 마저 거둔다 — 안 그러면 이 객체가 사라진 뒤에도
        // 커넥션이 살아 있어, 종료를 기다리는 쪽(서버 StopAsync)이 이유 없이 붙잡힌다.
        foreach (Task pending in _pendingCloses.Keys)
        {
            _pendingCloses.TryRemove(pending, out _);
            await pending.ConfigureAwait(false);
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
                // 대신 태스크를 붙들어 둔다: DisposeAsync 가 이것을 거둔다.
                Track(link.CloseAsync().AsTask());
                Log(LinkClosedEvent, LogLevel.Information, id, "구성원에서 빠져 링크를 닫는다");
            }
        }
    }

    /// <summary>기다리지 않고 시작한 정리 작업을 붙들어 둔다.</summary>
    /// <remarks>
    /// 끝난 것은 스스로 빠진다 — 장수명 클러스터에서 이 사전이 무한히 자라면
    /// "정리하려고 만든 것이 누수" 가 된다.
    /// </remarks>
    private void Track(Task closing)
    {
        if (closing.IsCompleted)
        {
            return;
        }

        _pendingCloses[closing] = 0;
        _ = closing.ContinueWith(
            static (task, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _),
            _pendingCloses,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>이 피어의 링크를 얻는다. 없으면 만든다.</summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/> 를
    /// 쓰지 않는다.</b> 그 팩토리는 경합 시 <b>여러 번 불릴 수 있고</b>, 진 쪽이 만든 링크는
    /// 사전에 들어가지 않은 채 <b>소비자 태스크와 채널만 남긴다</b> — 아무도 닫지 않는
    /// 고아다. 여기서는 <b>먼저 사전에 넣고, 이긴 쪽만 소비자를 시작한다</b>. 진 쪽이
    /// 만든 것은 시작된 것이 없으므로 그냥 버리면 된다.
    /// </para>
    /// <para>
    /// 넣은 직후 종료가 시작됐는지 다시 본다. 이 재확인이 없으면 <see cref="DisposeAsync"/> 의
    /// 정리 루프를 <b>지나친 뒤에</b> 들어온 링크가 영영 닫히지 않는다.
    /// </para>
    /// </remarks>
    private bool TryGetLink(ClusterNode node, out PeerLink link)
    {
        while (true)
        {
            if (_links.TryGetValue(node.Id, out PeerLink? existing))
            {
                link = existing;
                return true;
            }

            // ⚠ 유계 채널이다. 무제한이면 느린 피어 하나가 프로세스를 OOM 으로 끌고 간다.
            PeerLink created = new(
                node,
                Channel.CreateBounded<PeerFrame>(
                    new BoundedChannelOptions(_options.SendQueueDepth)
                    {
                        SingleReader = true,
                        FullMode = BoundedChannelFullMode.Wait,
                    }));

            if (!_links.TryAdd(node.Id, created))
            {
                // 졌다. 소비자를 아직 시작하지 않았으므로 버리면 그만이다.
                continue;
            }

            created.Start(RunLinkAsync(created));
            Log(LinkOpenedEvent, LogLevel.Information, node.Id, "피어 링크를 연다");

            if (Volatile.Read(ref _disposed) == 1 && _links.TryRemove(node.Id, out PeerLink? orphan))
            {
                _ = orphan.CloseAsync().AsTask();
                link = null!;
                return false;
            }

            link = created;
            return true;
        }
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
            while (await reader.WaitToReadAsync(_shutdownToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out PeerFrame frame))
                {
                    try
                    {
                        if (!await TryWriteAsync(link, frame).ConfigureAwait(false))
                        {
                            // 상대가 읽기를 끝냈다. 예외가 아니므로 여기서 끊지 않으면
                            // 이 링크는 살아 있는 척하며 이후 전부를 삼킨다.
                            await DropAsync(link, "상대가 링크를 닫았다 — 다음 전송이 다시 연다")
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        // ⚠ 항목별로 잡는다. 나쁜 프레임 하나가 이 피어의 큐 전체를 죽이면
                        //   그 노드로 가는 모든 트래픽이 함께 멈춘다(9.2).
                        await DropAsync(link, error.Message).ConfigureAwait(false);
                    }
                    finally
                    {
                        _pool.Return(frame.Buffer);
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
                _pool.Return(leftover.Buffer);
            }
        }
    }

    /// <summary>프레임 하나를 쓴다.</summary>
    /// <returns>링크가 여전히 쓸 만하면 <see langword="true"/>. 상대가 닫았으면 <see langword="false"/>.</returns>
    /// <remarks>
    /// <b>⚠⚠ 반환값이 이 메서드의 존재 이유다.</b> <see cref="PipeWriter.FlushAsync"/> 는
    /// 읽는 쪽이 완료됐을 때 <b>던지지 않고</b> <see cref="FlushResult.IsCompleted"/> 만 세운다.
    /// 그것을 <c>await</c> 하고 버리면 성공과 구분되지 않는다 — 이 프레임워크가 레거시에서
    /// 승계를 거부한 "조용한 유실" 이 정확히 이 모양이었다(9.6).
    /// </remarks>
    private async ValueTask<bool> TryWriteAsync(PeerLink link, PeerFrame frame)
    {
        IConnection connection = await EnsureConnectedAsync(link).ConfigureAwait(false);

        FlushResult result = await FrameWriter.WriteFrameAsync(
            connection.Output,
            _client.Encoder,
            frame.MessageId,
            frame.Buffer.AsSpan(0, frame.Length),
            FrameFlags.None,
            link.NextSequence(),
            connection.ConnectionClosed).ConfigureAwait(false);

        return !result.IsCompleted && !result.IsCanceled;
    }

    /// <summary>쓸 수 있는 커넥션을 보장한다. 없거나 죽었으면 새로 연다.</summary>
    /// <remarks>
    /// <b>쓰기 전에 죽은 것을 발견하면 그 프레임은 살릴 수 있다.</b> 아직 보내지 않았으므로
    /// 새 커넥션에 쓰는 것은 <b>재전송이 아니다</b> — "재전송하지 않는다" 는 계약은
    /// <b>이미 파이프에 넘긴</b> 프레임에만 적용된다. 이 구분이 없으면 상대가 재기동할 때마다
    /// 굳이 한 장씩 잃는다.
    /// </remarks>
    private async ValueTask<IConnection> EnsureConnectedAsync(PeerLink link)
    {
        if (link.Connection is { } live && !live.ConnectionClosed.IsCancellationRequested && !link.IsReadLoopFinished)
        {
            return live;
        }

        // 죽은 커넥션을 먼저 놓아준다. 놓지 않으면 재연결마다 소켓이 하나씩 샌다.
        await DetachAsync(link).ConfigureAwait(false);

        ClientSession session = await _client
            .ConnectAsync(link.Node.EndPoint, _shutdownToken)
            .ConfigureAwait(false);

        link.Attach(session);
        return session.Connection;
    }

    /// <summary>죽은 링크를 놓아주고 이유를 남긴다. 다음 전송이 다시 연다.</summary>
    private async ValueTask DropAsync(PeerLink link, string reason)
    {
        await DetachAsync(link).ConfigureAwait(false);
        Log(LinkFailedEvent, LogLevel.Warning, link.Node.Id, reason);
    }

    /// <summary>커넥션을 놓아주고, 읽기 루프는 <b>기다리지 않고</b> 관측만 예약한다.</summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ 읽기 루프를 <i>기다리지</i> 않는다. <i>관측</i>만 한다.</b> 둘은 다르다 —
    /// 관측은 예외가 <see cref="TaskScheduler.UnobservedTaskException"/> 로 새는 것을 막고,
    /// 기다림은 <b>남의 태스크가 끝나야 우리가 진행할 수 있게</b> 만든다.
    /// </para>
    /// <para>
    /// 기다리면 어떻게 되는지는 고의 회귀로 확인했다: 커넥션 해제를 빠뜨린 구현에서
    /// 읽기 루프가 끝나지 않자 <b>보내는 경로와 종료가 함께 무한 정지</b>했다. 커넥션이
    /// 우리 자원이라 해제는 기다려도 되지만, 그 결과로 끝나야 할 루프는 기다리지 않는다 —
    /// 인메모리 전송이 종료 드레인에 상한을 둔 것과 같은 판단이다(2026-08-04 감사 H3).
    /// </para>
    /// </remarks>
    private static async ValueTask DetachAsync(PeerLink link)
    {
        if (await link.DetachAsync().ConfigureAwait(false) is { } loop)
        {
            _ = PeerLink.ObserveAsync(loop);
        }
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

    /// <summary>피어 하나의 링크 — 큐, 커넥션, 읽기 루프, 일련번호.</summary>
    /// <remarks>
    /// <para>
    /// <b>스레드 규약.</b> <see cref="Connection"/>·<see cref="ReadLoop"/> 는
    /// <b>소비자 하나만</b> 만진다(<see cref="RunLinkAsync"/>). <see cref="Queue"/> 만
    /// 다중 생산자에 노출된다. <see cref="CloseAsync"/> 는 소비자를 먼저 끝내고 만진다.
    /// </para>
    /// <para>
    /// <b>⚠ 읽기 루프 태스크를 반드시 붙들고 있는다.</b> <see cref="ChServerMClient.ConnectAsync"/>
    /// 가 돌려주는 <see cref="ClientSession.Completion"/> 을 버리면 (1) 그 루프가 던진 예외가
    /// <b>아무 데서도 관측되지 않고</b>, (2) 무엇보다 <b>루프의 완료가 곧 "이 링크는 죽었다"</b>
    /// 라는 가장 이른 신호인데 그것을 못 쓰게 된다.
    /// </para>
    /// </remarks>
    private sealed class PeerLink(ClusterNode node, Channel<PeerFrame> queue)
    {
        private uint _sequence;
        private int _closing;

        public ClusterNode Node { get; } = node;

        public Channel<PeerFrame> Queue { get; } = queue;

        public IConnection? Connection { get; private set; }

        /// <summary>이 링크의 읽기 루프. 완료됐다면 커넥션은 이미 끝난 것이다.</summary>
        public Task? ReadLoop { get; private set; }

        /// <summary>보낼 큐의 소비자.</summary>
        public Task? Consumer { get; private set; }

        /// <summary>닫히는 중이거나 닫혔다. 큐 거절을 포화와 구분하기 위한 것이다.</summary>
        public bool IsClosing => Volatile.Read(ref _closing) == 1;

        /// <summary>읽기 루프가 끝났는가 — 상대가 사라졌다는 가장 이른 신호다.</summary>
        public bool IsReadLoopFinished => ReadLoop is { IsCompleted: true };

        /// <summary>소비자를 시작한다. 사전에 등록된 뒤 정확히 한 번 불린다.</summary>
        public void Start(Task consumer) => Consumer = consumer;

        /// <summary>커넥션과 읽기 루프의 소유권을 가져온다.</summary>
        public void Attach(ClientSession session)
        {
            Connection = session.Connection;
            ReadLoop = session.Completion;
        }

        /// <summary>다음 프레임 일련번호. 소비자가 하나뿐이라 동기화가 필요 없다.</summary>
        public uint NextSequence() => ++_sequence;

        /// <summary>커넥션을 놓아준다. 큐와 소비자는 그대로 살아 다음 전송을 받는다.</summary>
        /// <remarks>
        /// <b>여기서 <see cref="IAsyncDisposable.DisposeAsync"/> 를 부르지 않으면 소켓이 샌다.</b>
        /// 예전 구현은 참조만 <see langword="null"/> 로 만들어, 끊길 때마다 커넥션과 그
        /// 읽기 루프가 하나씩 쌓였다.
        /// </remarks>
        /// <returns>놓아준 읽기 루프. 호출자가 관측을 책임진다. 없으면 <see langword="null"/>.</returns>
        public async ValueTask<Task?> DetachAsync()
        {
            Task? loop = ReadLoop;
            ReadLoop = null;

            // ⚠ 순서가 중요하다 — **커넥션을 먼저 해제해야** 읽기 루프가 끝난다.
            //   반대로 하면 루프가 영원히 대기하고, 그것을 기다리는 쪽이 함께 멈춘다.
            if (Connection is { } connection)
            {
                Connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            return loop;
        }

        public async ValueTask CloseAsync()
        {
            Volatile.Write(ref _closing, 1);
            Queue.Writer.TryComplete();

            if (Consumer is { } consumer)
            {
                Consumer = null;
                await ObserveAsync(consumer).ConfigureAwait(false);
            }

            // 여기서도 기다리지 않는다 — 커넥션은 이미 해제됐으므로 루프는 스스로 끝난다.
            // 그것을 기다리면 종료가 커넥션 하나에 볼모로 잡힌다(2026-08-04 감사 H3).
            if (await DetachAsync().ConfigureAwait(false) is { } loop)
            {
                _ = ObserveAsync(loop);
            }
        }

        /// <summary>태스크의 결과를 관측한다. 종료 경로에서 예외로 정리를 멈추지 않는다.</summary>
#pragma warning disable CA1031 // 정리 경로다. 던지면 나머지 링크가 정리되지 않는다.
        public static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 관측하는 것이 목적이다 — 버려두면 TaskScheduler.UnobservedTaskException 이 된다.
            }
        }
#pragma warning restore CA1031
    }
}
