using System;
using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// 고정 16바이트 헤더를 쓰는 인코더 (ADR-0002).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 헤더만 쓴다. 페이로드는 호출자가 같은 <see cref="IBufferWriter{T}"/>에
/// 이어서 쓴다. 인코더가 페이로드까지 받으면 직렬화 결과를 한 번 더 복사해야 하는데,
/// 그 복사가 레거시의 프레임당 5~8회 힙 할당을 만든 직접적 원인이다.
/// </para>
/// <para>
/// <b>보내기 전에 검증한다.</b> 상한을 넘는 프레임이나 모르는 플래그를 그대로 내보내면
/// 상대가 커넥션을 끊는다. 그때는 원인이 이쪽 코드라는 걸 알기 어렵다 —
/// 여기서 예외로 즉시 드러낸다. 이 경로는 우리 코드의 버그이지 원격 입력이 아니므로
/// 예외가 옳다(핫패스 예외 금지 규칙의 예외 조건).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인코더 자체는 불변이라 스레드 안전하다.
/// 다만 <see cref="IBufferWriter{T}"/>는 대개 아니다 —
/// 하나의 <c>PipeWriter</c>에 여러 스레드가 동시에 쓰면 안 된다.
/// </para>
/// <para><b>할당.</b> 프레임당 힙 할당 0.</para>
/// </remarks>
public sealed class FixedHeaderFrameEncoder : IFrameEncoder
{
    private readonly int _maxPayloadLength;
    private readonly ushort _protocolVersion;

    /// <summary>설정으로 인코더를 만든다.</summary>
    /// <param name="options">프레이밍 설정.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정 값이 유효하지 않을 때.</exception>
    public FixedHeaderFrameEncoder(FramingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _maxPayloadLength = options.MaxPayloadLength;
        _protocolVersion = options.ProtocolVersion;
    }

    /// <summary>값을 직접 지정해 인코더를 만든다.</summary>
    /// <param name="maxPayloadLength">허용하는 최대 페이로드 크기.</param>
    /// <param name="protocolVersion">쓸 프로토콜 버전.</param>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public FixedHeaderFrameEncoder(
        int maxPayloadLength = FramingOptions.DefaultMaxPayloadLength,
        ushort protocolVersion = FrameHeader.CurrentVersion)
        : this(new FramingOptions { MaxPayloadLength = maxPayloadLength, ProtocolVersion = protocolVersion })
    {
    }

    /// <inheritdoc />
    /// <remarks>고정 헤더이므로 상한이 곧 정확한 크기다.</remarks>
    public int MaxHeaderSize => FrameHeader.Size;

    /// <summary>허용하는 최대 페이로드 크기.</summary>
    public int MaxPayloadLength => _maxPayloadLength;

    /// <summary>쓰는 프로토콜 버전.</summary>
    public ushort ProtocolVersion => _protocolVersion;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="writer"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 페이로드 길이가 음수이거나 <see cref="MaxPayloadLength"/>를 넘을 때.
    /// </exception>
    /// <exception cref="ArgumentException">정의되지 않은 플래그 비트가 켜져 있을 때.</exception>
    /// <remarks>
    /// 프로토콜 버전은 여기서 찍는다 — 버전은 인코더의 설정이지 메시지의 속성이
    /// 아니고(ADR-0010), 호출자가 채우게 두면 틀리는 실수가 가능해진다.
    /// </remarks>
    public void WriteHeader(IBufferWriter<byte> writer, in MessageEnvelope envelope, int payloadLength)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        if (payloadLength > _maxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                payloadLength,
                $"페이로드가 상한({_maxPayloadLength})을 넘는다. 상대가 이 프레임을 거부하고 커넥션을 닫는다.");
        }

        if (!FrameHeaderCodec.AreFlagsKnown(envelope.Flags))
        {
            throw new ArgumentException(
                $"정의되지 않은 플래그 비트가 켜져 있다: {envelope.Flags}. " +
                $"플래그를 추가했다면 {nameof(FrameHeaderCodec)}.{nameof(FrameHeaderCodec.KnownFlags)}도 갱신한다.",
                nameof(envelope));
        }

        FrameHeader header = new(
            envelope.MessageId, payloadLength, envelope.Flags, envelope.Sequence, _protocolVersion);

        Span<byte> destination = writer.GetSpan(FrameHeader.Size);
        FrameHeaderCodec.Write(destination, header);
        writer.Advance(FrameHeader.Size);
    }
}
