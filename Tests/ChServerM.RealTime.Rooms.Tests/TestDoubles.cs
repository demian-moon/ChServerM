using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Execution;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.RealTime.Rooms.Tests;

/// <summary>카운터 메트릭만 이름별로 합산하는 싱크. 이중 집계 회귀(감사 2026-08-18 R-7) 검증용.</summary>
internal sealed class CountingMetricsSink : IMetricsSink
{
    private readonly Dictionary<string, long> _counts = [];

    internal long CountOf(string name) => _counts.TryGetValue(name, out long value) ? value : 0;

    public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        => _counts[name] = CountOf(name) + delta;

    public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
    {
    }

    public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
    {
    }
}

/// <summary>브로드캐스트가 수신하는 프레임을 기록만 하는 싱크. 룸·브로드캐스터 단위 테스트용.</summary>
internal sealed class RecordingSink : IRoomMemberSink
{
    private readonly RoomDeliveryStatus _response;

    internal RecordingSink(ConnectionId id, RoomDeliveryStatus response = RoomDeliveryStatus.Accepted)
    {
        ConnectionId = id;
        _response = response;
    }

    public ConnectionId ConnectionId { get; }

    internal List<byte[]> Delivered { get; } = [];

    public RoomDeliveryStatus TryDeliver(BroadcastFrame frame)
    {
        if (_response != RoomDeliveryStatus.Accepted)
        {
            return _response; // 소유권을 받지 않았다 — 해제하지 않는다.
        }

        Delivered.Add(frame.Written.ToArray());
        frame.Release(); // 수락했으므로 소비 후 정확히 한 번 해제한다.
        return RoomDeliveryStatus.Accepted;
    }
}

/// <summary>배타 작업을 그 자리에서 실행하는 파티션. 단일 스레드 테스트에서 배타성이 자명하다.</summary>
internal sealed class InlinePartition : IExecutionPartition
{
    internal bool RejectEnqueue { get; set; }

    internal int ExclusiveCount { get; private set; }

    public int Index => 0;

    public TaskScheduler Scheduler => TaskScheduler.Default;

    public bool TryPost<TWork>(in TWork work) where TWork : struct, IPartitionWork
    {
        work.Execute();
        return true;
    }

    public bool TryEnqueueExclusive(IPartitionExclusiveWork work)
    {
        if (RejectEnqueue)
        {
            return false;
        }

        ExclusiveCount++;
        // 테스트는 단일 스레드라 인라인 실행이 곧 배타 실행이다.
        work.ExecuteAsync().AsTask().GetAwaiter().GetResult();
        return true;
    }
}

/// <summary>파이프 하나로 Output 을 노출하는 커넥션. 읽기 쪽에서 와이어 바이트를 검증한다.</summary>
internal sealed class PipeConnection : IConnection, IAsyncDisposable
{
    private readonly Pipe _output = new();
    private readonly CancellationTokenSource _closed = new();

    internal PipeConnection(uint slot)
    {
        Id = new ConnectionId(slot, generation: 1);
    }

    public ConnectionId Id { get; }

    public PipeReader Input => throw new NotSupportedException("이 테스트 더블은 송신 전용이다.");

    public PipeWriter Output => _output.Writer;

    public IFeatureCollection Features => throw new NotSupportedException("이 테스트 더블은 송신 전용이다.");

    public CancellationToken ConnectionClosed => _closed.Token;

    internal PipeReader Reader => _output.Reader;

    /// <summary>수신자가 파이프를 닫아 다음 플러시가 IsCompleted 를 반환하게 만든다.</summary>
    internal void CompleteReader() => _output.Reader.Complete();

    public void Abort(in ConnectionCloseInfo info) => _closed.Cancel();

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync();
        _closed.Dispose();
    }
}
