namespace ChServerM.Diagnostics;

/// <summary>
/// <c>ActivitySource</c>·<c>Meter</c> 이름과 공통 접두사.
/// </summary>
/// <remarks>
/// <para>
/// 이름은 <b>대시보드와 알람 규칙의 계약</b>이다. 문자열 리터럴이 여기저기 흩어지면
/// 오타 하나가 조용히 메트릭을 사라지게 만든다. 상수로 모아 컴파일러가 검사하게 한다.
/// </para>
/// <para>
/// 명명 규칙은 OpenTelemetry 관례를 따른다 — 소문자, <c>.</c> 구분.
/// </para>
/// <para>레거시는 메트릭이 <b>하나도 없었다.</b> 그래서 조용한 실패를 아무도 몰랐다.</para>
/// </remarks>
/// <seealso cref="MetricNames" />
/// <seealso cref="TagNames" />
/// <seealso cref="ActivityNames" />
public static class DiagnosticNames
{
    /// <summary>모든 메트릭 이름의 공통 접두사.</summary>
    // "chserverm" — 프로젝트명(ChServerM) 소문자다. 한 글자가 빠진 오타("chservem")로
    // 릴리스되면 대시보드·알람 규칙에 영구 계약으로 굳는다(2026-08-04 감사에서 발견·정정).
    public const string Prefix = "chserverm";

    /// <summary>분산 추적 <c>ActivitySource</c> 이름.</summary>
    public const string ActivitySourceName = "ChServerM";

    /// <summary>계측 <c>Meter</c> 이름.</summary>
    public const string MeterName = "ChServerM";
}

/// <summary>프레임워크가 발행하는 메트릭 이름.</summary>
public static class MetricNames
{
    /// <summary>현재 열려 있는 커넥션 수.</summary>
    public const string ConnectionsActive = DiagnosticNames.Prefix + ".connections.active";

    /// <summary>수용한 누적 커넥션 수.</summary>
    public const string ConnectionsAccepted = DiagnosticNames.Prefix + ".connections.accepted";

    /// <summary>거부한 누적 커넥션 수. <see cref="TagNames.CloseReason"/>으로 분류한다.</summary>
    public const string ConnectionsRejected = DiagnosticNames.Prefix + ".connections.rejected";

    /// <summary>수신 프레임 수.</summary>
    public const string FramesReceived = DiagnosticNames.Prefix + ".frames.received";

    /// <summary>송신 프레임 수.</summary>
    public const string FramesSent = DiagnosticNames.Prefix + ".frames.sent";

    /// <summary>디코딩에 실패한 프레임 수. <see cref="TagNames.ErrorCode"/>로 분류한다.</summary>
    public const string FramesDropped = DiagnosticNames.Prefix + ".frames.dropped";

    /// <summary>수신 바이트.</summary>
    public const string BytesReceived = DiagnosticNames.Prefix + ".bytes.received";

    /// <summary>송신 바이트.</summary>
    public const string BytesSent = DiagnosticNames.Prefix + ".bytes.sent";

    /// <summary>메시지 처리 지연(초).</summary>
    public const string DispatchDuration = DiagnosticNames.Prefix + ".dispatch.duration";

    /// <summary>핸들러 실패 수.</summary>
    public const string DispatchFailures = DiagnosticNames.Prefix + ".dispatch.failures";

    /// <summary>파티션 큐에 쌓인 작업 수.</summary>
    public const string PartitionQueueDepth = DiagnosticNames.Prefix + ".partition.queue.depth";

    /// <summary>큐 포화로 거부한 작업 수. <b>이 값이 0이 아니면 용량이 부족한 것이다.</b></summary>
    public const string PartitionWorkRejected = DiagnosticNames.Prefix + ".partition.work.rejected";

    /// <summary>백프레셔로 대기한 시간(초).</summary>
    public const string BackpressureDuration = DiagnosticNames.Prefix + ".backpressure.duration";
}

/// <summary>메트릭·추적에 붙이는 태그 이름.</summary>
/// <remarks>
/// <b>카디널리티에 주의한다.</b> 커넥션 ID·세션 ID처럼 값이 무한한 것은 태그로 쓰지 않는다.
/// 시계열이 폭발한다. 그런 값은 추적(span) 속성으로만 남긴다.
/// </remarks>
public static class TagNames
{
    /// <summary>전송 종류(<c>tcp</c>, <c>inmemory</c> 등).</summary>
    public const string Transport = "transport";

    /// <summary>메시지 식별자. 앱이 정의하는 값이 유한할 때만 쓴다.</summary>
    public const string MessageId = "message_id";

    /// <summary>커넥션 식별자.</summary>
    /// <remarks>
    /// <b>추적 span 속성 전용이다 — 메트릭 태그로 쓰지 않는다.</b> 커넥션 ID 는 값이
    /// 무한(연결마다 새로 생성)해 메트릭 태그로 쓰면 시계열이 폭발한다(이 클래스 규약).
    /// span 은 요청 단위라 고카디널리티 식별자를 담아도 안전하며, 오히려 트레이스끼리
    /// 상관(correlation)시키는 핵심 축이다.
    /// </remarks>
    public const string ConnectionId = "connection_id";

    /// <summary><see cref="Diagnostics.ErrorCode"/> 값.</summary>
    public const string ErrorCode = "error_code";

    /// <summary>커넥션 종료 사유.</summary>
    public const string CloseReason = "close_reason";

    /// <summary>실행 파티션 인덱스.</summary>
    public const string Partition = "partition";
}

/// <summary>추적 span 이름.</summary>
public static class ActivityNames
{
    /// <summary>커넥션 하나의 전 생애.</summary>
    public const string Connection = DiagnosticNames.Prefix + ".connection";

    /// <summary>메시지 하나의 디스패치.</summary>
    public const string Dispatch = DiagnosticNames.Prefix + ".dispatch";
}
