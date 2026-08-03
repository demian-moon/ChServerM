using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// 프레임 헤더를 와이어 형식으로 쓴다.
/// </summary>
/// <remarks>
/// <para>
/// 페이로드는 <b>호출자가 직접</b> 같은 출력 버퍼에 이어서 쓴다.
/// 인코더가 페이로드까지 받으면 직렬화 결과를 한 번 더 복사해야 하는데,
/// 그 복사가 바로 레거시의 프레임당 5~8회 힙 할당을 만든 원인이다.
/// </para>
/// <para>
/// 이 순서가 성립하려면 <b>직렬화가 끝난 뒤에야 헤더를 쓸 수 있어야 한다.</b>
/// FlatBuffers는 자체 버퍼에 빌드하므로 길이를 미리 알 수 있고, 조건이 맞는다.
/// </para>
/// </remarks>
public interface IFrameEncoder
{
    /// <summary>이 인코더가 쓰는 헤더 크기(바이트).</summary>
    int HeaderSize { get; }

    /// <summary>헤더를 쓴다.</summary>
    /// <param name="writer">출력 버퍼.</param>
    /// <param name="header">쓸 헤더. <see cref="FrameHeader.PayloadLength"/>가 실제 길이와 일치해야 한다.</param>
    /// <remarks>
    /// 길이가 실제와 어긋나면 <b>수신 측 프레임 경계가 통째로 밀린다.</b>
    /// 이 불일치는 커넥션이 끊길 때까지 이어지므로 디버깅이 매우 어렵다.
    /// </remarks>
    void WriteHeader(IBufferWriter<byte> writer, in FrameHeader header);
}
