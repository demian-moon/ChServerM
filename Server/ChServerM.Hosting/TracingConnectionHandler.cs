using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 커넥션 전 생애를 span 으로 남기고, 그 컨텍스트를 프레임 디스패치의 부모로 넘기는
/// 데코레이터 (Phase 11 관측, ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 크로스 스레드 부모 전파.</b> 커넥션 하나의 span 을 열어
/// (<see cref="ActivityNames.Connection"/>), 그 <see cref="ActivityContext"/> 를
/// <see cref="ConnectionTraceFeature"/> 로 커넥션 기능에 실는다. 디스패치
/// (<see cref="Dispatch.TracingMiddleware"/>)는 파티션 스레드에서 이 컨텍스트를 읽어 자기
/// span 의 <b>명시적 부모</b>로 삼는다 — <see cref="Activity.Current"/> 가 파티션 스레드로
/// 흐르지 않는 문제(<see cref="ConnectionTraceFeature"/> 문서)를 이렇게 우회한다.
/// 그 결과 한 커넥션의 모든 디스패치 span 이 커넥션 span 아래 한 trace 로 묶인다.
/// </para>
/// <para>
/// <b>가장 바깥에 감싼다.</b> 커넥션 span 이 핸드셰이크(TLS·버전 협상)까지 덮어야 커넥션의
/// 전 생애가 하나의 trace 가 된다. <see cref="MetricsConnectionHandler"/> 와 같은 결의
/// 데코레이터다.
/// </para>
/// <para>
/// <b>span 종료는 <c>finally</c> 로 보장한다(9.2).</b> 내부 핸들러가 예외로 끝나도
/// <c>using</c> 이 span 을 닫고 기능을 지운다 — 커넥션 span 이 영원히 열린 채 남으면
/// 그 trace 는 export 되지 않는다.
/// </para>
/// <para>
/// <b>트레이드오프 — 장수명 커넥션은 큰 trace 가 된다.</b> 커넥션 span 은 종료 시점에
/// export 되므로, 오래 사는 커넥션(세션형)은 trace 가 늦게 보이고 자식 span 이 많이 쌓인다.
/// 볼륨은 리스너의 <c>Sample</c> 콜백(head 샘플링)으로 조절한다 — 표준 운영 방식이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다. 커넥션당
/// <see cref="RunAsync"/> 는 한 번만 호출된다.
/// </para>
/// </remarks>
internal sealed class TracingConnectionHandler : IConnectionHandler
{
    private readonly IConnectionHandler _inner;

    public TracingConnectionHandler(IConnectionHandler inner) => _inner = inner;

    public async Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // 리스너가 없으면 null 이 온다 — 그때는 기능을 싣지 않아 디스패치 span 이 루트가 된다.
        using Activity? connectionSpan = ServerTracing.Source.StartActivity(
            ActivityNames.Connection, ActivityKind.Server);

        if (connectionSpan is not null)
        {
            connectionSpan.SetTag(TagNames.ConnectionId, connection.Id.ToString());

            // 부모 컨텍스트를 커넥션 기능에 1회 싣는다. 디스패치가 파티션 스레드에서 읽는다
            // (ConnectionTraceFeature 의 수명·스레드 규약).
            connection.Features.Set(new ConnectionTraceFeature(connectionSpan.Context));
        }

        try
        {
            await _inner.RunAsync(connection).ConfigureAwait(false);
        }
        finally
        {
            // 9.2 — span 종료(using)와 기능 해제를 finally 에 둔다. 커넥션 span 이 영원히
            // 열린 채 남으면 그 trace 는 export 되지 않는다.
            connection.Features.Set<ConnectionTraceFeature>(null);
        }
    }
}
