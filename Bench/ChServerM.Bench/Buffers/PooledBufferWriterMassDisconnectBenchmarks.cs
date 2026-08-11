using System;
using BenchmarkDotNet.Attributes;
using ChServerM.Buffers;

namespace ChServerM.Bench.Buffers;

/// <summary>
/// <b>대량 접속 해제 구간</b> — 1만 커넥션의 버퍼가 한꺼번에 반납될 때 무슨 일이 나는가.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 2026-08-11 재측정(ADR-0051 기준 정정)이 남긴 명시적 공백이다:
/// "붙들고만 있는 대여물은 버킷을 놓고 경쟁하지 않는다. 경쟁하는 것은 <b>동시에 반납되는
/// 것</b>이다" — 그렇다면 1만 개가 동시에 반납되는 대량 접속 해제(서버 재시작·LB 드레인·
/// 대규모 킥)가 정확히 그 경쟁 조건이다. <b>기전은 알고 크기를 몰랐다.</b> 이 벤치가 크기를 잰다.
/// </para>
/// <para>
/// <b>세 팔이 각각 묻는 것.</b>
/// </para>
/// <list type="bullet">
///   <item><b>대량 반납</b> — 1만 개 <c>Dispose</c> 자체의 비용. 반납은 할당하지 않으므로
///     여기서 나오는 숫자는 시간이지 바이트가 아니다. 버킷이 넘친 반납은 <b>버려져 가비지가
///     된다</b> — 그 크기는 다음 팔에서 드러난다.</item>
///   <item><b>반납 폭주 직후 대량 재대여</b> — 재접속 폭풍. 풀이 방금의 1만 반납 중 얼마를
///     실제로 붙들었는지가 <b>이 팔의 할당량</b>으로 나타난다: 풀이 다 담았다면 0B,
///     다 버렸다면 커넥션당 정착 크기(8KiB) 전부가 새 할당이다.</item>
///   <item><b>순차 왕래(비교 기준)</b> — 같은 1만 회 생성·폐기를 <b>한 번에 하나씩</b>.
///     반납이 몰리지 않으면 방금 반납한 배열을 즉시 되빌리므로 할당이 거의 없어야 한다.
///     몰림(대량) 대 안 몰림(순차)의 차이가 곧 "폭주의 값"이다.</item>
/// </list>
/// <para>
/// <b>측정 방법 주의.</b> 팔마다 풀 상태가 조건이므로 <c>[IterationSetup]</c> 으로 매 반복
/// 상태를 다시 만들고 <c>InvocationCount = 1</c> 로 돌린다 — 시간 수치는 그만큼 흔들리지만
/// 이 벤치의 판정 대상은 <b>할당 바이트</b>다(MemoryDiagnoser).
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
[InvocationCount(1)]
[WarmupCount(2)]
[IterationCount(10)]
public class PooledBufferWriterMassDisconnectBenchmarks
{
    private const int Connections = 10_000;
    private const int PayloadBytes = 4096;
    private const int ChunkBytes = 256;
    private const int SettledCapacity = 8192;

    private PooledBufferWriter[] _writers = [];

    /// <summary>대량 반납 팔의 조건 — 반납할 1만 개를 정착 상태로 만들어 둔다.</summary>
    [IterationSetup(Target = nameof(MassReturn))]
    public void SetupMassReturn()
    {
        _writers = CreateSettled(Connections);
    }

    /// <summary>대량 반납 — 1만 개가 한꺼번에 풀로 돌아간다.</summary>
    [Benchmark(Description = "대량 반납(1만 Dispose)")]
    public void MassReturn()
    {
        foreach (PooledBufferWriter writer in _writers)
        {
            writer.Dispose();
        }
    }

    /// <summary>재접속 팔의 조건 — 방금 1만 개가 한꺼번에 반납된 풀 상태를 만든다.</summary>
    [IterationSetup(Target = nameof(ReconnectAfterMassReturn))]
    public void SetupReconnect()
    {
        PooledBufferWriter[] writers = CreateSettled(Connections);
        foreach (PooledBufferWriter writer in writers)
        {
            writer.Dispose();
        }
    }

    /// <summary>반납 폭주 직후 대량 재대여 — 재접속 폭풍. 할당량 = 풀이 버린 만큼.</summary>
    [Benchmark(Description = "반납 폭주 직후 대량 재대여(1만)")]
    public void ReconnectAfterMassReturn()
    {
        _writers = CreateSettled(Connections);
    }

    /// <summary>재접속 팔이 만든 것을 반복 밖에서 정리한다.</summary>
    [IterationCleanup(Target = nameof(ReconnectAfterMassReturn))]
    public void CleanupReconnect()
    {
        foreach (PooledBufferWriter writer in _writers)
        {
            writer.Dispose();
        }

        _writers = [];
    }

    /// <summary>순차 왕래(비교 기준) — 같은 1만 회를 한 번에 하나씩. 반납이 몰리지 않는다.</summary>
    [Benchmark(Baseline = true, Description = "순차 왕래(1만 회, 몰림 없음)")]
    public void SequentialChurn()
    {
        for (int i = 0; i < Connections; i++)
        {
            using PooledBufferWriter writer = new(SettledCapacity);
            Fill(writer);
        }
    }

    private static PooledBufferWriter[] CreateSettled(int count)
    {
        PooledBufferWriter[] writers = new PooledBufferWriter[count];
        for (int i = 0; i < count; i++)
        {
            writers[i] = new PooledBufferWriter(SettledCapacity);
            Fill(writers[i]);
        }

        return writers;
    }

    // 구체 타입을 받는다 — 측정 대상 안 루프이므로 인터페이스 디스패치가 수치에 섞이면 안 된다.
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
