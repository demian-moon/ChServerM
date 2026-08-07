using System;
using System.Net;
using ChServerM.Resilience;

namespace ChServerM.Hosting;

/// <summary>
/// 부하 수준이 임계에 도달하면 신규 연결을 거부하는 <see cref="IAdmissionControl"/> (Phase 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이미 붙은 커넥션을 보호한다.</b> 메모리·자원 압박이 한계에 이르면, 새 손님을
/// 계속 받는 것은 <b>이미 서비스 중인 손님까지 함께 죽이는 길</b>이다. 이 규칙은 그 지점에서
/// 신규 수용을 끊어 남은 자원을 기존 커넥션에 남긴다("거부가 붕괴보다 낫다", CLAUDE.md 9.6).
/// </para>
/// <para>
/// <b>부하 소스는 축이다.</b> <see cref="ILoadLevelSource"/> 를 받으므로 메모리
/// (<see cref="MemoryLoadLevelSource"/>)뿐 아니라 큐 깊이·지연·외부 오케스트레이터 신호 등
/// 어떤 근거로도 같은 자리에 꽂힌다. 열화 미들웨어와 <b>같은 신호를 공유</b>하는 것이 요점이다 —
/// 두 방어가 서로 다른 기준으로 움직이면 운영자가 상태를 설명할 수 없다.
/// </para>
/// <para>
/// <b>⚠ 열화가 먼저, 수용 거부가 나중이다 — 이 순서가 설계의 핵심.</b> 기본 임계를
/// <see cref="LoadLevel.Critical"/> 로 둔 이유다.
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="LoadLevel.Elevated"/> — <b>열화</b>가 비필수 메시지를 버려 부하를 낮춘다
///     (기존 커넥션은 계속 서비스된다).
///   </description></item>
///   <item><description>
///     <see cref="LoadLevel.Critical"/> — 그래도 부족하면 <b>신규 수용을 끊는다</b>.
///   </description></item>
/// </list>
/// <para>
/// 이 순서를 뒤집으면(Elevated 에서 이미 연결을 거부) 조금만 밀려도 신규 사용자가 못 들어오고,
/// 그 재시도가 accept 부하를 더한다. <b>먼저 덜 중요한 일을 버리고, 그래도 안 되면 문을 닫는다.</b>
/// </para>
/// <para>
/// <b>거부는 값싸다.</b> 부하 조회는 소스가 캐시하므로(<see cref="MemoryLoadLevelSource"/> 는
/// 기본 1초) 커넥션당 비용이 사실상 없다.
/// </para>
/// <para>
/// <b>주소를 보지 않는다.</b> 이것은 서버 자신의 상태에 대한 판정이라 원격 주소가 무관하다 —
/// 주소별 제한은 <see cref="PerAddressConnectionRateAdmissionControl"/> 의 몫이고, 둘은
/// <see cref="CompositeAdmissionControl"/> 로 AND 결합한다.
/// </para>
/// <para><b>스레드 규약.</b> 소스가 스레드 안전하면(계약) 이 규칙도 안전하다. 자체 상태가 없다.</para>
/// </remarks>
public sealed class LoadLevelAdmissionControl : IAdmissionControl
{
    private readonly ILoadLevelSource _loadLevel;
    private readonly LoadLevel _rejectAtOrAbove;

    /// <summary>부하 소스와 임계로 규칙을 만든다.</summary>
    /// <param name="loadLevel">현재 부하를 알려주는 소스. 열화 미들웨어와 같은 인스턴스를 쓰는 것이 의도된 사용법이다.</param>
    /// <param name="rejectAtOrAbove">
    /// 이 수준 <b>이상</b>이면 신규 연결을 거부한다. 기본 <see cref="LoadLevel.Critical"/> —
    /// 그 근거는 타입 문서의 "열화가 먼저" 절.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="loadLevel"/>이 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="rejectAtOrAbove"/>가 <see cref="LoadLevel.Normal"/>일 때.</exception>
    public LoadLevelAdmissionControl(
        ILoadLevelSource loadLevel,
        LoadLevel rejectAtOrAbove = LoadLevel.Critical)
    {
        ArgumentNullException.ThrowIfNull(loadLevel);

        if (rejectAtOrAbove == LoadLevel.Normal)
        {
            // Normal 에서 거부하면 평상시에 모든 연결이 막힌다 — 조립 실수다.
            throw new InvalidOperationException(
                $"{nameof(rejectAtOrAbove)} 를 {nameof(LoadLevel.Normal)} 로 두면 평상시에도 모든 연결을 거부한다. " +
                $"{nameof(LoadLevel.Elevated)} 이상을 쓴다.");
        }

        _loadLevel = loadLevel;
        _rejectAtOrAbove = rejectAtOrAbove;
    }

    /// <inheritdoc />
    public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint)
    {
        LoadLevel current = _loadLevel.Current;

        if (current < _rejectAtOrAbove)
        {
            return AdmissionDecision.Admit();
        }

        // 사유에 부하 수준을 담는다 — 값이 셋뿐이라 카디널리티가 안전하고(TagNames 규약),
        // "왜 거부됐는가"가 로그에서 바로 읽힌다.
        return AdmissionDecision.Reject($"load level {current}");
    }
}
