using System;

namespace ChServerM.Framing;

/// <summary>
/// 고정 헤더 프레이밍의 설정.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 프레이밍에서 유일하게 "정책"인 값들을 한곳에 모은다. 특히
/// <see cref="MaxPayloadLength"/>는 기본값에 기대면 안 되는 값이다 — 워크로드마다
/// 정당한 최대 메시지 크기가 다르고, 이 값이 곧 <b>커넥션당 최악의 메모리 점유</b>다.
/// </para>
/// <para>
/// <b>검증은 시작 시점에 한다.</b> <see cref="Validate"/>는 예외를 던진다. 핫패스가
/// 아니고, 잘못된 설정으로 뜬 서버는 조용히 도는 것보다 안 뜨는 편이 낫다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 조립 시점에만 쓰고, 디코더·인코더 생성 이후에는 읽지도 않는다.
/// 값은 생성자에서 복사되므로 나중에 이 객체를 바꿔도 동작 중인 코덱에 영향이 없다 —
/// <b>의도한 것이다.</b> 실행 중 프레임 상한이 바뀌면 진행 중인 디코딩이 일관성을 잃는다.
/// </para>
/// <para>
/// 레거시는 커넥션마다 <b>64KB 고정 송신 버퍼</b>를 잡았고(1만 접속 = 640MB),
/// 그 크기가 설정도 아니고 상수였다. 여기서는 설정이되 <b>상한이 검증되는</b> 값이다.
/// </para>
/// </remarks>
public sealed class FramingOptions
{
    /// <summary>기본 최대 페이로드 크기. 1 MiB.</summary>
    /// <remarks>
    /// 안전하게 작은 값이다. 대용량 전송이 필요하면 조각화(<see cref="FrameFlags.Fragmented"/>)를
    /// 쓰거나 이 값을 올린다. <b>올릴 때는 동시 접속 수를 곱해 최악의 메모리를 계산한다.</b>
    /// </remarks>
    public const int DefaultMaxPayloadLength = 1024 * 1024;

    /// <summary>허용하는 최대 페이로드 크기의 절대 상한. 64 MiB.</summary>
    /// <remarks>
    /// 설정 실수로 <c>int.MaxValue</c>를 넣는 것을 막는다. 이 값을 넘겨야 한다면
    /// 프레이밍이 아니라 스트리밍 전송을 써야 한다는 신호다.
    /// </remarks>
    public const int AbsoluteMaxPayloadLength = 64 * 1024 * 1024;

    /// <summary>받아들일 최대 페이로드 크기(바이트).</summary>
    /// <remarks>
    /// 이 값을 넘는 길이 필드를 보면 <see cref="FrameDecodeStatus.TooLarge"/>로 커넥션을 닫는다.
    /// <b>버퍼를 잡기 전에</b> 판정하므로, 4바이트짜리 거짓말로 메모리를 고갈시킬 수 없다.
    /// </remarks>
    public int MaxPayloadLength { get; set; } = DefaultMaxPayloadLength;

    /// <summary>이 노드가 말하는 프로토콜 버전.</summary>
    /// <remarks>
    /// 송신 헤더에 실리고, 수신 시 이 값과 다르면
    /// <see cref="FrameDecodeStatus.VersionMismatch"/>로 커넥션을 닫는다.
    /// </remarks>
    public ushort ProtocolVersion { get; set; } = FrameHeader.CurrentVersion;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효 범위를 벗어났을 때.</exception>
    /// <remarks>서버 시작 시점에 호출한다. 코덱 생성자도 내부적으로 호출한다.</remarks>
    public void Validate()
    {
        if (MaxPayloadLength <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxPayloadLength)}는 1 이상이어야 한다. 현재 값: {MaxPayloadLength}");
        }

        if (MaxPayloadLength > AbsoluteMaxPayloadLength)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxPayloadLength)}({MaxPayloadLength})가 절대 상한" +
                $"({AbsoluteMaxPayloadLength})을 넘는다. 이 크기가 필요하면 조각화나 스트리밍을 쓴다.");
        }

        if (ProtocolVersion == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(ProtocolVersion)} 0은 '설정되지 않음'을 뜻하는 센티넬이다. 1 이상을 쓴다.");
        }
    }
}
