using System;
using ChServerM.Identity;

namespace ChServerM.Execution;

/// <summary>
/// 작업을 어떤 스레드에서 돌릴지 정하는 축.
/// </summary>
/// <remarks>
/// <para>
/// <b>교체 가능한 축이다.</b> 기본은 키 기반 파티션 샤딩(ADR-0005)이지만,
/// 파티션 1개짜리 단일 스레드 모델(디버깅·결정적 테스트용)이나
/// 스레드풀에 그대로 던지는 모델(무상태 웹 프로필)도 같은 인터페이스로 들어온다.
/// </para>
/// <para>
/// <b>ADR-0005에는 검증 조건이 붙어 있다.</b> 이 모델이 코어 수에 대해 선형 확장을
/// 증명하지 못하면 결정 자체가 무효다. Phase 8 벤치마크에서 확인한다.
/// </para>
/// <para>
/// <b>파티션의 보장은 배타성+FIFO 순서다</b>(ADR-0008). 스레드 어피니티가 아니다 —
/// 상세는 <see cref="IExecutionPartition"/> 문서.
/// </para>
/// </remarks>
public interface IExecutionModel : IAsyncDisposable
{
    /// <summary>파티션 개수. 항상 1 이상이다.</summary>
    /// <remarks>
    /// 2의 거듭제곱일 필요는 없다. <see cref="PartitionKey.ToIndex"/>가
    /// 곱셈-시프트로 축소하므로 임의의 개수를 쓸 수 있다.
    /// </remarks>
    int PartitionCount { get; }

    /// <summary>키에 해당하는 파티션을 구한다.</summary>
    /// <param name="key">파티션 키.</param>
    /// <returns>이 키가 배정된 파티션.</returns>
    /// <remarks>
    /// <b>같은 키는 항상 같은 파티션을 돌려준다.</b> 이 성질이 무너지면
    /// 순서 보장이 통째로 사라진다. 그래서 파티션 개수는 실행 중 바뀌지 않는다.
    /// </remarks>
    IExecutionPartition GetPartition(PartitionKey key);

    /// <summary>인덱스로 파티션을 구한다.</summary>
    /// <param name="index"><c>0</c> 이상 <see cref="PartitionCount"/> 미만.</param>
    /// <returns>해당 인덱스의 파티션.</returns>
    /// <exception cref="ArgumentOutOfRangeException">인덱스가 범위를 벗어났을 때.</exception>
    /// <remarks>진단·전체 순회·부팅 시 워커 배치에 쓴다. 핫패스용이 아니다.</remarks>
    IExecutionPartition GetPartition(int index);
}
