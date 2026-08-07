using System;
using ChServerM.Buffers;
using ChServerM.Diagnostics;

namespace ChServerM.Observability;

/// <summary>
/// 버퍼 풀 상태를 진단 스냅샷으로 내놓는 소스 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 여기 있는가.</b> <c>ChServerM.Buffers</c> 는 "Core 조차 참조하지 않는다" 가 의도된
/// 결정이라(그 csproj 주석) <see cref="IDiagnosticsSource"/> 를 볼 수 없다. 그래서 관측 배선을
/// 관측 어셈블리가 가져간다 — <see cref="BufferPoolMetrics"/> 와 같은 근거다(ADR-0025).
/// </para>
/// <para>
/// <b>메트릭과 중복이 아니다.</b> 같은 카운터를 메트릭(시계열)과 진단(스냅샷) 두 곳에 낸다:
/// 메트릭은 "지난 한 시간의 추세" 를, 진단은 "지금 이 순간의 값" 을 답한다. 장애 중에
/// 대시보드를 못 볼 때 <c>curl</c> 한 번으로 누수 여부를 확인할 수 있어야 한다.
/// </para>
/// <para>
/// <b><c>leaked</c> 가 0이 아니면 버그다</b> — 반납 누락을 파이널라이저가 회수한 횟수이며,
/// 레거시의 조용한 풀 누수를 이 값이 드러낸다.
/// </para>
/// <para><b>스레드 규약.</b> 정적 카운터를 <see cref="System.Threading.Interlocked"/> 로 읽는다 — 안전하다.</para>
/// </remarks>
public sealed class BufferPoolDiagnosticsSource : IDiagnosticsSource
{
    /// <inheritdoc />
    public string Name => "buffers.pool";

    /// <inheritdoc />
    public void Collect(IDiagnosticsWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        long rented = BufferPoolDiagnostics.RentedBuffers;
        long returned = BufferPoolDiagnostics.ReturnedBuffers;

        writer.Write("rented", rented);
        writer.Write("returned", returned);

        // 살아 있는 대여 수 — 부하가 빠진 뒤에도 줄지 않으면 누수를 의심한다.
        // 두 값을 따로 읽는 사이에 바뀔 수 있어 정확한 순간값은 아니다(진단 용도엔 충분).
        writer.Write("live", rented - returned);

        writer.Write("leaked", BufferPoolDiagnostics.LeakedBuffers);
    }
}
