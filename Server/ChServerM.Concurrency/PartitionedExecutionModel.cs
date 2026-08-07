using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Execution;
using ChServerM.Identity;

namespace ChServerM.Concurrency;

/// <summary>
/// 키 기반 파티션 샤딩 실행 모델 (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "공유 상태를 어떻게 보호할지 고민하기 전에 공유하지 않는 방법을
/// 먼저 찾는다"(CLAUDE.md 9.1)의 구현이다.
/// </para>
/// <code>
///   파티션 키의 안정 해시 → 파티션 선택
///   같은 키 → 항상 같은 파티션 → 단일 소비자 → 순서 보장 + 동기화 불필요
///   다른 키 → 완전 독립       → 코어 수만큼 선형 확장
/// </code>
/// <para>
/// 레거시가 <c>oid % n</c> 으로 송신·수신·스케줄러 세 곳에서 독립적으로 재발명한
/// 패턴이다. 여기서는 <see cref="PartitionKey"/> 의 피보나치 해싱을 써서
/// 음수·오버플로·분포 편향을 없앴다.
/// </para>
/// <para>
/// <b>ADR-0005 에는 검증 조건이 붙어 있다.</b> 이 모델이 코어 수에 대해 선형 확장을
/// 증명하지 못하면 결정 자체가 무효다. Phase 8 벤치마크에서 확인한다.
/// </para>
/// <para>
/// <b>파티션 수는 실행 중에 바뀌지 않는다.</b> 바뀌면 키→파티션 매핑이 달라져
/// 순서 보장이 사라진다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// <para>
/// <b>헬스 기여(<see cref="IHealthCheck"/>).</b> 이 모델은 liveness 신호를 낸다 — 파티션
/// 전용 스레드가 전부 살아 있는지. 스레드가 죽으면 그 파티션의 커넥션이 영영 멈추므로
/// 확정적 고장이다. 호스팅은 실행 모델이 <see cref="IHealthCheck"/> 를 구현하면 liveness
/// 프로브에 자동 등록한다 — Core 실행 모델 계약(<see cref="IExecutionModel"/>)에 진단
/// 멤버를 얹지 않고, 호스팅이 Concurrency 를 참조하지 않고도 배선되게 하는 접점이다.
/// </para>
/// </remarks>
public sealed class PartitionedExecutionModel : IExecutionModel, IHealthCheck
{
    private readonly ExecutionPartition[] _partitions;
    private readonly TimeSpan _shutdownTimeout;
    private int _disposed;

    /// <summary>실행 모델을 만들고 파티션 스레드를 시작한다.</summary>
    /// <param name="options">설정. <see langword="null"/>이면 기본값.</param>
    /// <param name="logger">진단 로거.</param>
    /// <param name="metricsSink">
    /// 메트릭 싱크. 주어지면 각 파티션이 백프레셔 관측
    /// (<see cref="MetricNames.PartitionWorkRejected"/> 카운터·
    /// <see cref="MetricNames.PartitionQueueDepth"/> 게이지)을 방출한다.
    /// <see langword="null"/>이면 <see cref="NullMetricsSink"/> — 수집하지 않는다.
    /// <b>서버에 <c>UseMetrics</c> 로 넘긴 것과 같은 싱크를 여기에도 넘겨야</b> 파티션 큐
    /// 포화가 다른 프레임워크 메트릭과 같은 대시보드에 모인다. 실행 모델은 사용자가
    /// 조립해 <c>UseExecutionModel</c> 로 주입하므로(로거와 동일), 프레임워크가 대신
    /// 배선해 줄 수 없다 — 이 인자가 그 접점이다.
    /// </param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public PartitionedExecutionModel(
        PartitionedExecutionOptions? options = null,
        IServerLogger? logger = null,
        IMetricsSink? metricsSink = null)
    {
        options ??= new PartitionedExecutionOptions();

        // 시작 시점 검증. static 초기자에서 다른 static 을 읽어 0 이 되는 레거시의
        // DivideByZeroException 경로를 여기서 원천 차단한다 (CLAUDE.md 9.1).
        options.Validate();

        logger ??= NullServerLogger.Instance;
        metricsSink ??= NullMetricsSink.Instance;
        _shutdownTimeout = options.ShutdownTimeout;
        _partitions = new ExecutionPartition[options.PartitionCount];

        for (int i = 0; i < _partitions.Length; i++)
        {
            _partitions[i] = new ExecutionPartition(i, options, logger, metricsSink);
        }

        // 모든 파티션을 다 만든 뒤에 시작한다. 생성 도중 시작하면 아직 초기화되지 않은
        // 배열을 다른 스레드가 볼 수 있다.
        foreach (ExecutionPartition partition in _partitions)
        {
            partition.Start();
        }
    }

    /// <inheritdoc />
    public int PartitionCount => _partitions.Length;

    /// <inheritdoc />
    public IExecutionPartition GetPartition(PartitionKey key) =>
        _partitions[key.ToIndex(_partitions.Length)];

    /// <inheritdoc />
    public IExecutionPartition GetPartition(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _partitions.Length);

        return _partitions[index];
    }

    /// <summary>모든 파티션이 실행한 작업 수의 합.</summary>
    /// <remarks>
    /// 진단·테스트용 누적값이다. 백프레셔는 주입된 <see cref="IMetricsSink"/> 로 방출된다
    /// (<see cref="MetricNames.PartitionWorkRejected"/>·<see cref="MetricNames.PartitionQueueDepth"/>).
    /// </remarks>
    public long TotalExecutedCount
    {
        get
        {
            long total = 0;
            foreach (ExecutionPartition partition in _partitions)
            {
                total += partition.ExecutedCount;
            }

            return total;
        }
    }

    /// <summary>모든 파티션이 거부한 작업 수의 합.</summary>
    /// <remarks><b>0이 아니면 용량이 부족한 것이다.</b></remarks>
    public long TotalRejectedCount
    {
        get
        {
            long total = 0;
            foreach (ExecutionPartition partition in _partitions)
            {
                total += partition.RejectedCount;
            }

            return total;
        }
    }

    /// <summary>liveness 판정 — 파티션 전용 스레드가 전부 살아 있는지.</summary>
    /// <param name="cancellationToken">쓰이지 않는다 — 로컬 플래그를 읽는 즉시 완료다.</param>
    /// <returns>
    /// 전부 살아 있으면 <see cref="HealthStatus.Healthy"/>, 하나라도 죽었으면
    /// <see cref="HealthStatus.Unhealthy"/>(죽은 개수를 설명에 남긴다).
    /// </returns>
    /// <remarks>
    /// 스레드 생존은 <b>확정적 고장</b>만 잡는다 — 살아서 교착한 파티션은 못 잡는다
    /// (<see cref="ExecutionPartition.IsThreadAlive"/> 문서). 그 수준의 감지는 진행도 하트비트가
    /// 필요한 후속 신호다. 지금은 "스레드가 죽어 커넥션이 영영 멈춘" 경우를 드러낸다.
    /// </remarks>
    public ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        int dead = 0;
        foreach (ExecutionPartition partition in _partitions)
        {
            if (!partition.IsThreadAlive)
            {
                dead++;
            }
        }

        HealthCheckResult result = dead == 0
            ? HealthCheckResult.Healthy($"파티션 스레드 {_partitions.Length}개 전부 생존")
            : HealthCheckResult.Unhealthy($"파티션 스레드 {_partitions.Length}개 중 {dead}개 죽음");

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 모든 파티션 스레드를 멈추고 <see cref="PartitionedExecutionOptions.ShutdownTimeout"/>
    /// 동안만 기다린다. 상한 없는 대기는 종료를 영원히 막는다.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // 전부에게 먼저 알린 뒤 공유 데드라인으로 함께 기다린다. 파티션마다 제한 시간을
        // 새로 시작하면, 전부가 블로킹된 최악의 경우 순차 조인이 파티션 수 × 제한 시간이
        // 된다(64 × 5초 = 5분 20초 — 정확히 피하려던 그 수치다. 2026-08-04 감사).
        foreach (ExecutionPartition partition in _partitions)
        {
            partition.SignalStop();
        }

        long deadline = Environment.TickCount64 + (long)_shutdownTimeout.TotalMilliseconds;
        foreach (ExecutionPartition partition in _partitions)
        {
            partition.DisposeCore(deadline);
        }

        return ValueTask.CompletedTask;
    }
}
