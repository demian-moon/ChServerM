using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Connections;
using ChServerM.Execution;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Identity;
using ChServerM.RealTime.Rooms;

namespace ChServerM.Bench.RealTime;

/// <summary>
/// 룸 브로드캐스트 비용 측정 — Phase 18 로드맵 항목 "룸 인원 대비 브로드캐스트 비용"의 근거.
/// </summary>
/// <remarks>
/// <para>
/// <b>무엇을 재는가.</b> 브로드캐스트 한 번의 비용이 룸 인원(10·100·1,000)에 어떻게
/// 비례하는가 — "직렬화 1회 + 멤버당 바이트 복사"가 설계 주장이므로, 멤버당 비용이
/// 인원과 무관하게 일정한지(선형 확장)와 할당이 0 인지를 본다.
/// </para>
/// <para>
/// <b>측정의 한계.</b> 싱크는 파이프에 쓰고 즉시 소비하는 인라인 파티션이다 — 실제 소켓
/// 송신·파티션 스케줄링 경합은 없다. 재는 것은 프레임 조립 + 팬아웃 + 파이프 복사까지의
/// 프레임워크 몫이다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
public class RoomBroadcastBenchmarks
{
    private static readonly MessageId BenchMessage = new(100);

    [Params(10, 100, 1_000)]
    public int Members { get; set; }

    private RoomBroadcaster _broadcaster = null!;
    private Room _room = null!;
    private byte[] _payload = null!;
    private DrainingConnection[] _connections = null!;

    [GlobalSetup]
    public void Setup()
    {
        var directory = new RoomDirectory(new RoomDirectoryOptions
        {
            MaxMembersPerRoom = Members,
        });
        directory.TryGetOrCreate(new RoomId(1), out Room? room);
        _room = room!;

        _broadcaster = new RoomBroadcaster(new FixedHeaderFrameEncoder(), ArrayPool<byte>.Shared);
        _payload = new byte[128];
        for (int i = 0; i < _payload.Length; i++)
        {
            _payload[i] = (byte)i; // 내용은 측정에 무관하다 — 결정적 채움이면 충분하다.
        }

        var partition = new InlinePartition();
        _connections = new DrainingConnection[Members];
        for (int i = 0; i < Members; i++)
        {
            _connections[i] = new DrainingConnection((uint)(i + 1));
            _room.TryJoin(new PartitionedMemberSink(_connections[i], partition));
        }
    }

    /// <summary>128B 페이로드 브로드캐스트 한 번. 조립 1회 + 멤버 수만큼 파이프 복사.</summary>
    [Benchmark]
    public int Broadcast() =>
        _broadcaster.Broadcast(_room, BenchMessage, _payload, FrameFlags.None).Accepted;

    /// <summary>배타 작업을 그 자리에서 실행하는 파티션(테스트 더블과 동일한 발상).</summary>
    private sealed class InlinePartition : IExecutionPartition
    {
        public int Index => 0;

        public TaskScheduler Scheduler => TaskScheduler.Default;

        public bool TryPost<TWork>(in TWork work) where TWork : struct, IPartitionWork
        {
            work.Execute();
            return true;
        }

        public bool TryEnqueueExclusive(IPartitionExclusiveWork work)
        {
            ValueTask task = work.ExecuteAsync();
            if (!task.IsCompleted)
            {
                task.AsTask().GetAwaiter().GetResult();
            }

            return true;
        }
    }

    /// <summary>쓰인 바이트를 즉시 버리는 커넥션 — 파이프가 차서 측정을 왜곡하지 않게 한다.</summary>
    private sealed class DrainingConnection : IConnection
    {
        private readonly Pipe _output;

        internal DrainingConnection(uint slot)
        {
            Id = new ConnectionId(slot, 1);
            // 임계값 0 = 파이프가 절대 배압을 걸지 않는다. 소비 태스크가 계속 버린다.
            _output = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
            _ = DrainForeverAsync(_output.Reader);
        }

        public ConnectionId Id { get; }

        public PipeReader Input => throw new NotSupportedException();

        public PipeWriter Output => _output.Writer;

        public IFeatureCollection Features => throw new NotSupportedException();

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async Task DrainForeverAsync(PipeReader reader)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
    }
}
