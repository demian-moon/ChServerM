using System;
using System.Collections.Generic;
using System.Net;
using BenchmarkDotNet.Attributes;
using ChServerM.Cluster;
using ChServerM.Identity;

namespace ChServerM.Bench.Cluster;

/// <summary>
/// 클러스터 라우팅 조회 비용 — <b>랑데뷰의 O(노드 수)가 실제로 얼마인가</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 벤치의 질문은 하나다.</b> ADR-0048 은 일관 해싱(링, O(log V))이 아니라 랑데뷰
/// (O(N))를 골랐고, 그 근거에 "노드 수가 수백을 넘으면 링이 유리해진다" 고 적었다.
/// <b>"수백" 이 어디쯤인지를 수치로 남기지 않으면 그 문장은 근거가 아니라 짐작이다.</b>
/// </para>
/// <para>
/// 비교 대상으로 <see cref="ToIndex"/> 를 함께 잰다 — 이동량 때문에 쓸 수 없는 방식이지만,
/// <b>라우팅 하나가 낼 수 있는 최소 비용</b>이 얼마인지를 보여 주는 바닥이다.
/// 랑데뷰가 그 바닥에서 얼마나 떨어져 있는지가 이 축의 실제 세금이다.
/// </para>
/// <para>
/// 키는 미리 만들어 둔다 — 키 생성 비용이 측정에 섞이면 노드 수에 따른 기울기가 흐려진다.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RoutingBenchmarks
{
    private const int KeyCount = 1024;

    private PartitionKey[] _keys = [];
    private RendezvousRouter _router = null!;
    private ClusterNode?[] _candidates = [];

    /// <summary>클러스터 크기. 기울기를 보려면 여러 점이 필요하다.</summary>
    [Params(3, 8, 16, 64, 256)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        List<ClusterNode> nodes = new(NodeCount);
        for (int i = 0; i < NodeCount; i++)
        {
            nodes.Add(new ClusterNode(
                new NodeId((ushort)i), $"node-{i:D4}", new DnsEndPoint($"n{i}.internal", 7000)));
        }

        _router = new RendezvousRouter(new ClusterView(nodes, generation: 1));

        _keys = new PartitionKey[KeyCount];
        for (int i = 0; i < KeyCount; i++)
        {
            _keys[i] = PartitionKey.FromValue((ulong)i);
        }

        _candidates = new ClusterNode?[3];
    }

    /// <summary>소유자 하나를 구한다. 메시지마다 불릴 수 있는 경로다.</summary>
    [Benchmark(OperationsPerInvoke = KeyCount)]
    public int Owner()
    {
        int sink = 0;
        foreach (PartitionKey key in _keys)
        {
            if (_router.TryGetOwner(key, out ClusterNode? owner))
            {
                sink += owner!.Id.Value;
            }
        }

        return sink;
    }

    /// <summary>상위 3개 후보를 구한다. 복제·장애 조치가 쓰는 경로다.</summary>
    [Benchmark(OperationsPerInvoke = KeyCount)]
    public int TopThree()
    {
        int sink = 0;
        foreach (PartitionKey key in _keys)
        {
            sink += _router.GetOwners(key, _candidates);
        }

        return sink;
    }

    /// <summary>
    /// 비교용 바닥 — 곱셈-시프트 축소. <b>라우팅으로는 쓸 수 없다</b>(노드 하나 추가에
    /// 키의 절반이 이동한다, ADR-0048). 최소 비용이 어디인지를 보여 줄 뿐이다.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = KeyCount)]
    public int ToIndex()
    {
        int sink = 0;
        foreach (PartitionKey key in _keys)
        {
            sink += key.ToIndex(NodeCount);
        }

        return sink;
    }
}
