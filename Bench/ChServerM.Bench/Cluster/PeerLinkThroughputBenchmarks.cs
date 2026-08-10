using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Cluster;
using ChServerM.Cluster.Hosting;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;
using ChServerM.Transport.InMemory;

namespace ChServerM.Bench.Cluster;

/// <summary>
/// 피어 링크의 <b>부하 아래 처리량</b> — ADR-0050 이 미검증으로 남긴 마지막 항목.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 벤치의 질문.</b> 피어당 "유계 채널 + 소비자 하나" 라는 구조를 고른 근거는
/// 락 없이 직렬화·백프레셔·거절을 한꺼번에 얻는다는 것이었다. 그 대가로 <b>피어 하나의
/// 처리량이 소비자 하나에 묶인다</b> — 그 천장이 어디인지를 수치로 남기지 않으면
/// "충분히 빠르다" 는 짐작이다(CLAUDE.md 2 "측정 없는 최적화 금지").
/// </para>
/// <para>
/// <b>무엇을 재지 <i>않는가</i>.</b> 이것은 소켓 성능 측정이 아니다. 인메모리 전송을 써서
/// <b>피어 링크 계층이 더하는 비용</b>(큐 왕복 · 복사 · 프레이밍 · 일련번호)만 남긴다.
/// TCP 종단 수치는 별개 측정이며 거기서는 네트워크가 지배한다.
/// </para>
/// <para>
/// <b>큐 깊이를 프레임 수보다 크게 잡는다.</b> 여기서 거절이 섞이면 재시도 루프가
/// 측정에 들어와 무엇을 잰 것인지 알 수 없게 된다. 포화 동작은 처리량이 아니라
/// <b>계약</b>이므로 테스트가 검증한다(<c>QueueFull_isRejected_notSilentlyDropped</c>).
/// </para>
/// <para>
/// <b>할당량이 1급 지표다.</b> 보낼 때 페이로드를 풀에서 빌려 복사하므로 프레임당 힙 할당은
/// 0 이어야 한다. 이 열이 0 이 아니면 대여 경로 어딘가가 새고 있다는 뜻이다.
/// </para>
/// <para>
/// <b>⚠⚠ <see cref="FrameCount"/> 가 1,000 인 것은 임의의 숫자가 아니다 — 실측으로 정했다.</b>
/// 10,000 으로 두고 재니 프레임당 <b>27 B(64 B 페이로드) · 308 B(1 KiB 페이로드)</b> 가
/// 할당됐고, 1,000 으로 줄이니 <b>양쪽 다 0 B</b> 가 됐다(2026-08-10, ENV-B).
/// 원인은 링크가 아니라 <b>미처리 대여물의 수</b>다: <see cref="System.Buffers.ArrayPool{T}.Shared"/> 는
/// 버킷당 보관 개수가 유한해서, 큐에 수만 장이 동시에 떠 있으면 대여가 풀을 빗나가 새로
/// 할당하고 반납은 버려진다. <b>즉 "무할당" 은 큐 깊이에 조건부다.</b>
/// </para>
/// <para>
/// 이것이 <see cref="ClusterPeerSet"/> 에 전용 풀 생성자를 둔 실측 근거다 — 큐를 깊게
/// 잡아야 하는 배치 경로는 공유 풀이 아니라 자기 풀을 줘야 한다.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkDotNet.Attributes.Config(typeof(BenchConfig))]
public class PeerLinkThroughputBenchmarks
{
    private const int FrameCount = 1_000;

    private static readonly MessageId PeerMessage = new(400);

    private readonly InMemoryTransportHub _hub = new();
    private readonly InMemoryTransportOptions _transportOptions = new();

    private ChServerMServer _peer = null!;
    private ChServerMClient _client = null!;

    // 전송은 빌더가 소유하지만, 분석기(CA2000)가 그 이관을 볼 수 없다.
    // 참조를 들고 정리에서 함께 해제한다 — 두 해제 모두 멱등이다.
    private InMemoryServerTransport _serverTransport = null!;
    private InMemoryClientTransport _clientTransport = null!;
    private StaticClusterMembership _membership = null!;
    private ClusterPeerSet _peers = null!;
    private byte[] _payload = [];

    private TaskCompletionSource _drained = null!;
    private int _arrived;

    /// <summary>페이로드 크기. 작은 프레임에서는 고정 비용이, 큰 프레임에서는 복사가 지배한다.</summary>
    [Params(64, 1024)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        FramingOptions framing = new() { MaxPayloadLength = 8 * 1024 };
        InMemoryEndPoint peerEndPoint = new($"bench-peer-{Guid.NewGuid():N}");

        _serverTransport = new InMemoryServerTransport(_hub, peerEndPoint, _transportOptions);
        _clientTransport = new InMemoryClientTransport(_hub, null, _transportOptions);

        _peer = new ServerBuilder()
            .UseTransport(_serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(d => d.MapRaw(PeerMessage, _ =>
            {
                // 페이로드를 복사하지 않는다 — 수신 측 비용이 측정에 섞이면
                // 피어 링크가 더하는 비용을 분리할 수 없다.
                if (Interlocked.Increment(ref _arrived) == FrameCount)
                {
                    _drained.TrySetResult();
                }

                return ValueTask.FromResult(DispatchStatus.Handled);
            }))
            .Build();

        _peer.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

        _client = new ClientBuilder()
            .UseTransport(_clientTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .Build();

        StaticClusterMembershipOptions membership = new() { SelfId = new NodeId(1) };
        membership.Nodes.Add((new NodeId(1), "self", new InMemoryEndPoint("bench-self")));
        membership.Nodes.Add((new NodeId(2), "peer", peerEndPoint));
        _membership = new StaticClusterMembership(membership);

        _peers = new ClusterPeerSet(
            _membership,
            _client,
            new ClusterPeerOptions
            {
                // 프레임 수보다 크게. 거절이 섞이면 처리량을 잰 것이 아니게 된다.
                SendQueueDepth = FrameCount * 2,
                MaxPayloadLength = 8 * 1024,
            },
            NullServerLogger.Instance);

        _payload = new byte[PayloadBytes];
        _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _peers.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _membership.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _peer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _clientTransport.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serverTransport.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>한 피어로 <see cref="FrameCount"/> 장을 보내고 <b>전부 도착할 때까지</b> 기다린다.</summary>
    /// <remarks>
    /// <b>도착까지 기다리는 것이 핵심이다.</b> 큐에 넣기만 재면 "빨리 거절당하는 것" 도
    /// 빠르게 보인다. <see cref="PeerSendStatus.Sent"/> 가 도착 보장이 아니라는
    /// 계약(모듈 문서)이 곧 측정 설계이기도 하다.
    /// </remarks>
    [Benchmark(OperationsPerInvoke = FrameCount)]
    public async Task SendToOnePeer()
    {
        Volatile.Write(ref _arrived, 0);
        _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (int i = 0; i < FrameCount; i++)
        {
            await _peers.SendAsync(new NodeId(2), PeerMessage, _payload, CancellationToken.None)
                .ConfigureAwait(false);
        }

        await _drained.Task.ConfigureAwait(false);
    }
}
