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
/// </remarks>
public sealed class PartitionedExecutionModel : IExecutionModel
{
    private readonly ExecutionPartition[] _partitions;
    private readonly TimeSpan _shutdownTimeout;
    private int _disposed;

    /// <summary>실행 모델을 만들고 파티션 스레드를 시작한다.</summary>
    /// <param name="options">설정. <see langword="null"/>이면 기본값.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public PartitionedExecutionModel(
        PartitionedExecutionOptions? options = null,
        IServerLogger? logger = null)
    {
        options ??= new PartitionedExecutionOptions();

        // 시작 시점 검증. static 초기자에서 다른 static 을 읽어 0 이 되는 레거시의
        // DivideByZeroException 경로를 여기서 원천 차단한다 (CLAUDE.md 9.1).
        options.Validate();

        logger ??= NullServerLogger.Instance;
        _shutdownTimeout = options.ShutdownTimeout;
        _partitions = new ExecutionPartition[options.PartitionCount];

        for (int i = 0; i < _partitions.Length; i++)
        {
            _partitions[i] = new ExecutionPartition(i, options, logger);
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
    /// <remarks>진단·테스트용이다. 메트릭은 Phase 11 에서 정식으로 붙인다.</remarks>
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

        // 전부에게 먼저 알린 뒤 함께 기다린다. 순차로 하면 최악의 종료 시간이
        // 파티션 수 × 제한 시간이 된다.
        foreach (ExecutionPartition partition in _partitions)
        {
            partition.SignalStop();
        }

        foreach (ExecutionPartition partition in _partitions)
        {
            partition.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
