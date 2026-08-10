using System;
using System.Buffers;
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

/// <summary>버퍼 풀 선택지.</summary>
public enum PeerBufferPool
{
    /// <summary>프로세스 공유 풀. 버킷당 보관 개수가 유한하다.</summary>
    Shared = 0,

    /// <summary>이 피어 집합 전용 풀. 보관 개수를 큐 깊이에 맞춰 정한다.</summary>
    Dedicated = 1,
}

/// <summary>
/// <b>깊은 큐에서 전용 풀이 무할당을 회복하는가</b> — ADR-0051 결정 5 의 검증.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 벤치는 해결책을 검증한다. 문제는 이미 관측했다.</b>
/// <see cref="PeerLinkThroughputBenchmarks"/> 에서 같은 코드가 in-flight 1,000 에서 0 B,
/// 10,000 에서 27~308 B 를 할당하는 것을 봤다. ADR-0051 은 그 원인을
/// <see cref="ArrayPool{T}.Shared"/> 의 <b>버킷당 보관 개수 한계</b>로 지목하고
/// <see cref="ClusterPeerSet"/> 에 전용 풀 생성자를 열었다 — <b>그러나 그것은 원인 가설이지
/// 해결 확인이 아니었다.</b> 여기서 A/B 로 답한다.
/// </para>
/// <para>
/// <b>판정 기준.</b> 전용 풀 팔이 <b>0 B</b> 면 가설이 맞고 ADR-0051 결정 5 가 근거를 얻는다.
/// 여전히 할당이 남으면 원인은 풀이 아닌 다른 곳이며, <b>그 ADR 을 정정해야 한다</b> —
/// 틀린 근거를 남겨 두는 것이 측정을 하지 않은 것보다 나쁘다.
/// </para>
/// <para>
/// <b>왜 in-flight 를 10,000 으로 고정하는가.</b> 얕은 큐에서는 두 팔이 모두 0 B 라
/// 아무것도 구분되지 않는다. 문제가 나타나는 조건에서만 해결책을 물을 수 있다.
/// </para>
/// <para>
/// 페이로드는 1 KiB 하나만 본다 — 효과가 가장 크게 나온 구성이고
/// (64 B 27 B vs 1 KiB 308 B), 팔이 늘면 판정만 흐려진다.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkDotNet.Attributes.Config(typeof(BenchConfig))]
public class PeerLinkPoolBenchmarks
{
    private const int FrameCount = 10_000;
    private const int PayloadBytes = 1024;
    private const int MaxPayload = 8 * 1024;

    private static readonly MessageId PeerMessage = new(401);

    private readonly InMemoryTransportHub _hub = new();
    private readonly InMemoryTransportOptions _transportOptions = new();

    private ChServerMServer _peer = null!;
    private ChServerMClient _client = null!;
    private InMemoryServerTransport _serverTransport = null!;
    private InMemoryClientTransport _clientTransport = null!;
    private StaticClusterMembership _membership = null!;
    private ClusterPeerSet _peers = null!;
    private byte[] _payload = [];

    private TaskCompletionSource _drained = null!;
    private int _arrived;

    /// <summary>공유 풀이냐 전용 풀이냐. 이 벤치의 유일한 변수다.</summary>
    [Params(PeerBufferPool.Shared, PeerBufferPool.Dedicated)]
    public PeerBufferPool Pool { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        FramingOptions framing = new() { MaxPayloadLength = MaxPayload };
        InMemoryEndPoint peerEndPoint = new($"pool-peer-{Guid.NewGuid():N}");

        _serverTransport = new InMemoryServerTransport(_hub, peerEndPoint, _transportOptions);
        _clientTransport = new InMemoryClientTransport(_hub, null, _transportOptions);

        _peer = new ServerBuilder()
            .UseTransport(_serverTransport)
            .UseFraming(new FixedHeaderFrameDecoder(framing), new FixedHeaderFrameEncoder(framing))
            .ConfigureDispatcher(d => d.MapRaw(PeerMessage, _ =>
            {
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
        membership.Nodes.Add((new NodeId(1), "self", new InMemoryEndPoint("pool-self")));
        membership.Nodes.Add((new NodeId(2), "peer", peerEndPoint));
        _membership = new StaticClusterMembership(membership);

        ClusterPeerOptions options = new()
        {
            SendQueueDepth = FrameCount * 2,
            MaxPayloadLength = MaxPayload,
        };

        // ⚠ 전용 풀의 보관 개수를 **큐 깊이에 맞춘다**. 이것이 이 A/B 의 전부다 —
        //   풀을 바꾼 것이 아니라 "동시에 떠 있을 수 있는 만큼 담을 수 있게" 만든 것이다.
        _peers = Pool == PeerBufferPool.Dedicated
            ? new ClusterPeerSet(
                _membership, _client, options, NullServerLogger.Instance,
                ArrayPool<byte>.Create(maxArrayLength: MaxPayload, maxArraysPerBucket: FrameCount * 2))
            : new ClusterPeerSet(_membership, _client, options, NullServerLogger.Instance);

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

    /// <summary>깊은 큐에 <see cref="FrameCount"/> 장을 쌓고 전부 도착할 때까지 기다린다.</summary>
    [Benchmark(OperationsPerInvoke = FrameCount)]
    public async Task DeepQueue()
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
