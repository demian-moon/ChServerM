using System;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 커넥션 생명주기를 메트릭으로 남기는 데코레이터 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 횡단 관심사는 데코레이터로.</b> 커넥션 수 계측을 읽기 루프에 넣으면
/// 코어 로직이 오염된다(CLAUDE.md 4). 이 데코레이터가 내부 핸들러의 <c>RunAsync</c> 를
/// 감싸 수립 시 <see cref="MetricNames.ConnectionsAccepted"/> +1 과
/// <see cref="MetricNames.ConnectionsActive"/> 게이지 +1 을, 종료 시(어떤 경로든)
/// 게이지 -1 을 남긴다. 프레이밍도 보안도 커넥션 카운터를 알지 않는다.
/// </para>
/// <para>
/// <b>게이지 감소는 <c>finally</c> 로 보장한다.</b> 내부 핸들러가 예외로 끝나도 활성
/// 게이지가 새면 "닫혔는데 열린 것으로 보이는" 커넥션이 대시보드에 영구 누적된다 —
/// 락-프리 상태 복원을 <c>finally</c> 에 두는 규약과 같은 부류다(9.2).
/// </para>
/// <para>
/// <b>거부(<see cref="MetricNames.ConnectionsRejected"/>)는 여기 없다.</b> 동시 접속 상한
/// 거부는 커넥션이 이 핸들러에 <b>닿기 전</b> 전송 계층에서 일어난다 — 거부 카운터는
/// 전송 계측 지점의 몫이다(후속 증분). 이 데코레이터는 수락된 커넥션만 본다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다. 게이지 조정은
/// 싱크가 스레드 안전하게 처리한다.
/// </para>
/// </remarks>
internal sealed class MetricsConnectionHandler : IConnectionHandler
{
    private readonly IConnectionHandler _inner;
    private readonly IMetricsSink _sink;

    public MetricsConnectionHandler(IConnectionHandler inner, IMetricsSink sink)
    {
        _inner = inner;
        _sink = sink;
    }

    public async Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _sink.Count(MetricNames.ConnectionsAccepted, 1, ReadOnlySpan<MetricTag>.Empty);
        _sink.AdjustGauge(MetricNames.ConnectionsActive, 1, ReadOnlySpan<MetricTag>.Empty);

        try
        {
            await _inner.RunAsync(connection).ConfigureAwait(false);
        }
        finally
        {
            // 9.2 — 감소를 finally 에 두지 않으면 예외 하나가 활성 게이지를 영구 누수시킨다.
            _sink.AdjustGauge(MetricNames.ConnectionsActive, -1, ReadOnlySpan<MetricTag>.Empty);
        }
    }
}
