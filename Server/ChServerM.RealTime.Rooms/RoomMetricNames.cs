using ChServerM.Diagnostics;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 축의 메트릭 이름. <see cref="IMetricsSink"/>에 넘기는 문자열의 정본이다.
/// </summary>
/// <remarks>
/// Core 의 <c>MetricNames</c>에 넣지 않는 이유는 이 어셈블리가 선택 축이기 때문이다
/// (<c>RealTimeMetricNames</c>와 같은 판단, ADR-0064). 거부·사망이 목록에 있는 이유:
/// 조용한 유실은 관측되지 않으면 존재하지 않는 것과 같다(9.6).
/// </remarks>
public static class RoomMetricNames
{
    /// <summary>브로드캐스트 호출 수. 카운터.</summary>
    public const string Broadcasts = DiagnosticNames.Prefix + ".room.broadcasts";

    /// <summary>싱크가 수락한 프레임 수(큐 진입). 카운터.</summary>
    public const string FramesAccepted = DiagnosticNames.Prefix + ".room.frames.accepted";

    /// <summary>커넥션까지 쓰기·플러시가 끝난 프레임 수. 카운터.</summary>
    public const string FramesDelivered = DiagnosticNames.Prefix + ".room.frames.delivered";

    /// <summary>거부된 프레임 수(큐 포화·닫힌 싱크). 카운터.</summary>
    /// <remarks>
    /// 집계 책임은 <see cref="RoomBroadcaster"/> <b>한 곳</b>이다. 싱크도 같은 이름으로 세면
    /// 양쪽에 같은 <see cref="IMetricsSink"/>를 꽂았을 때(자연스러운 구성) 거부 1건이 2로
    /// 집계되어 알람 임계값이 흔들린다 — 싱크 내부의 큐 포화는
    /// <see cref="SinkQueueFull"/>이라는 별도 이름으로 관측한다(감사 2026-08-18 R-7).
    /// </remarks>
    public const string FramesRejected = DiagnosticNames.Prefix + ".room.frames.rejected";

    /// <summary>싱크의 송신 큐가 포화하여 프레임을 거부한 수. 카운터.</summary>
    /// <remarks>
    /// <see cref="FramesRejected"/>(브로드캐스터 집계, 포화+닫힘 합산)의 부분집합이되 이름을
    /// 분리해 이중 집계를 없앤다 — "QueueFull 1건 = 카운트 1"(감사 2026-08-18 R-7). 이 값이
    /// 크면 그 멤버의 소비가 느리다는 뜻이다 — 큐 깊이
    /// (<see cref="PartitionedMemberSinkOptions.SendQueueDepth"/>) 조정의 입력이다.
    /// </remarks>
    public const string SinkQueueFull = DiagnosticNames.Prefix + ".room.sink.queue.full";

    /// <summary>송신 실패로 사망한 싱크 수. 카운터.</summary>
    public const string SinkFaults = DiagnosticNames.Prefix + ".room.sink.faults";
}
