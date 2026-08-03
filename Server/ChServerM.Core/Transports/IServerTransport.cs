using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;

namespace ChServerM.Transports;

/// <summary>
/// 들어오는 커넥션을 수용하는 전송.
/// </summary>
/// <remarks>
/// <para>
/// <b>종료가 3단계인 것이 이 인터페이스의 요점이다.</b>
/// </para>
/// <list type="number">
///   <item><description><see cref="BindAsync"/> — 수용 시작</description></item>
///   <item><description><see cref="UnbindAsync"/> — <b>신규 수용만</b> 중단. 기존 커넥션은 계속 산다</description></item>
///   <item><description><see cref="StopAsync"/> — 기존 커넥션을 드레인하고 정리</description></item>
/// </list>
/// <para>
/// 2와 3 사이가 <b>무중단 배포의 창</b>이다. 로드밸런서가 새 트래픽을 다른 노드로
/// 돌리는 동안 이미 붙어 있는 클라이언트는 하던 일을 끝낸다. 레거시에는 이 단계가 없어서
/// 종료가 곧 전원 차단이었다.
/// </para>
/// <para>
/// 구현체는 <b>워크로드를 알지 못한다.</b> TCP든 인메모리 루프백이든 같은 계약이고,
/// 그래서 <c>.UseTcp()</c> ↔ <c>.UseInMemory()</c> 교체가 성립한다(ADR-0004).
/// </para>
/// </remarks>
public interface IServerTransport : IAsyncDisposable
{
    /// <summary>실제로 바인드된 주소. 바인드 전이면 <see langword="null"/>.</summary>
    /// <remarks>
    /// 포트 0으로 바인드하면 여기서 <b>실제 배정된 포트</b>를 읽는다.
    /// 테스트가 포트를 하드코딩하지 않아도 되는 이유다.
    /// </remarks>
    EndPoint? LocalEndPoint { get; }

    /// <summary>수용을 시작한다.</summary>
    /// <param name="handler">수용된 커넥션을 처리할 핸들러.</param>
    /// <param name="cancellationToken">바인드 작업 자체의 취소 토큰.</param>
    /// <returns>바인드가 끝나면 완료되는 작업. <b>수용 루프의 종료를 기다리지 않는다.</b></returns>
    /// <exception cref="InvalidOperationException">이미 바인드돼 있을 때.</exception>
    /// <remarks>
    /// 바인드 실패는 <b>예외</b>다. 시작 시점 실패이므로 핫패스가 아니고,
    /// 조용히 넘어가면 "떠 있는데 아무도 못 붙는 서버"가 된다.
    /// </remarks>
    ValueTask BindAsync(IConnectionHandler handler, CancellationToken cancellationToken = default);

    /// <summary>신규 수용을 중단한다. 기존 커넥션은 유지한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <remarks>바인드되지 않은 상태에서 호출해도 아무 일도 일어나지 않는다.</remarks>
    ValueTask UnbindAsync(CancellationToken cancellationToken = default);

    /// <summary>남은 커넥션을 드레인하고 전송을 정리한다.</summary>
    /// <param name="cancellationToken">
    /// 드레인 제한 시간. 취소되면 남은 커넥션을 <see cref="IConnection.Abort"/>로 끊는다.
    /// </param>
    /// <remarks>
    /// <see cref="UnbindAsync"/>를 먼저 부르지 않았다면 내부적으로 먼저 수행한다.
    /// <b>드레인에 무한정 기다리지 않는다</b> — 상한 없는 대기는 종료를 영원히 막는다.
    /// </remarks>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
