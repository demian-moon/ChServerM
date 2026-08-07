namespace ChServerM.Diagnostics;

/// <summary>
/// 운영 중에 조회할 수 있는 진단 스냅샷을 내놓는 축 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 메트릭이 못 담는 것을 담는다.</b> 메트릭은 카디널리티 규약상 커넥션 ID·
/// 원격 주소처럼 값이 무한한 것을 태그로 쓰지 못한다(<see cref="TagNames"/>) — 쓰면 시계열이
/// 폭발한다. 그래서 "지금 어떤 커넥션이 몇 개 있고 어느 것이 오래 멈춰 있는가" 같은
/// <b>요청 시점의 고카디널리티 상세</b>는 메트릭으로 답할 수 없다. 이 계약이 그 여집합이다:
/// <b>주기적 시계열이 아니라 필요할 때 한 번 뜨는 스냅샷.</b>
/// </para>
/// <para>
/// <b>옵트인이다.</b> 전송·실행 모델 등 진단할 것이 있는 구현이 이 인터페이스를 구현하면
/// 호스팅이 자동으로 수집한다 — Core 의 축 계약(<c>IServerTransport</c>·<c>IExecutionModel</c>)에
/// 진단 멤버를 얹지 않는다(<see cref="IHealthCheck"/> 와 같은 규율, ADR-0023).
/// </para>
/// <para>
/// <b>⚠ 무엇을 내놓을지는 노출 위험을 보고 정한다.</b> 이 스냅샷은 admin 엔드포인트로
/// 나갈 수 있고 그곳은 대개 평문·무인증이다. <b>전체 목록을 그대로 쏟지 않는다</b> —
/// 1만 접속이면 응답이 MB 급이고 클라이언트 주소가 통째로 노출된다. 집계를 먼저 내고
/// 개별 항목은 <b>상한을 둬 표본만</b> 낸다(문제 있는 것부터).
/// </para>
/// <para>
/// <b>싸고 안전해야 한다.</b> 운영자가 장애 중에 부르는 경로다 — 잠그거나 오래 걸리면
/// 진단이 장애를 키운다. 이미 있는 값을 읽어 쓰고, 살아 있는 자료구조를 순회할 때는
/// 스냅샷이 정확하지 않을 수 있음을 전제한다(순회 중 항목이 들고 난다).
/// </para>
/// <para><b>스레드 규약.</b> 임의 스레드에서 호출된다 — 구현은 스레드 안전해야 한다.</para>
/// </remarks>
public interface IDiagnosticsSource
{
    /// <summary>스냅샷의 구역 이름(<c>transport</c>·<c>execution-model</c> 등).</summary>
    /// <remarks>출력에서 구역을 구분하는 키다. 조립 안에서 고유해야 오해가 없다.</remarks>
    string Name { get; }

    /// <summary>현재 상태를 수집기에 기록한다.</summary>
    /// <param name="writer">값을 받아 적는 수집기.</param>
    /// <remarks>예외를 던져도 된다 — 수집자가 잡아 그 구역만 실패로 표시한다(한 구역이 전체를 깨지 않는다).</remarks>
    void Collect(IDiagnosticsWriter writer);
}

/// <summary>
/// <see cref="IDiagnosticsSource"/> 가 값을 적어 넣는 수집기 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>평평한 키-값이다.</b> 트리·표를 표현하는 타입을 만들지 않는다 — 진단 출력은 사람이
/// <c>curl</c> 로 읽는 것이 1차 용도이고, 목록은 인덱스를 키에 담아
/// (<c>connection.0.id</c>) 표현하면 충분하다. 표현식이 늘어나는 것 자체가 비용이다.
/// </para>
/// <para><b>구현은 수집자가 제공한다.</b> 소스는 이 인터페이스를 구현하지 않는다.</para>
/// </remarks>
public interface IDiagnosticsWriter
{
    /// <summary>문자열 값을 적는다.</summary>
    /// <param name="key">항목 키. 구역 안에서 고유해야 한다.</param>
    /// <param name="value">값. <see langword="null"/>이면 빈 문자열로 기록된다.</param>
    void Write(string key, string? value);

    /// <summary>정수 값을 적는다.</summary>
    /// <param name="key">항목 키.</param>
    /// <param name="value">값.</param>
    void Write(string key, long value);
}
