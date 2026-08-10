using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using ChServerM.Buffers;

namespace ChServerM.Bench.Buffers;

/// <summary>
/// <b><see cref="PooledBufferWriter"/> 의 "정상 상태 할당 0" 이 1만 커넥션에서도 참인가</b>
/// — ADR-0016 의 값이 얕은 조건에서 잰 것임을 확인한 뒤의 재측정(ADR-0051).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0016 의 "정상 상태 할당 0" 은 <b>writer 하나</b>로 측정한 값이다
/// (<see cref="BufferWriterBenchmarks"/>). 그런데 이 타입의 문서는 <b>"커넥션당 하나를
/// 만들어 재사용"</b> 을 의도된 사용법으로 권장하고, 그것은 1만 커넥션에서
/// <b>1만 개가 동시에 대여를 붙드는</b> 모양이다 — ADR-0051 이 `ClusterPeerSet` 에서
/// 확인한 함정("미처리 대여물이 <b>설정 용량</b>에 비례하면 위험")과 같은 판별 기준에 걸린다.
/// </para>
/// <para>
/// <b>⚠ 그러나 같은 함정이라고 단정하지 않는다.</b> `ClusterPeerSet` 은 <b>메시지마다</b>
/// 빌리고 반납하지만, 이 타입은 <b>생성 시 한 번</b> 빌려 수명 내내 들고 있으며
/// <see cref="PooledBufferWriter.Clear"/> 는 버퍼를 <b>유지</b>한다. 즉 정상 상태에는
/// 대여 왕래가 아예 없다 — 그렇다면 붙들린 개수가 많아도 정상 상태 할당은 0 일 수 있다.
/// <b>어느 쪽인지는 재야 안다.</b>
/// </para>
/// <para>
/// <b>세 팔이 서로 다른 질문이다.</b>
/// </para>
/// <list type="bullet">
///   <item><b>정상 상태</b> — <c>Clear</c> + 쓰기. 대여 왕래가 없는 경로.
///     ADR-0016 의 주장이 그대로 걸린 자리다.</item>
///   <item><b>생성·폐기 왕래</b> — 커넥션이 붙었다 끊기는 모양(또는 요청마다 만드는
///     <b>잘못된</b> 사용법). 여기서는 대여와 반납이 매번 일어나므로
///     <see cref="ArrayPool{T}.Shared"/> 의 버킷 한계가 걸릴 수 있다.</item>
///   <item><b>성장</b> — 2배 대여-복사-반납. 1만 개가 붙들린 상태에서 반납이
///     버킷에 담기지 못하면 <b>다음 성장이 매번 새 배열</b>이 된다.</item>
/// </list>
/// <para>
/// <b>판정 기준.</b> <see cref="OutstandingWriters"/> 1 과 10,000 에서 <b>같은 팔의
/// 할당량이 다르면</b> 그 경로가 규모에 조건부인 것이다. 정상 상태 팔이 양쪽 모두 0 이면
/// ADR-0016 의 주장은 <b>깊은 조건에서도 성립</b>하며, 그 사실을 적어 두어야
/// "얕은 조건의 값" 이라는 경고를 거둘 수 있다.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
public class PooledBufferWriterScaleBenchmarks
{
    private const int PayloadBytes = 4096;
    private const int ChunkBytes = 256;
    private const int SettledCapacity = 8192;

    /// <summary>사용법이 "커넥션당 하나" 이므로 이 값이 곧 <b>동시 커넥션 수</b>다.</summary>
    [Params(1, 10_000)]
    public int OutstandingWriters { get; set; }

    /// <summary>수명 내내 대여를 붙들고 있는 것들. 측정 대상이 아니라 <b>조건</b>이다.</summary>
    private PooledBufferWriter[] _outstanding = [];

    /// <summary>정상 상태 팔의 측정 대상. 이미 정착 크기까지 자라 있다.</summary>
    private PooledBufferWriter _settled = null!;

    [GlobalSetup]
    public void Setup()
    {
        _outstanding = new PooledBufferWriter[OutstandingWriters];
        for (int i = 0; i < _outstanding.Length; i++)
        {
            // ⚠ 만들기만 하면 대여는 하지만 **정착 크기까지 자란 상태**가 아니다.
            //   실제 커넥션은 한 번은 응답을 쓰므로 그 상태로 맞춰 둔다.
            _outstanding[i] = new PooledBufferWriter(SettledCapacity);
            Fill(_outstanding[i]);
        }

        _settled = new PooledBufferWriter(SettledCapacity);
        Fill(_settled);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _settled.Dispose();

        foreach (PooledBufferWriter writer in _outstanding)
        {
            writer.Dispose();
        }
    }

    /// <summary>정상 상태 — <c>Clear</c> 후 다시 쓴다. <b>대여 왕래가 없다.</b></summary>
    [Benchmark(Baseline = true, Description = "정상 상태(Clear + 쓰기)")]
    public int SteadyState()
    {
        _settled.Clear();
        Fill(_settled);
        return _settled.WrittenCount;
    }

    /// <summary>생성·폐기 왕래 — 커넥션이 붙었다 끊기는 모양. <b>매번 대여와 반납이 있다.</b></summary>
    [Benchmark(Description = "생성·폐기 왕래")]
    public int ChurnPerOperation()
    {
        using PooledBufferWriter writer = new(SettledCapacity);
        Fill(writer);
        return writer.WrittenCount;
    }

    /// <summary>성장 — 작게 시작해 정착 크기까지 2배 대여-복사-반납을 반복한다.</summary>
    [Benchmark(Description = "성장(작게 시작)")]
    public int GrowToSettled()
    {
        using PooledBufferWriter writer = new(ChunkBytes);
        Fill(writer);
        return writer.WrittenCount;
    }

    // ⚠ 인터페이스가 아니라 구체 타입을 받는다 — 이 루프는 측정 대상 안에 있으므로
    //   인터페이스 디스패치 비용이 측정에 섞인다(CA1859 가 잡아 줬다).
    private static void Fill(PooledBufferWriter writer)
    {
        for (int written = 0; written < PayloadBytes; written += ChunkBytes)
        {
            Span<byte> span = writer.GetSpan(ChunkBytes);
            span[..ChunkBytes].Fill(0xAB);
            writer.Advance(ChunkBytes);
        }
    }
}
