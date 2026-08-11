using ChServerM.Identity;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 멤버의 브로드캐스트 수신 지점. 룸은 이 계약만 알고, 전달 방법은 구현이 정한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 룸이 커넥션을 직접 알면 "커넥션 <c>Output</c>은 단일 라이터"라는
/// 프레임워크 규약을 룸이 책임져야 한다. 이 인터페이스가 그 책임을 전달 구현으로 민다 —
/// 기본 구현 <see cref="PartitionedMemberSink"/>는 커넥션의 파티션 배타 슬롯에서 쓴다.
/// 테스트·봇·리플레이 수집기는 자기 싱크를 꽂는다.
/// </para>
/// <para>
/// <b>수명 규약(중요).</b> <see cref="TryDeliver"/>가 <see cref="RoomDeliveryStatus.Accepted"/>를
/// 반환하면 <b>프레임의 참조 하나가 싱크로 넘어간 것</b>이다 — 싱크는 소비 후
/// <see cref="BroadcastFrame.Release"/>를 정확히 한 번 호출한다. 그 외 반환값에서는 소유권이
/// 호출자(브로드캐스터)에 남으므로 싱크는 해제하지 않는다. 이 규약이 새면 풀 버퍼가
/// 유실되거나 이중 반납된다(레거시 반납 누수의 재발 방지 — 규약을 반환값에 실었다).
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="TryDeliver"/>는 아무 스레드에서나 불릴 수 있다.
/// 블로킹하지 않아야 한다 — 브로드캐스트 루프가 멤버 수만큼 이것을 연달아 부른다.
/// </para>
/// </remarks>
public interface IRoomMemberSink
{
    /// <summary>이 싱크가 대표하는 커넥션. 룸 멤버십의 키다.</summary>
    ConnectionId ConnectionId { get; }

    /// <summary>브로드캐스트 프레임 하나를 전달 시도한다. 블로킹하지 않는다.</summary>
    /// <param name="frame">인코딩된 프레임. 수락 시에만 소유권(참조 1)이 넘어간다.</param>
    RoomDeliveryStatus TryDeliver(BroadcastFrame frame);
}
