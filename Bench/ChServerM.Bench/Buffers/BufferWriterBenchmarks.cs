using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using ChServerM.Buffers;

namespace ChServerM.Bench.Buffers;

/// <summary>
/// 응답 직렬화 스크래치 버퍼의 요청당 비용 — <see cref="ArrayBufferWriter{T}"/>(매번 새로)
/// vs <see cref="PooledBufferWriter"/>(재사용). Phase 3 의 존재 근거 수치를 만든다.
/// </summary>
/// <remarks>
/// 4KB 페이로드를 256B 청크 16개로 쓰는, 프레임 직렬화와 같은 모양의 워크로드다.
/// ArrayBufferWriter 를 요청마다 새로 만드는 것이 현재 핸들러 예제들의 패턴이고,
/// PooledBufferWriter + Clear 재사용이 이 축이 제안하는 패턴이다.
/// </remarks>
[Config(typeof(BenchConfig))]
public class BufferWriterBenchmarks
{
    private PooledBufferWriter _pooled = null!;

    [GlobalSetup]
    public void Setup() => _pooled = new PooledBufferWriter(8192);

    [GlobalCleanup]
    public void Cleanup() => _pooled.Dispose();

    [Benchmark(Baseline = true, Description = "ArrayBufferWriter 매번 생성")]
    public int ArrayBufferWriterPerRequest()
    {
        ArrayBufferWriter<byte> writer = new();
        Fill(writer);
        return writer.WrittenCount;
    }

    [Benchmark(Description = "PooledBufferWriter 재사용(Clear)")]
    public int PooledBufferWriterReused()
    {
        _pooled.Clear();
        Fill(_pooled);
        return _pooled.WrittenCount;
    }

    private static void Fill(IBufferWriter<byte> writer)
    {
        for (int chunk = 0; chunk < 16; chunk++)
        {
            Span<byte> span = writer.GetSpan(256);
            span[..256].Fill((byte)chunk);
            writer.Advance(256);
        }
    }
}
