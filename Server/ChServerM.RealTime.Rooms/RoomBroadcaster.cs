using System;
using System.Buffers;
using System.Threading;
using ChServerM.Diagnostics;
using ChServerM.Framing;
using ChServerM.Identity;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 룸 브로드캐스터 — 프레임을 <b>한 번</b> 조립해 룸의 모든 싱크에 나눠 준다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "같은 페이로드를 N 명에게 보낼 때 직렬화 1회"(Phase 18)의 실행 지점이다.
/// 호출자가 페이로드를 한 번 직렬화해 오면, 여기서 헤더 인코딩을 한 번 더 하고
/// (<see cref="IFrameEncoder"/>는 <see cref="IBufferWriter{T}"/>를 받으므로 그대로 조립된다)
/// 참조 계수 프레임(<see cref="BroadcastFrame"/>)으로 공유한다. 멤버당 비용은 파이프에의
/// 바이트 복사뿐이다.
/// </para>
/// <para>
/// <b>시퀀스 규약.</b> 브로드캐스트 프레임의 <see cref="MessageEnvelope.Sequence"/>는 0 이다 —
/// 헤더를 N 명이 공유하므로 커넥션별 일련번호를 실을 수 없다(실으면 1회 인코딩이 깨진다).
/// 커넥션별 시퀀스가 필요한 메시지는 브로드캐스트 대상이 아니다(ADR-0064).
/// </para>
/// <para>
/// <b>수명 규약.</b> <see cref="ArrayPool{T}"/>은 필수 인자다 — 최악 미처리 대여량이
/// <b>송신 큐 깊이 × 멤버 수</b>라는 계산을 조립하는 쪽이 해야 하기 때문이다(ADR-0051 규약).
/// 프레임 래퍼 객체는 내부 풀로 재사용한다(브로드캐스트당 할당 0 이 정상 상태).
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전. 여러 스레드가 동시에 브로드캐스트해도 된다.</para>
/// </remarks>
public sealed class RoomBroadcaster
{
    private readonly IFrameEncoder _encoder;
    private readonly ArrayPool<byte> _payloadPool;
    private readonly IMetricsSink? _metrics;
    private readonly int _framePoolCapacity;
    private readonly int _initialFrameCapacity;

    private BroadcastFrame? _framePoolHead; // Treiber 스택.
    private int _framePoolCount;

    /// <summary>브로드캐스터를 만든다.</summary>
    /// <param name="encoder">프레임 인코더. 커넥션 수신 경로와 같은 와이어 규약이어야 한다.</param>
    /// <param name="payloadPool">
    /// 프레임 버퍼 풀. <b>필수 인자다</b> — 기본 공유 풀을 몰래 쓰면 "최악 몇 바이트가
    /// 대여 중인가"를 아무도 계산하지 않게 된다(ADR-0051 이 실측으로 고정한 규약).
    /// </param>
    /// <param name="options">추가 설정. <see langword="null"/>이면 기본값.</param>
    public RoomBroadcaster(IFrameEncoder encoder, ArrayPool<byte> payloadPool, RoomBroadcasterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(payloadPool);
        options?.Validate();

        _encoder = encoder;
        _payloadPool = payloadPool;
        _metrics = options?.MetricsSink;
        _framePoolCapacity = options?.FramePoolCapacity ?? RoomBroadcasterOptions.DefaultFramePoolCapacity;
        _initialFrameCapacity = options?.InitialFrameCapacity ?? RoomBroadcasterOptions.DefaultInitialFrameCapacity;
    }

    /// <summary>룸의 모든 멤버(예외 하나 제외 가능)에게 프레임을 브로드캐스트한다.</summary>
    /// <param name="room">대상 룸.</param>
    /// <param name="messageId">메시지 타입.</param>
    /// <param name="payload">직렬화가 끝난 페이로드. 호출자가 <b>한 번만</b> 직렬화한다.</param>
    /// <param name="flags">페이로드에 적용된 변환(압축 등). 수신 쪽 해석과 일치해야 한다.</param>
    /// <param name="exceptConnection">
    /// 제외할 커넥션(대개 발신자 본인). <c>default</c>면 아무도 제외하지 않는다.
    /// </param>
    /// <returns>수락·거부 수. <b>거부를 버리지 않는다</b> — 관측되지 않는 유실은 존재하지 않는 것과 같다(9.6).</returns>
    public RoomBroadcastResult Broadcast(
        Room room,
        MessageId messageId,
        ReadOnlySpan<byte> payload,
        FrameFlags flags,
        ConnectionId exceptConnection = default)
    {
        ArgumentNullException.ThrowIfNull(room);

        IRoomMemberSink[] members = room.MembersSnapshot;
        if (members.Length == 0)
        {
            return new RoomBroadcastResult(0, 0);
        }

        BroadcastFrame frame = RentFrame();
        var envelope = new MessageEnvelope(messageId, flags, sequence: 0);
        _encoder.WriteHeader(frame, in envelope, payload.Length);
        payload.CopyTo(frame.GetSpan(payload.Length));
        frame.Advance(payload.Length);

        int accepted = 0;
        int rejected = 0;

        foreach (IRoomMemberSink member in members)
        {
            if (member.ConnectionId == exceptConnection)
            {
                continue;
            }

            frame.AddReference();
            if (member.TryDeliver(frame) == RoomDeliveryStatus.Accepted)
            {
                accepted++;
            }
            else
            {
                // 소유권이 넘어가지 않았다 — 우리가 준 참조를 우리가 놓는다.
                frame.Release();
                rejected++;
            }
        }

        frame.Release(); // 조립자 몫.

        _metrics?.Count(RoomMetricNames.Broadcasts, 1, default);
        _metrics?.Count(RoomMetricNames.FramesAccepted, accepted, default);
        if (rejected > 0)
        {
            _metrics?.Count(RoomMetricNames.FramesRejected, rejected, default);
        }

        return new RoomBroadcastResult(accepted, rejected);
    }

    private BroadcastFrame RentFrame()
    {
        var spinner = new SpinWait();
        while (true)
        {
            BroadcastFrame? head = Volatile.Read(ref _framePoolHead);
            if (head is null)
            {
                var created = new BroadcastFrame(_payloadPool, _initialFrameCapacity);
                created.Attach(this);
                created.Reset();
                return created;
            }

            if (Interlocked.CompareExchange(ref _framePoolHead, head.PoolNext, head) == head)
            {
                Interlocked.Decrement(ref _framePoolCount);
                head.PoolNext = null;
                head.Reset();
                return head;
            }

            spinner.SpinOnce(); // 재시도 시에만 스핀한다 (9.3).
        }
    }

    /// <summary>마지막 참조가 놓인 프레임을 회수한다. <see cref="BroadcastFrame.Release"/> 전용.</summary>
    internal void ReturnFrame(BroadcastFrame frame)
    {
        if (Interlocked.Increment(ref _framePoolCount) > _framePoolCapacity)
        {
            Interlocked.Decrement(ref _framePoolCount);
            frame.ReturnBuffer(); // 상한 초과분은 버퍼를 반납하고 래퍼는 GC 에 맡긴다 — 무제한 풀 금지.
            return;
        }

        var spinner = new SpinWait();
        while (true)
        {
            BroadcastFrame? head = Volatile.Read(ref _framePoolHead);
            frame.PoolNext = head;
            if (Interlocked.CompareExchange(ref _framePoolHead, frame, head) == head)
            {
                return;
            }

            spinner.SpinOnce(); // 재시도 시에만 스핀한다 (9.3).
        }
    }
}

/// <summary>
/// <see cref="RoomBroadcaster"/>의 설정.
/// </summary>
public sealed class RoomBroadcasterOptions
{
    /// <summary>기본 프레임 래퍼 풀 상한. 256.</summary>
    public const int DefaultFramePoolCapacity = 256;

    /// <summary>기본 프레임 초기 용량. 4 KiB.</summary>
    public const int DefaultInitialFrameCapacity = 4 * 1024;

    /// <summary>프레임 래퍼 풀 상한. 동시에 살아 있는 브로드캐스트 프레임 수의 기대치로 잡는다.</summary>
    public int FramePoolCapacity { get; set; } = DefaultFramePoolCapacity;

    /// <summary>프레임 버퍼 초기 용량. 대표 브로드캐스트 페이로드보다 크게 잡으면 성장이 없다.</summary>
    public int InitialFrameCapacity { get; set; } = DefaultInitialFrameCapacity;

    /// <summary>메트릭 싱크(Phase 11). <see langword="null"/>이면 기록하지 않는다.</summary>
    public IMetricsSink? MetricsSink { get; set; }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (FramePoolCapacity < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(FramePoolCapacity)}는 음수일 수 없다. 풀 비활성은 0이다. 현재 값: {FramePoolCapacity}");
        }

        if (InitialFrameCapacity < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(InitialFrameCapacity)}는 1 이상이어야 한다. 현재 값: {InitialFrameCapacity}");
        }
    }
}

/// <summary>
/// 브로드캐스트 한 번의 결과. 거부 수가 값으로 드러난다(9.6 — 조용한 유실 금지).
/// </summary>
public readonly struct RoomBroadcastResult : IEquatable<RoomBroadcastResult>
{
    internal RoomBroadcastResult(int accepted, int rejected)
    {
        Accepted = accepted;
        Rejected = rejected;
    }

    /// <summary>싱크가 수락한 수. 수락은 큐 진입이지 전달 완료가 아니다 — 전달 실패는 싱크가 보고한다.</summary>
    public int Accepted { get; }

    /// <summary>거부된 수(큐 포화·닫힌 싱크).</summary>
    public int Rejected { get; }

    /// <inheritdoc />
    public bool Equals(RoomBroadcastResult other) => Accepted == other.Accepted && Rejected == other.Rejected;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RoomBroadcastResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Accepted, Rejected);

    /// <summary>두 값이 같은지 비교한다.</summary>
    public static bool operator ==(RoomBroadcastResult left, RoomBroadcastResult right) => left.Equals(right);

    /// <summary>두 값이 다른지 비교한다.</summary>
    public static bool operator !=(RoomBroadcastResult left, RoomBroadcastResult right) => !left.Equals(right);
}
