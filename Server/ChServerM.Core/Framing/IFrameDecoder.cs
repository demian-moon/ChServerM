using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// 바이트 스트림에서 프레임 경계를 찾아낸다.
/// </summary>
/// <remarks>
/// <para>
/// 프레이밍은 <b>교체 가능한 축</b>이다. 고정 16바이트 헤더가 기본이지만
/// 길이 접두사만 있는 형식, 구분자 기반 형식, 기존 프로토콜과의 호환 형식이
/// 모두 이 인터페이스로 들어올 수 있다.
/// </para>
/// <para>
/// <b>구현은 상태를 갖지 않아야 한다.</b> 부분 프레임 상태는 <c>PipeReader</c>의 버퍼가
/// 이미 들고 있다. 디코더가 따로 상태를 두면 커넥션마다 인스턴스가 필요해지고,
/// 레거시가 겪은 "상태 머신이 어긋난 채 계속 도는" 문제가 되돌아온다.
/// </para>
/// <para><b>무할당이어야 한다.</b> 프레임당 힙 할당 0이 Phase 1의 합격 기준이다.</para>
/// </remarks>
public interface IFrameDecoder
{
    /// <summary>허용하는 최대 페이로드 크기(바이트).</summary>
    /// <remarks>
    /// <b>상한은 선택이 아니라 필수다.</b> 헤더의 길이 필드는 상대가 보낸 값이고,
    /// 검사 없이 믿으면 4바이트짜리 거짓말로 서버 메모리를 고갈시킬 수 있다.
    /// </remarks>
    int MaxPayloadLength { get; }

    /// <summary>이 디코더가 와이어에서 읽어낼 수 있는 논리 필드.</summary>
    /// <remarks>
    /// 조립 검사가 이 선언으로 죽은 조합을 시작 시점에 거부한다 — 예를 들어 압축 코덱과
    /// 플래그 없는 프레이밍을 조립하면 수신 측이 압축 플래그를 영영 볼 수 없어 해제가
    /// 조용히 발동하지 않는다(<see cref="FrameCodecCapabilities"/>).
    /// </remarks>
    FrameCodecCapabilities Capabilities { get; }

    /// <summary>버퍼 앞쪽에서 프레임 하나를 읽어낸다.</summary>
    /// <param name="buffer"><c>PipeReader</c>가 넘겨준 읽기 버퍼.</param>
    /// <returns>
    /// 디코딩 결과. 호출자는 <b>반드시</b>
    /// <see cref="FrameDecodeResult.Consumed"/>/<see cref="FrameDecodeResult.Examined"/>를
    /// <c>PipeReader.AdvanceTo</c>에 넘겨야 한다.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <paramref name="buffer"/>는 <b>세그먼트 경계를 넘을 수 있다.</b> 헤더 16바이트가
    /// 두 세그먼트에 걸쳐 있는 경우를 반드시 처리해야 한다 — 실전에서 흔하고,
    /// 여기서 무너지는 구현이 대부분이다.
    /// </para>
    /// <para>예외를 던지지 않는다. 모든 실패는 <see cref="FrameDecodeStatus"/>로 표현한다.</para>
    /// </remarks>
    FrameDecodeResult Decode(in ReadOnlySequence<byte> buffer);
}
