using System;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Execution;

namespace ChServerM.Hosting;

/// <summary>
/// 커넥션의 처리 전체를 하나의 실행 파티션에 고정하는 데코레이터.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — ADR-0005 의 주 경로를 실제로 켜는 곳이다.</b> 읽기 루프를 파티션
/// 스케줄러에서 시작하면 그 루프와 <b>모든 <c>await</c> 연속</b>이 같은 스레드에서 이어진다.
/// 결과는 두 가지다.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>프레임당 큐 비용이 0.</b> 프레임을 큐에 넣는 모델(레거시)은 메시지마다
///     큐 왕복이 필요하다. 여기서는 루프 자체가 이미 올바른 스레드 위에 있다
///   </description></item>
///   <item><description>
///     <b>같은 커넥션의 메시지는 자동으로 순차.</b> 별도 동기화가 없다 —
///     그럴 필요가 없기 때문이다
///   </description></item>
/// </list>
/// <para>
/// <b>파티션 키는 커넥션 슬롯이다.</b> 커넥션이 끊겼다 다시 붙으면 다른 파티션으로
/// 갈 수 있다. 세션 단위 고정이 필요하면 <see cref="Identity.SessionId"/> 기반으로
/// 다시 배정해야 하는데, 그것은 세션 계층(Phase 7)의 몫이다.
/// </para>
/// <para>
/// <b>데코레이터인 이유.</b> 실행 모델을 쓰지 않는 조립(무상태 웹 프로필)은 이 래퍼를
/// 그냥 빼면 된다. 안쪽 핸들러는 자기가 어느 스레드에서 도는지 알지 못한다 —
/// 그것이 축이 교체 가능하다는 뜻이다(ADR-0004).
/// </para>
/// <para><b>스레드 규약.</b> 불변이므로 스레드 안전하다.</para>
/// </remarks>
public sealed class PartitionedConnectionHandler : IConnectionHandler
{
    private readonly IConnectionHandler _inner;
    private readonly IExecutionModel _executionModel;

    /// <summary>안쪽 핸들러를 파티션에 고정한다.</summary>
    /// <param name="inner">실제 처리를 하는 핸들러.</param>
    /// <param name="executionModel">파티션을 제공하는 실행 모델.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    public PartitionedConnectionHandler(IConnectionHandler inner, IExecutionModel executionModel)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(executionModel);

        _inner = inner;
        _executionModel = executionModel;
    }

    /// <inheritdoc />
    public Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IExecutionPartition partition = _executionModel.GetPartition(connection.Id.ToPartitionKey());

        // DenyChildAttach: 핸들러 안에서 만든 자식 태스크가 이 태스크의 완료에
        // 묶이면 커넥션이 끝났는데도 완료가 지연된다.
        return Task.Factory.StartNew(
                () => _inner.RunAsync(connection),
                System.Threading.CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                partition.Scheduler)
            .Unwrap();
    }
}
