using System;

namespace ChServerM.Hosting;

/// <summary>무중단 배포에서 이 노드를 트래픽에서 빼는 절차의 설정.</summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 순서와 <i>간격</i>이 이 절차의 전부다.</b> 종료 자체는
/// <see cref="ChServerMServer.UnbindAsync"/> 와 <see cref="ChServerMServer.StopAsync"/> 가
/// 이미 한다. 여기가 더하는 것은 그 사이의 <b>기다림</b>이고, 그것이 빠졌을 때의 증상이
/// 정확히 "무중단 배포인데 오류가 난다" 다.
/// </para>
/// </remarks>
public sealed class DrainOptions
{
    /// <summary>
    /// readiness 를 내린 뒤 <b>수용을 멈추기 전에</b> 기다리는 시간. 기본 5초.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ 이 간격이 무중단 배포의 핵심이다.</b> readiness 를 내려도 로드밸런서·
    /// kube-proxy·서비스 메시가 그것을 알아채기까지 시간이 걸린다(프로브 주기 + 전파).
    /// <b>그 사이에 도착한 접속은 이미 닫힌 수락 소켓을 만나 RST 를 받는다</b> —
    /// 다른 노드로 라우팅되는 것이 아니라 <b>실패</b>한다.
    /// </para>
    /// <para>
    /// <b>0 으로 두면 그 창이 그대로 열린다.</b> 값은 배포 환경이 정한다 —
    /// K8s 기본 readiness 주기가 10초면 그보다 넉넉해야 한다. <b>프레임워크는 그 값을
    /// 알 수 없으므로 짐작하지 않고, 기본 5초는 "생각해 보라" 는 뜻의 안전한 출발점이다.</b>
    /// </para>
    /// </remarks>
    public TimeSpan ReadinessPropagationDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 수용을 멈춘 뒤 기존 커넥션이 끝나기를 기다리는 상한. 기본 30초.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ 이 값은 오케스트레이터의 종료 유예보다 <i>짧아야</i> 한다.</b>
    /// K8s <c>terminationGracePeriodSeconds</c>(기본 30초) 안에
    /// <see cref="ReadinessPropagationDelay"/> + 이 값 + 나머지 정리가 모두 들어가야 한다.
    /// 넘치면 <b>드레인 도중에 SIGKILL 이 온다</b> — 드레인을 길게 잡을수록 안전해지는 것이
    /// 아니라, 어느 지점을 넘으면 <b>드레인이 아예 없는 것보다 나빠진다</b>(중간에 잘리므로).
    /// </para>
    /// <para>
    /// <b>⚠ 긴 수명 커넥션은 스스로 끝나지 않는다.</b> 상태 유지 프로필(실시간 TCP)에서는
    /// 이 상한을 <b>항상</b> 치고 강제 종료로 끝난다 — 그것이 정상이다.
    /// 클라이언트에게 "다른 노드로 옮겨 가라" 고 말하는 것은 <b>프로토콜 결정</b>이므로
    /// 앱의 몫이고, 그 통지를 보내려면 이 드레인을 시작하기 <b>전에</b> 보내야 한다.
    /// </para>
    /// </remarks>
    public TimeSpan ConnectionDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 성립하지 않는다.</exception>
    public void Validate()
    {
        if (ReadinessPropagationDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(ReadinessPropagationDelay)} 는 음수일 수 없다.");
        }

        if (ConnectionDrainTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(ConnectionDrainTimeout)} 는 음수일 수 없다.");
        }
    }
}

/// <summary>드레인이 어떻게 끝났는가.</summary>
/// <remarks>
/// <b>존재 이유 — 배포 파이프라인이 알아야 하는 것은 "끝났다" 가 아니라 "깨끗이 끝났는가" 다.</b>
/// 강제 종료가 있었다면 그 배포에서 사용자 요청이 잘렸다는 뜻이고, 그것이 매번 일어난다면
/// <see cref="DrainOptions.ConnectionDrainTimeout"/> 이 짧거나 앱이 커넥션을 놓아주지 않는
/// 것이다. 이 값을 돌려주지 않으면 <b>둘 다 조용히 지나간다</b>.
/// </remarks>
public readonly struct DrainReport : IEquatable<DrainReport>
{
    internal DrainReport(TimeSpan elapsed, bool completedWithinTimeout)
    {
        Elapsed = elapsed;
        CompletedWithinTimeout = completedWithinTimeout;
    }

    /// <summary>readiness 를 내린 시점부터 정지까지 걸린 시간(전파 대기 포함).</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// 커넥션이 <b>상한 안에 스스로</b> 끝났는가. <see langword="false"/> 면 강제 종료가 있었다.
    /// </summary>
    /// <remarks>
    /// <b>⚠ 이것은 관측이 아니라 판정이다.</b> 전송 축은 "몇 개를 강제로 끊었는가" 를
    /// 돌려주지 않으므로(<c>IServerTransport.StopAsync</c> 의 표면에 없다), 상한이
    /// 소진됐는지로 가른다. <b>몇 개였는지는 알 수 없고, 안다고 적지 않는다.</b>
    /// </remarks>
    public bool CompletedWithinTimeout { get; }

    /// <inheritdoc/>
    public bool Equals(DrainReport other) =>
        Elapsed == other.Elapsed && CompletedWithinTimeout == other.CompletedWithinTimeout;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DrainReport other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Elapsed, CompletedWithinTimeout);

    /// <inheritdoc/>
    public override string ToString() =>
        $"Drain({Elapsed.TotalSeconds:F1}s, {(CompletedWithinTimeout ? "clean" : "forced")})";

    /// <summary>두 보고가 같은가.</summary>
    /// <param name="left">왼쪽.</param>
    /// <param name="right">오른쪽.</param>
    /// <returns>같으면 <see langword="true"/>.</returns>
    public static bool operator ==(DrainReport left, DrainReport right) => left.Equals(right);

    /// <summary>두 보고가 다른가.</summary>
    /// <param name="left">왼쪽.</param>
    /// <param name="right">오른쪽.</param>
    /// <returns>다르면 <see langword="true"/>.</returns>
    public static bool operator !=(DrainReport left, DrainReport right) => !left.Equals(right);
}
