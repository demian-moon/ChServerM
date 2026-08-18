using System;
using ChServerM.Framing;
using ChServerM.Transports;

namespace ChServerM.Hosting;

/// <summary>
/// 축을 잘못 조합했을 때 시작 시점에 막는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 각 축은 자기 설정만 검증할 수 있다. 프레이밍은 전송을 모르고
/// 전송은 프레이밍을 모른다. 그래서 <b>둘의 조합이 성립하는지</b>는 조립하는 쪽만
/// 검사할 수 있다.
/// </para>
/// <para>
/// 여기서 걸러내는 실수는 전부 <b>런타임에 조용히 나타나는</b> 종류다.
/// "거부가 붕괴보다 낫다"(CLAUDE.md 9.6)를 조립 시점으로 앞당긴다.
/// </para>
/// </remarks>
internal static class CompositionGuard
{
    /// <summary>프레임 크기가 전송 버퍼 안에 들어가는지 확인한다.</summary>
    /// <param name="transport">검사할 전송. <see cref="ITransportBufferLimits"/> 를 구현하지 않으면 건너뛴다.</param>
    /// <param name="decoder">프레임 디코더.</param>
    /// <param name="encoder">프레임 인코더.</param>
    /// <exception cref="InvalidOperationException">최대 프레임이 전송 버퍼보다 클 때.</exception>
    /// <remarks>
    /// <para>
    /// <b>이 검사가 없으면 증상이 최악이다.</b> 작은 프레임은 멀쩡히 오가다가
    /// 큰 메시지 하나에서만 멈추고, 예외도 로그도 없다. 커넥션이 그냥 응답하지 않는다.
    /// </para>
    /// <para>
    /// 원인은 두 성질의 충돌이다 — 디코더는 완전한 프레임이 오기 전에 아무것도 소비할 수
    /// 없고(경계를 잃으므로), 전송 버퍼는 소비되지 않은 바이트가 임계값에 닿으면
    /// 쓰기를 멈춘다. 프레임이 임계값보다 크면 영원히 벗어나지 못한다.
    /// </para>
    /// </remarks>
    public static void EnsureFrameFitsInTransportBuffer(
        object transport,
        IFrameDecoder decoder,
        IFrameEncoder encoder)
    {
        if (transport is not ITransportBufferLimits limits)
        {
            // 버퍼 개념이 없는 전송(메시지 단위, 공유 메모리 등)은 검사 대상이 아니다.
            return;
        }

        long maxFrame = (long)decoder.MaxPayloadLength + encoder.MaxHeaderSize;

        if (maxFrame > limits.MaxBufferedBytesPerConnection)
        {
            throw new InvalidOperationException(
                $"최대 프레임({maxFrame}B = 페이로드 {decoder.MaxPayloadLength} + 헤더 {encoder.MaxHeaderSize})이 " +
                $"전송의 커넥션당 버퍼 한계({limits.MaxBufferedBytesPerConnection}B)를 넘는다. " +
                "이대로 두면 그 크기의 프레임에서 커넥션이 조용히 교착한다 " +
                "(디코더는 부분 프레임을 소비할 수 없고, 버퍼가 차면 쓰기가 멈춘다). " +
                "전송의 PauseWriterThreshold 를 올리거나(커넥션 수를 곱한 메모리를 계산할 것), " +
                "프레이밍의 MaxPayloadLength 를 낮추거나, 조각화를 쓴다.");
        }
    }

    /// <summary>압축 코덱이 조립됐다면 프레이밍이 플래그 필드를 실을 수 있는지 확인한다.</summary>
    /// <exception cref="InvalidOperationException">플래그를 싣지 못하는 프레이밍과 압축이 함께 조립됐다.</exception>
    /// <remarks>
    /// 이 검사가 없으면 증상이 양쪽에서 다르게 조용하다 — 송신은 인코더의 런타임 예외
    /// (첫 압축 프레임에서야), 수신은 플래그가 없어 해제가 <b>영영 발동하지 않는 무동작</b>이다.
    /// 감사 2026-08-18 H-8 의 결정(capabilities 표면 추가)이 이 검사를 가능하게 했다.
    /// </remarks>
    public static void EnsureCodecSupportsCompression(IFrameEncoder encoder, IFrameDecoder decoder)
    {
        if ((encoder.Capabilities & FrameCodecCapabilities.Flags) == 0
            || (decoder.Capabilities & FrameCodecCapabilities.Flags) == 0)
        {
            throw new InvalidOperationException(
                "압축 코덱(UsePayloadCodec)이 조립됐지만 프레이밍이 플래그 필드를 싣지 못한다 " +
                $"(인코더 {encoder.Capabilities}, 디코더 {decoder.Capabilities}). " +
                "압축 여부는 프레임 플래그로 전달되므로 이 조합은 성립하지 않는다 — " +
                "고정 헤더 프레이밍을 쓰거나 압축을 뺀다.");
        }
    }

    /// <summary>버전 협상이 조립됐다면 프레이밍이 버전 필드를 실을 수 있는지 확인한다.</summary>
    /// <exception cref="InvalidOperationException">버전 필드가 없는 프레이밍과 협상이 함께 조립됐다.</exception>
    /// <remarks>
    /// 협상 핸드셰이크 자체는 프레이밍 축을 타지 않아 동작하지만, 결과가 실릴 버전 필드가
    /// 없으면 협상은 아무것도 바꾸지 못한다 — 성립하지 않는 조립이다. 이 검사는
    /// <c>VersionNegotiationOptions</c> 문서가 "Core 계약에 표면이 없어 불가능"이라고
    /// 보류했던 바로 그 검증이다(감사 2026-08-18 H-8).
    /// </remarks>
    public static void EnsureCodecSupportsVersionNegotiation(IFrameEncoder encoder, IFrameDecoder decoder)
    {
        if ((encoder.Capabilities & FrameCodecCapabilities.ProtocolVersion) == 0
            || (decoder.Capabilities & FrameCodecCapabilities.ProtocolVersion) == 0)
        {
            throw new InvalidOperationException(
                "버전 협상(UseVersionNegotiation)이 조립됐지만 프레이밍에 버전 필드가 없다 " +
                $"(인코더 {encoder.Capabilities}, 디코더 {decoder.Capabilities}). " +
                "협상 결과가 와이어 어디에도 반영되지 않는 조립이다 — " +
                "고정 헤더 프레이밍을 쓰거나 협상을 뺀다.");
        }
    }
}
