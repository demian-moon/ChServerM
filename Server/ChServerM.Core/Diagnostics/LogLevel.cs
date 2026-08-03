namespace ChServerM.Diagnostics;

/// <summary>
/// 로그 심각도.
/// </summary>
/// <remarks>
/// <para>
/// 값과 순서를 <c>Microsoft.Extensions.Logging.LogLevel</c>과 <b>의도적으로 일치</b>시켰다.
/// 어댑터가 캐스팅 한 번으로 변환할 수 있어야 하기 때문이다. Core는 그 패키지를 참조하지 않는다
/// (무의존 하드 룰).
/// </para>
/// <para>
/// 레거시에는 <b>레벨 개념 자체가 없었다.</b> 모든 로그가 같은 경로로 나갔고,
/// 설정 파일이 없으면 로깅이 통째로 조용히 꺼졌다. 그래서 이 enum은 선택이 아니라
/// <see cref="IServerLogger"/>의 <b>필수 인자</b>다.
/// </para>
/// </remarks>
public enum LogLevel
{
    /// <summary>가장 상세한 진단. 운영에서는 켜지 않는다.</summary>
    Trace = 0,

    /// <summary>개발 중 흐름 추적.</summary>
    Debug = 1,

    /// <summary>정상 동작의 이정표. 시작·종료·바인딩 등.</summary>
    Information = 2,

    /// <summary>비정상이지만 계속 진행 가능. <b>버려진 작업은 여기 이상이어야 한다.</b></summary>
    Warning = 3,

    /// <summary>현재 작업이 실패했다. 프로세스는 계속 산다.</summary>
    Error = 4,

    /// <summary>프로세스를 계속 둘 수 없다.</summary>
    Critical = 5,

    /// <summary>아무것도 기록하지 않는다. <see cref="IServerLogger.IsEnabled"/>의 인자로 쓰지 않는다.</summary>
    None = 6,
}
