using System.Threading;

namespace ChServerM.Buffers;

/// <summary>
/// 풀 대여·반납·누수 카운터. "조용한 유실은 관측되지 않으면 존재하지 않는 것과 같다"의
/// 버퍼 계층 적용이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 레거시의 <c>ArrayPool</c> 반납 누수는 코드 어디에서도 관측되지 않았다 —
/// 풀이 조용히 새 배열을 만들며 성능만 잠식했다. 여기서는 누수가 숫자로 드러난다:
/// <see cref="LeakedBuffers"/> 가 0이 아니면 어딘가의 <c>Dispose</c> 가 빠졌다는 뜻이고,
/// Phase 11 관측 축이 이 값을 메트릭으로 내보내 경보 대상으로 만든다.
/// </para>
/// <para>
/// 정상 상태 판정: <c>RentedBuffers - ReturnedBuffers</c> = 살아 있는 대여 수.
/// 부하가 빠진 뒤에도 이 값이 계속 크면 반납이 늦거나 새는 것이다.
/// </para>
/// <para><b>스레드 규약.</b> 전부 <see cref="Interlocked"/> — 어느 스레드에서든 안전하다.
/// 카운터는 프로세스 전역이다(파티션별 분리는 경합이 관측되면 — CLAUDE.md 9.1).</para>
/// </remarks>
public static class BufferPoolDiagnostics
{
    private static long _rented;
    private static long _returned;
    private static long _leaked;

    /// <summary>지금까지 풀에서 대여한 횟수.</summary>
    public static long RentedBuffers => Interlocked.Read(ref _rented);

    /// <summary>지금까지 풀에 반납한 횟수(누수 회수 제외).</summary>
    public static long ReturnedBuffers => Interlocked.Read(ref _returned);

    /// <summary>반납 누락으로 파이널라이저가 회수한 횟수. <b>0이 아니면 버그다.</b></summary>
    public static long LeakedBuffers => Interlocked.Read(ref _leaked);

    internal static void OnRented() => Interlocked.Increment(ref _rented);

    internal static void OnReturned() => Interlocked.Increment(ref _returned);

    internal static void OnLeaked() => Interlocked.Increment(ref _leaked);
}
