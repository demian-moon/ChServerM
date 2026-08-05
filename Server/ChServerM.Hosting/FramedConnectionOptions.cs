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

    /// <summary>조각 재조립(<see cref="Framing.FrameFlags.Fragmented"/>) 후 논리 메시지의 최대 길이.</summary>
    /// <remarks>
    /// <para>
    /// 기본값 1 MiB. <b>0 이면 재조립을 끈다</b> — 조각 프레임을 받는 즉시 프로토콜 오류로
    /// 커넥션을 닫는다. 큰 메시지가 프로토콜에 없는 프로필(무상태 웹 등)은 끄는 것이
    /// 공격 표면을 줄인다.
    /// </para>
    /// <para>
    /// <b>이 상한이 방어선이다.</b> 마지막 조각을 보내지 않는 상대가 부분 메시지를
    /// 무한정 키우는 것을 여기서 끊는다(ADR-0015). 커넥션당 이 크기까지 버퍼가 자랄 수
    /// 있으므로 <c>MaxConnections × 이 값</c>이 최악 재조립 메모리다 — 상한을 올릴 때
    /// 그 곱을 계산해 볼 것.
    /// </para>
    /// </remarks>
    public int MaxAssembledMessageLength { get; set; } = 1024 * 1024;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (MaxAssembledMessageLength < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxAssembledMessageLength)} 는 0(재조립 끔) 이상이어야 한다: {MaxAssembledMessageLength}");
        }
    }
}
