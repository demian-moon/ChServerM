using System;

namespace ChServerM.Framing;

/// <summary>
/// 프레이밍 코덱이 와이어에 실을 수 있는 논리 필드의 집합.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 프레이밍은 교체 가능한 축이고 와이어 포맷은 구현이 소유한다(ADR-0010).
/// 그 결과 어떤 축 조합은 <b>구조적으로 성립하지 않는다</b> — 압축 코덱은
/// <see cref="MessageEnvelope.Flags"/> 를 와이어에 실을 수 있는 프레이밍을 요구하고
/// (수신 측이 플래그를 못 보면 해제가 영영 발동하지 않는다), 버전 협상은 협상 결과를
/// 실을 버전 필드를 요구한다. 이 표면이 없으면 그 불성립이 조립 시점에 잡히지 않고
/// 런타임 예외(송신)나 조용한 무동작(수신)으로만 드러난다 — "조립 시점 실패가 런타임
/// 디버깅보다 싸다"는 원칙의 마지막 구멍이었다(감사 2026-08-18 H-8, 결정: capabilities 추가).
/// </para>
/// <para>
/// <b>capabilities 는 선언이지 검증이 아니다.</b> 인코더는 여전히 표현 불가 값을 받으면
/// 예외를 던진다(ADR-0010 — 조용히 버리지 않는다). 이 열거는 그 예외를 조립 시점
/// 검사(<c>CompositionGuard</c>)로 앞당기는 근거다.
/// </para>
/// </remarks>
[Flags]
public enum FrameCodecCapabilities
{
    /// <summary>길이·메시지 ID 외에 아무 논리 필드도 싣지 못한다.</summary>
    None = 0,

    /// <summary><see cref="MessageEnvelope.Flags"/> 를 와이어에 싣는다. 압축·조각화가 요구한다.</summary>
    Flags = 1 << 0,

    /// <summary><see cref="MessageEnvelope.Sequence"/> 를 와이어에 싣는다.</summary>
    Sequence = 1 << 1,

    /// <summary>프로토콜 버전 필드를 와이어에 싣는다. 버전 협상 결과가 반영될 자리다.</summary>
    ProtocolVersion = 1 << 2,
}
