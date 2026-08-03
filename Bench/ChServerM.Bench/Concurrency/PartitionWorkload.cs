using System.Runtime.CompilerServices;

namespace ChServerM.Bench.Concurrency;

/// <summary>
/// 확장성 측정에 쓰는 CPU 바운드 작업 단위.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 빈 작업으로 재지 않는가.</b> 작업이 거의 0이면 측정되는 것은 <b>큐 오버헤드뿐</b>이고,
/// 그 구간의 확장성은 캐시 라인 경합에 지배된다. 그러면 "파티션 모델이 확장되는가"가 아니라
/// "채널이 얼마나 경합하는가"를 재게 된다. 반대로 작업이 너무 무거우면 큐 비용이 묻혀
/// 어떤 모델이든 선형으로 보인다.
/// </para>
/// <para>
/// 그래서 작업 단위를 <b>약 1µs 규모의 순수 계산</b>으로 고정한다. 실제 메시지 핸들러가
/// 하는 일의 규모에 가깝고, 큐 비용과 계산 비용이 둘 다 보이는 구간이다.
/// </para>
/// <para>
/// <b>최적화로 사라지지 않아야 한다.</b> LCG 는 각 단계가 앞 결과에 의존하므로
/// JIT 이 루프를 접거나 지울 수 없다. 결과를 반드시 반환해 DCE 도 막는다.
/// </para>
/// </remarks>
internal static class PartitionWorkload
{
    /// <summary>작업 단위 하나의 내부 반복 횟수. 약 1µs 를 목표로 한다.</summary>
    public const int IterationsPerUnit = 1000;

    /// <summary>
    /// 전체 작업 단위 수.
    /// </summary>
    /// <remarks>
    /// 1·2·4·8·12·24 로 모두 나누어떨어져야 파티션 수를 바꿀 때 잔여 작업이 생기지 않는다
    /// (잔여가 있으면 특정 파티션만 한 단위 더 하고, 그 편차가 곡선에 노이즈로 들어온다).
    /// 480,000 = 2^7 × 3 × 5^3 × ... — 위 여섯 값의 최소공배수 24 의 배수다.
    /// </remarks>
    public const int TotalUnits = 480_000;

    /// <summary>작업 단위 하나를 실행한다.</summary>
    /// <param name="seed">시작값.</param>
    /// <returns>계산 결과. 호출자가 반드시 소비해야 최적화로 사라지지 않는다.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long ExecuteUnit(long seed)
    {
        long acc = seed;

        // LCG — 각 단계가 앞 결과에 의존하므로 접히지 않는다.
        for (int i = 0; i < IterationsPerUnit; i++)
        {
            acc = unchecked((acc * 6364136223846793005L) + 1442695040888963407L);
        }

        return acc;
    }

    /// <summary>작업 단위를 연속으로 실행한다.</summary>
    /// <param name="units">실행할 단위 수.</param>
    /// <param name="seed">시작값.</param>
    public static long ExecuteUnits(int units, long seed)
    {
        long acc = seed;

        for (int i = 0; i < units; i++)
        {
            acc = ExecuteUnit(acc);
        }

        return acc;
    }
}
