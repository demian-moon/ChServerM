using System;
using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// 고정 16바이트 헤더로 프레임 경계를 찾아내는 디코더 (ADR-0002).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> TCP는 바이트 스트림이지 메시지 스트림이 아니다. 한 번의 읽기에
/// 프레임이 3.5개 들어올 수도, 헤더 절반만 들어올 수도 있다. 그 경계를 복원하는 것이
/// 이 타입의 유일한 책임이다.
/// </para>
/// <para>
/// <b>상태를 갖지 않는다.</b> 부분 프레임은 <c>PipeReader</c>의 버퍼가 이미 들고 있으므로,
/// 디코더가 따로 상태를 둘 이유가 없다. 그 결과 <b>인스턴스 하나를 모든 커넥션이
/// 공유</b>할 수 있고, 커넥션당 할당이 그만큼 줄어든다.
/// </para>
/// <para>
/// 레거시는 반대였다. 5단 상태 머신을 커넥션마다 들고 있었고, 체크섬 예외를 상위에서
/// 삼킨 뒤 <b>상태가 어긋난 채 파싱을 계속</b>했다. 그래서 손상된 프레임 하나가
/// 커넥션 전체를 영구히 오염시켰다. 여기서는 실패가 곧 커넥션 종료이고,
/// 재동기화를 시도하지 않는다 — <b>어디서부터 다시 맞춰야 할지 알 수 없기 때문</b>이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 불변이므로 스레드 안전하다. 여러 커넥션이 동시에 호출해도 된다.
/// </para>
/// <para>
/// <b>할당.</b> 프레임당 힙 할당 0. 헤더가 세그먼트 경계를 넘을 때만
/// 16바이트 <c>stackalloc</c>을 쓴다.
/// </para>
/// </remarks>
public sealed class FixedHeaderFrameDecoder : IFrameDecoder
{
    private readonly int _maxPayloadLength;
    private readonly ushort _acceptedVersion;

    /// <summary>설정으로 디코더를 만든다.</summary>
    /// <param name="options">프레이밍 설정.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정 값이 유효하지 않을 때.</exception>
    /// <remarks>
    /// 값을 <b>복사</b>한다. 생성 이후 <paramref name="options"/>를 바꿔도 이 디코더는
    /// 영향받지 않는다 — 동작 중에 프레임 상한이 바뀌면 진행 중인 디코딩이 일관성을 잃는다.
    /// </remarks>
    public FixedHeaderFrameDecoder(FramingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _maxPayloadLength = options.MaxPayloadLength;
        _acceptedVersion = options.ProtocolVersion;
    }

    /// <summary>값을 직접 지정해 디코더를 만든다.</summary>
    /// <param name="maxPayloadLength">허용하는 최대 페이로드 크기.</param>
    /// <param name="acceptedVersion">받아들일 프로토콜 버전.</param>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    /// <remarks>테스트와 간단한 조립을 위한 지름길이다.</remarks>
    public FixedHeaderFrameDecoder(
        int maxPayloadLength = FramingOptions.DefaultMaxPayloadLength,
        ushort acceptedVersion = FrameHeader.CurrentVersion)
        : this(new FramingOptions { MaxPayloadLength = maxPayloadLength, ProtocolVersion = acceptedVersion })
    {
    }

    /// <inheritdoc />
    public int MaxPayloadLength => _maxPayloadLength;

    /// <summary>받아들이는 프로토콜 버전.</summary>
    public ushort AcceptedVersion => _acceptedVersion;

    /// <inheritdoc />
    public FrameDecodeResult Decode(in ReadOnlySequence<byte> buffer)
    {
        // 1) 헤더조차 다 오지 않았다. examined 를 버퍼 끝으로 둬야 파이프가 더 읽는다.
        if (buffer.Length < FrameHeader.Size)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);
        }

        FrameDecodeStatus status = TryReadHeader(buffer, out FrameHeader header);
        if (status != FrameDecodeStatus.Decoded)
        {
            // 재동기화하지 않는다. 스트림의 어디가 프레임 경계인지 더는 알 수 없다.
            return FrameDecodeResult.Failed(status, buffer.Start);
        }

        // long 으로 계산한다. int 로 더하면 PayloadLength 가 상한에 가까울 때 오버플로 여지가 있다.
        long totalLength = FrameHeader.Size + (long)header.PayloadLength;

        // 2) 헤더는 왔지만 페이로드가 아직이다.
        if (buffer.Length < totalLength)
        {
            return FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);
        }

        ReadOnlySequence<byte> payload = buffer.Slice(FrameHeader.Size, header.PayloadLength);
        return FrameDecodeResult.Decoded(header, payload, buffer.GetPosition(totalLength));
    }

    /// <summary>세그먼트 경계를 넘는 경우까지 포함해 헤더를 읽는다.</summary>
    /// <remarks>
    /// 대부분의 프레임은 빠른 경로를 탄다. 느린 경로는 <b>실전에서 반드시 발생</b>하며
    /// (TCP 세그먼트 경계는 프레임 경계를 존중하지 않는다), 여기서 무너지는 구현이 흔하다.
    /// </remarks>
    private FrameDecodeStatus TryReadHeader(in ReadOnlySequence<byte> buffer, out FrameHeader header)
    {
        ReadOnlySpan<byte> first = buffer.FirstSpan;

        if (first.Length >= FrameHeader.Size)
        {
            // 빠른 경로 — 헤더가 첫 세그먼트 안에 다 있다. 복사 없음.
            return FrameHeaderCodec.TryRead(first, _maxPayloadLength, _acceptedVersion, out header);
        }

        // 느린 경로 — 헤더가 세그먼트에 걸쳐 있다. 16바이트만 스택에 모은다(힙 할당 없음).
        Span<byte> scratch = stackalloc byte[FrameHeader.Size];
        buffer.Slice(0, FrameHeader.Size).CopyTo(scratch);
        return FrameHeaderCodec.TryRead(scratch, _maxPayloadLength, _acceptedVersion, out header);
    }
}
