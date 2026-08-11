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
    public const string FramesRejected = DiagnosticNames.Prefix + ".room.frames.rejected";

    /// <summary>송신 실패로 사망한 싱크 수. 카운터.</summary>
    public const string SinkFaults = DiagnosticNames.Prefix + ".room.sink.faults";
}
