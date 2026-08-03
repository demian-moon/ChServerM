namespace ChServerM.Connections;

/// <summary>
/// 커넥션이 닫힌 이유의 대분류.
/// </summary>
/// <remarks>
/// <para>
/// 세부 원인은 <see cref="ChServerM.Diagnostics.ErrorCode"/>가 담는다. 이쪽은
/// <b>메트릭 태그로 쓸 수 있을 만큼 카디널리티가 낮은</b> 분류다.
/// </para>
/// <para>
/// "정상 종료"와 "사고"를 값으로 구분하는 것이 핵심이다. 레거시는 둘을 구분하지 않아
/// 대시보드에서 <b>서버 배포로 인한 대량 종료</b>와 <b>네트워크 장애</b>가 같은 그래프에 섞였다.
/// </para>
/// </remarks>
public enum CloseReason : byte
{
    /// <summary>설정되지 않았다. 이 값이 로그에 보이면 종료 경로가 정보를 채우지 않은 것이다.</summary>
    None = 0,

    /// <summary>상대가 정상적으로 닫았다.</summary>
    ClientClosed = 1,

    /// <summary>애플리케이션 요청으로 우리가 정상적으로 닫았다.</summary>
    ServerClosed = 2,

    /// <summary>서버 종료 절차에 따라 드레인 후 닫았다.</summary>
    ShuttingDown = 3,

    /// <summary>제한 시간 안에 활동이 없었다(하트비트·유휴·핸드셰이크).</summary>
    Timeout = 4,

    /// <summary>프레임이 규격을 위반했다. <b>파싱을 계속하지 않고 닫는다.</b></summary>
    ProtocolError = 5,

    /// <summary>애플리케이션 정책 위반(인증 실패, 권한 없음, 속도 제한).</summary>
    ApplicationError = 6,

    /// <summary>자원 상한에 걸렸다(동시 접속, 큐 포화, 버퍼 한계).</summary>
    ResourceLimit = 7,

    /// <summary>전송 계층에서 사고가 났다(RST, 소켓 오류).</summary>
    TransportError = 8,
}
