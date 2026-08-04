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
/// <para>
/// <b>와이어 포맷은 구현이 소유한다 (ADR-0010).</b> 이 계약은 논리 엔벨로프와
/// 페이로드 길이만 받는다 — 헤더가 몇 바이트인지, 어떤 필드가 어디 놓이는지,
/// 프로토콜 버전이 무엇인지는 전부 구현의 사정이다. 가변 길이 헤더(varint)도
/// 이 계약 안에서 성립한다.
/// </para>
/// </remarks>
public interface IFrameEncoder
{
    /// <summary>이 인코더가 쓸 수 있는 헤더 크기의 상한(바이트).</summary>
    /// <remarks>
    /// 정확한 크기가 아니라 <b>상한</b>이다 — 가변 길이 헤더는 프레임마다 크기가 다르다.
    /// 조립 검사(최대 프레임 ≤ 전송 버퍼 한계, ADR-0007)가 최악 조건을 계산하는 데 쓴다.
    /// </remarks>
    int MaxHeaderSize { get; }

    /// <summary>헤더를 쓴다.</summary>
    /// <param name="writer">출력 버퍼.</param>
    /// <param name="envelope">프레임의 논리 메타데이터.</param>
    /// <param name="payloadLength">
    /// 뒤이어 쓰일 페이로드의 바이트 수. <b>실제 길이와 일치해야 한다.</b>
    /// </param>
    /// <remarks>
    /// <para>
    /// 길이가 실제와 어긋나면 <b>수신 측 프레임 경계가 통째로 밀린다.</b>
    /// 이 불일치는 커넥션이 끊길 때까지 이어지므로 디버깅이 매우 어렵다.
    /// </para>
    /// <para>
    /// <b>표현 불가 값은 거부한다 (ADR-0010).</b> 와이어에 해당 필드가 없는 구현은
    /// 기본값이 아닌 <see cref="MessageEnvelope.Flags"/>/<see cref="MessageEnvelope.Sequence"/> 를
    /// 받으면 예외를 던진다. 조용히 버리면 압축·리플레이 방지가 소리 없이 무력화된다.
    /// 이 경로는 우리 코드의 조립 실수이지 원격 입력이 아니므로 예외가 옳다.
    /// </para>
    /// </remarks>
    void WriteHeader(IBufferWriter<byte> writer, in MessageEnvelope envelope, int payloadLength);
}
