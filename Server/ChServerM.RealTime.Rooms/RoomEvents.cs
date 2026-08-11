using ChServerM.Diagnostics;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 축의 로그 이벤트 ID. 1720 대역을 쓴다(1700 대역 = Part V, ADR-0061 의 관례 연장).
/// </summary>
internal static class RoomEvents
{
    /// <summary>브로드캐스트 싱크가 송신 실패로 사망했다.</summary>
    internal static readonly EventId SinkFaulted = new(1721, nameof(SinkFaulted));
}
