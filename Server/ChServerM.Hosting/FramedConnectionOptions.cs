using System;

namespace ChServerM.Hosting;

/// <summary>
/// 프레임 읽기 루프의 정책.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "이 실패에 커넥션을 닫을 것인가"는 <b>워크로드마다 답이 다르다.</b>
/// 엄격한 사내 프로토콜은 모르는 메시지에 즉시 끊는 것이 옳고, 여러 버전의 클라이언트가
/// 붙는 공개 서비스는 무시하고 계속하는 것이 옳다. 프레임워크가 한쪽을 강제하면
/// 다른 쪽은 이 계층을 통째로 다시 만들어야 한다(ADR-0004).
/// </para>
/// <para>
/// <b>기본값은 "구조적 실패는 닫고, 애플리케이션 실패는 계속"이다.</b>
/// 프레임 경계를 신뢰할 수 없게 된 상황(프로토콜 오류)은 닫는 것 말고 방법이 없다.
/// 반면 핸들러 하나가 예외를 던진 것으로 멀쩡한 후속 메시지까지 잃을 이유는 없다.
/// </para>
/// <para>
/// <b>무엇을 고르든 실패는 기록된다.</b> 닫지 않기로 한 실패가 조용해지면
/// 레거시와 같은 상태가 된다 — 관측되지 않는 유실.
/// </para>
/// </remarks>
public sealed class FramedConnectionOptions
{
    /// <summary>등록되지 않은 메시지 식별자를 받으면 커넥션을 닫는다.</summary>
    /// <remarks>
    /// 기본값 <see langword="false"/>. 구버전 클라이언트가 모르는 메시지를 보내는 것은
    /// 흔한 일이고, 그때마다 끊으면 롤링 배포가 불가능하다.
    /// 엄격한 프로토콜이라면 <see langword="true"/>로 켠다.
    /// </remarks>
    public bool CloseOnHandlerNotFound { get; set; }

    /// <summary>페이로드 역직렬화에 실패하면 커넥션을 닫는다.</summary>
    /// <remarks>
    /// 기본값 <see langword="true"/>. 길이와 식별자는 맞는데 내용을 읽을 수 없다는 것은
    /// <b>양쪽 스키마가 어긋났거나 조작된 입력</b>이라는 뜻이다. 둘 다 계속할 이유가 없다.
    /// </remarks>
    public bool CloseOnDeserializationFailure { get; set; } = true;

    /// <summary>미들웨어가 정책으로 거부하면 커넥션을 닫는다.</summary>
    /// <remarks>
    /// 기본값 <see langword="false"/>. 속도 제한에 걸린 요청 하나가 커넥션을 끊으면
    /// 정상 사용자가 재접속 폭풍을 만든다. 인증 실패처럼 즉시 끊어야 하는 경우는
    /// 미들웨어가 직접 <see cref="Connections.IConnection.Abort"/>를 부른다.
    /// </remarks>
    public bool CloseOnPolicyRejection { get; set; }

    /// <summary>핸들러가 예외를 던지면 커넥션을 닫는다.</summary>
    /// <remarks>
    /// 기본값 <see langword="false"/>. 애플리케이션 버그로 커넥션을 끊으면 장애가 증폭된다.
    /// 예외는 항상 기록되므로 조용히 사라지지는 않는다.
    /// </remarks>
    public bool CloseOnHandlerFault { get; set; }

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    /// <remarks>
    /// 지금은 검증할 조합이 없다. 옵션이 늘어날 때를 위한 자리이며,
    /// 호출부(<see cref="FramedConnectionHandler"/>)가 이미 부르고 있으므로
    /// 나중에 규칙을 추가해도 호출 지점을 찾아다닐 필요가 없다.
    /// </remarks>
#pragma warning disable CA1822 // static 으로 바꾸면 옵션 검증의 호출 형태가 달라진다 — 아래 주석 참조.
    public void Validate()
    {
        // 의도적으로 비어 있다.
        // static 으로 바꾸라는 분석기 제안을 따르지 않는 이유: 이 메서드는 다른 Options 타입
        // (FramingOptions, InMemoryTransportOptions)과 같은 인스턴스 메서드 형태여야 한다.
        // 나중에 검증 규칙이 생겼을 때 호출부 시그니처가 바뀌면, 그 시점에 모든 호출 지점을
        // 찾아다녀야 한다. 지금 형태를 고정해 두는 편이 싸다.
    }
#pragma warning restore CA1822
}
